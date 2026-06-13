using NUnit.Framework;
using UnityEditorMCP.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace UnityEditorMCP.Tests.Helpers
{
    [TestFixture]
    public class ResponseTests
    {
        [Test]
        public void Success_ShouldReturnCorrectJsonWithoutData()
        {
            // Act
            var result = Response.Success();
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.IsFalse(json.ContainsKey("data"));
        }
        
        [Test]
        public void Success_ShouldReturnCorrectJsonWithData()
        {
            // Arrange
            var data = new { message = "test", value = 42 };
            
            // Act
            var result = Response.Success(data);
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.AreEqual("test", json["data"]["message"].Value<string>());
            Assert.AreEqual(42, json["data"]["value"].Value<int>());
        }
        
        [Test]
        public void Error_ShouldReturnCorrectJsonWithMessage()
        {
            // Act
            var result = Response.Error("Something went wrong");
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("error", json["status"].Value<string>());
            Assert.AreEqual("Something went wrong", json["error"].Value<string>());
            Assert.IsFalse(json.ContainsKey("code"));
            Assert.IsFalse(json.ContainsKey("details"));
        }
        
        [Test]
        public void Error_ShouldReturnCorrectJsonWithCode()
        {
            // Act
            // Named args disambiguate the (message, code) overload from (id, message).
            var result = Response.Error(message: "Connection failed", code: "CONN_001");
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("error", json["status"].Value<string>());
            Assert.AreEqual("Connection failed", json["error"].Value<string>());
            Assert.AreEqual("CONN_001", json["code"].Value<string>());
        }
        
        [Test]
        public void Error_ShouldReturnCorrectJsonWithDetails()
        {
            // Arrange
            var details = new { port = 6400, attempts = 3 };
            
            // Act
            var result = Response.Error("Connection failed", "CONN_001", details);
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("error", json["status"].Value<string>());
            Assert.AreEqual("Connection failed", json["error"].Value<string>());
            Assert.AreEqual("CONN_001", json["code"].Value<string>());
            Assert.AreEqual(6400, json["details"]["port"].Value<int>());
            Assert.AreEqual(3, json["details"]["attempts"].Value<int>());
        }
        
        [Test]
        public void Pong_ShouldReturnCorrectJson()
        {
            // Act
            var result = Response.Pong();
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.AreEqual("pong", json["data"]["message"].Value<string>());
            Assert.IsNotNull(json["data"]["timestamp"]);
            
            // Verify timestamp is valid ISO 8601
            var timestamp = json["data"]["timestamp"].Value<string>();
            Assert.DoesNotThrow(() => System.DateTime.Parse(timestamp));
        }
        
        [Test]
        public void Response_ShouldHandleNullData()
        {
            // Act
            var result = Response.Success(null);
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.IsFalse(json.ContainsKey("data"));
        }
        
        [Test]
        public void Result_SuccessPayload_ProducesSuccessEnvelope()
        {
            var json = JObject.Parse(Response.Result("42", new { name = "Cube", count = 3 }));
            Assert.AreEqual("42", json["id"].Value<string>());
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.AreEqual("Cube", json["result"]["name"].Value<string>());
        }

        [Test]
        public void Result_ErrorShapedPayload_IsNotLaunderedAsSuccess()
        {
            // The legacy handler convention: failures are { error: "..." } objects.
            var json = JObject.Parse(Response.Result("42", new { error = "GameObject not found" }));
            Assert.AreEqual("error", json["status"].Value<string>());
            Assert.AreEqual("GameObject not found", json["error"].Value<string>());
            Assert.AreEqual("EDITOR_ERROR", json["code"].Value<string>());
            Assert.IsNull(json["result"]);
        }

        [Test]
        public void Result_ErrorWithCode_PropagatesCode()
        {
            var json = JObject.Parse(Response.Result("42", new { error = "compiling", code = "EDITOR_COMPILING" }));
            Assert.AreEqual("EDITOR_COMPILING", json["code"].Value<string>());
            Assert.AreEqual("compiling", json["details"]["error"].Value<string>());
        }

        [Test]
        public void Result_SuccessTrueWithErrorField_StaysSuccess()
        {
            // Mirrors the Node isHandlerLevelError predicate: success:true wins.
            var json = JObject.Parse(Response.Result("42", new { success = true, error = "just a message field" }));
            Assert.AreEqual("success", json["status"].Value<string>());
        }

        [Test]
        public void Result_NonObjectPayloads_AreSuccess()
        {
            Assert.AreEqual("success", JObject.Parse(Response.Result("1", null))["status"].Value<string>());
            Assert.AreEqual("success", JObject.Parse(Response.Result("2", "plain string"))["status"].Value<string>());
            Assert.AreEqual("success", JObject.Parse(Response.Result("3", new[] { 1, 2, 3 }))["status"].Value<string>());
        }

        [Test]
        public void Result_JObjectErrorPayload_IsClassified()
        {
            var payload = new JObject { ["error"] = "boom", ["context"] = "x" };
            var json = JObject.Parse(Response.Result("42", payload));
            Assert.AreEqual("error", json["status"].Value<string>());
            Assert.AreEqual("x", json["details"]["context"].Value<string>());
        }

        [Test]
        public void Result_SerializedErrorEnvelopeString_IsNotDoubleEncoded()
        {
            // ScriptHandler/TestRunnerHandler return Response.Error(...) — a STRING.
            var serialized = Response.Error("Failed to validate script", "EDITOR_ERROR");
            var json = JObject.Parse(Response.Result("7", serialized));
            Assert.AreEqual("error", json["status"].Value<string>());
            Assert.AreEqual("Failed to validate script", json["error"].Value<string>());
            Assert.AreEqual("EDITOR_ERROR", json["code"].Value<string>());
            Assert.IsNull(json["result"], "must not wrap the error string under result");
        }

        [Test]
        public void Result_SerializedSuccessEnvelopeString_IsUnwrapped()
        {
            // Response.SuccessResult(id, data) -> {id,status:success,result:data} string.
            var serialized = Response.SuccessResult("ignored", new { scriptPath = "Assets/A.cs" });
            var json = JObject.Parse(Response.Result("7", serialized));
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.AreEqual("7", json["id"].Value<string>(), "the command id wins, not the inner envelope's");
            Assert.AreEqual("Assets/A.cs", json["result"]["scriptPath"].Value<string>());
        }

        [Test]
        public void Result_LegacySuccessWithDataField_IsUnwrapped()
        {
            // Response.Success(id, data) -> {id,success:true,data:data} string.
            var serialized = Response.Success("ignored", new { count = 5 });
            var json = JObject.Parse(Response.Result("8", serialized));
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.AreEqual(5, json["result"]["count"].Value<int>());
        }

        [Test]
        public void Result_NonJsonString_StaysOpaqueSuccessPayload()
        {
            var json = JObject.Parse(Response.Result("9", "{ not actually json"));
            Assert.AreEqual("success", json["status"].Value<string>());
            Assert.AreEqual("{ not actually json", json["result"].Value<string>());
        }

        [Test]
        public void Response_ShouldHandleComplexObjects()
        {
            // Arrange
            var complexData = new
            {
                gameObjects = new[] { "Player", "Enemy", "Ground" },
                settings = new Dictionary<string, object>
                {
                    ["difficulty"] = "hard",
                    ["level"] = 5
                }
            };
            
            // Act
            var result = Response.Success(complexData);
            var json = JObject.Parse(result);
            
            // Assert
            Assert.AreEqual(3, json["data"]["gameObjects"].Count());
            Assert.AreEqual("hard", json["data"]["settings"]["difficulty"].Value<string>());
            Assert.AreEqual(5, json["data"]["settings"]["level"].Value<int>());
        }
    }
}