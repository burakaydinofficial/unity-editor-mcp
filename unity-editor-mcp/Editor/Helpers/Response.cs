using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace UnityEditorMCP.Helpers
{
    /// <summary>
    /// Helper class for creating standardized response messages
    /// </summary>
    public static class Response
    {
        /// <summary>
        /// Creates a success response with optional data
        /// </summary>
        /// <param name="data">Optional data to include in the response</param>
        /// <returns>JSON string of the response</returns>
        public static string Success(object data = null)
        {
            var response = new Dictionary<string, object>
            {
                ["status"] = "success"
            };
            
            if (data != null)
            {
                response["data"] = data;
            }
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Creates a success response with command ID and optional data
        /// </summary>
        /// <param name="id">Command ID</param>
        /// <param name="data">Optional data to include in the response</param>
        /// <returns>JSON string of the response</returns>
        public static string Success(string id, object data = null)
        {
            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["success"] = true
            };
            
            if (data != null)
            {
                response["data"] = data;
            }
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Creates an error response with message and optional error code
        /// </summary>
        /// <param name="message">Error message</param>
        /// <param name="code">Optional error code</param>
        /// <param name="details">Optional additional error details</param>
        /// <returns>JSON string of the response</returns>
        public static string Error(string message, string code = null, object details = null)
        {
            var response = new Dictionary<string, object>
            {
                ["status"] = "error",
                ["error"] = message
            };
            
            if (!string.IsNullOrEmpty(code))
            {
                response["code"] = code;
            }
            
            if (details != null)
            {
                response["details"] = details;
            }
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Creates an error response with command ID
        /// </summary>
        /// <param name="id">Command ID</param>
        /// <param name="message">Error message</param>
        /// <param name="code">Optional error code</param>
        /// <param name="details">Optional additional error details</param>
        /// <returns>JSON string of the response</returns>
        public static string Error(string id, string message, string code = null, object details = null)
        {
            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["success"] = false,
                ["error"] = message
            };
            
            if (!string.IsNullOrEmpty(code))
            {
                response["code"] = code;
            }
            
            if (details != null)
            {
                response["details"] = details;
            }
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Creates a response for the ping command
        /// </summary>
        /// <returns>JSON string of the pong response</returns>
        public static string Pong()
        {
            return Success(new { message = "pong", timestamp = System.DateTime.UtcNow.ToString("o") });
        }
        
        // ===== New Format Methods (Phase 1.1) =====
        
        /// <summary>
        /// Creates a standardized success response (new format)
        /// </summary>
        /// <param name="result">The result data</param>
        /// <returns>JSON string of the response</returns>
        public static string SuccessResult(object result)
        {
            var response = new Dictionary<string, object>
            {
                ["status"] = "success",
                ["result"] = result
            };
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Creates a standardized success response with command ID (new format)
        /// </summary>
        /// <param name="id">Command ID</param>
        /// <param name="result">The result data</param>
        /// <returns>JSON string of the response</returns>
        public static string SuccessResult(string id, object result)
        {
            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["status"] = "success",
                ["result"] = result
            };
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Builds the wire envelope for a handler return value, classifying
        /// error-shaped results (<c>{ error: "...", ... }</c> without
        /// <c>success: true</c>) as real protocol errors instead of wrapping them
        /// in a success envelope. The predicate mirrors the Node boundary's
        /// <c>isHandlerLevelError</c> (mcp-server/src/core/unityConnection.js) —
        /// keep the two in sync. This is the editor-side wire-truth fix for the
        /// error-laundering deviation (protocol/README.md §Known deviations).
        /// </summary>
        /// <param name="id">Command ID</param>
        /// <param name="handlerResult">Whatever the handler returned</param>
        /// <returns>JSON string of the success or error envelope</returns>
        public static string Result(string id, object handlerResult)
        {
            // Most handlers return a plain object. Two legacy handlers (ScriptHandler,
            // TestRunnerHandler) instead return an ALREADY-serialized envelope string
            // from Response.Success/Response.Error; parse it back so it is classified
            // and unwrapped here rather than double-encoded under a success envelope.
            if (handlerResult is string str)
            {
                var trimmed = str.TrimStart();
                if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
                {
                    try { handlerResult = JToken.Parse(str); }
                    catch { /* not JSON — treat as an opaque string payload */ }
                }
            }

            JObject shape = null;
            if (handlerResult != null)
            {
                try
                {
                    var token = handlerResult as JToken ?? JToken.FromObject(handlerResult);
                    shape = token as JObject;
                    handlerResult = token;
                }
                catch
                {
                    // Not convertible — treat as an opaque success payload.
                }
            }

            if (shape != null)
            {
                var hasSuccessTrue = shape.TryGetValue("success", out var success) &&
                    success.Type == JTokenType.Boolean && (bool)success;

                // Error-shaped: { error: "..." } (object handlers) or a serialized
                // { status:"error", error, code } (legacy string handlers), unless an
                // explicit success:true overrides (mirrors Node isHandlerLevelError).
                if (!hasSuccessTrue &&
                    shape.TryGetValue("error", out var error) && error.Type == JTokenType.String)
                {
                    var code = shape.TryGetValue("code", out var codeToken) && codeToken.Type == JTokenType.String
                        ? (string)codeToken
                        : "EDITOR_ERROR";
                    // The original object rides along as details so no fields are lost.
                    return ErrorResult(id, (string)error, code, shape);
                }

                // A legacy success envelope ({status:"success",…} / {success:true,…}) —
                // unwrap its payload so it is not double-encoded under result.
                var isSuccessEnvelope = hasSuccessTrue ||
                    (shape.TryGetValue("status", out var status) &&
                     status.Type == JTokenType.String && (string)status == "success");
                if (isSuccessEnvelope)
                {
                    if (shape.TryGetValue("result", out var resultToken)) return SuccessResult(id, resultToken);
                    if (shape.TryGetValue("data", out var dataToken)) return SuccessResult(id, dataToken);
                }
            }

            return SuccessResult(id, handlerResult);
        }

        /// <summary>
        /// Creates a standardized error response (new format)
        /// </summary>
        /// <param name="errorMessage">Error message</param>
        /// <param name="code">Error code</param>
        /// <param name="details">Optional error details</param>
        /// <returns>JSON string of the response</returns>
        public static string ErrorResult(string errorMessage, string code = "UNKNOWN_ERROR", object details = null)
        {
            var response = new Dictionary<string, object>
            {
                ["status"] = "error",
                ["error"] = errorMessage,
                ["code"] = code
            };
            
            if (details != null)
            {
                response["details"] = details;
            }
            
            return JsonConvert.SerializeObject(response);
        }
        
        /// <summary>
        /// Creates a standardized error response with command ID (new format)
        /// </summary>
        /// <param name="id">Command ID</param>
        /// <param name="errorMessage">Error message</param>
        /// <param name="code">Error code</param>
        /// <param name="details">Optional error details</param>
        /// <returns>JSON string of the response</returns>
        public static string ErrorResult(string id, string errorMessage, string code = "UNKNOWN_ERROR", object details = null)
        {
            var response = new Dictionary<string, object>
            {
                ["id"] = id,
                ["status"] = "error",
                ["error"] = errorMessage,
                ["code"] = code
            };
            
            if (details != null)
            {
                response["details"] = details;
            }
            
            return JsonConvert.SerializeObject(response);
        }
    }
}