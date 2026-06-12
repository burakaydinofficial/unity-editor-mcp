using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// EditMode smoke tests that exercise the Unity-independent Core *inside* the
    /// Unity editor (Mono / C# 8 / the com.unity.nuget.newtonsoft-json assembly).
    /// The same logic is covered far more thoroughly by `dotnet test`
    /// (dotnet/UnityEditorMCP.Core.Tests); this assembly's job is only to confirm
    /// Core compiles and runs under Unity's toolchain and that the generated
    /// CommandCatalog is valid in-editor. It does not touch the live transport.
    /// </summary>
    public class CoreSmokeTests
    {
        [Test]
        public void Dispatcher_Success_ProducesSuccessEnvelope()
        {
            var dispatcher = new CommandDispatcher();
            dispatcher.Register("ping", p => HandlerOutcome.Ok(new { pong = true }));
            var json = JObject.Parse(dispatcher.Dispatch(new CommandRequest { Id = "1", Type = "ping" }).ToJson());
            Assert.AreEqual("success", (string)json["status"]);
            Assert.IsTrue((bool)json["result"]["pong"]);
        }

        [Test]
        public void Dispatcher_Error_IsNotLaunderedAsSuccess()
        {
            var dispatcher = new CommandDispatcher();
            dispatcher.Register("boom", p => HandlerOutcome.Fail("nope", "VALIDATION_ERROR"));
            var json = JObject.Parse(dispatcher.Dispatch(new CommandRequest { Id = "2", Type = "boom" }).ToJson());
            Assert.AreEqual("error", (string)json["status"]);
            Assert.AreEqual("VALIDATION_ERROR", (string)json["code"]);
        }

        [Test]
        public void Dispatcher_UnknownCommand_IsUnknownCommand()
        {
            var dispatcher = new CommandDispatcher();
            var json = JObject.Parse(dispatcher.Dispatch(new CommandRequest { Id = "3", Type = "nope" }).ToJson());
            Assert.AreEqual("UNKNOWN_COMMAND", (string)json["code"]);
        }

        [Test]
        public void Framing_RoundTrips()
        {
            var framer = new MessageFramer();
            framer.Append(MessageFramer.Encode("hello"));
            Assert.IsTrue(framer.TryReadMessage(out var message));
            Assert.AreEqual("hello", message);
        }

        [Test]
        public void GeneratedCatalog_IsPopulated_AndConformanceHolds()
        {
            Assert.GreaterOrEqual(CommandCatalog.EditorCommands.Length, 60);
            CollectionAssert.Contains(CommandCatalog.EditorCommands, "create_gameobject");

            // Registering exactly the catalog's editor commands must conform.
            var dispatcher = new CommandDispatcher();
            foreach (var name in CommandCatalog.EditorCommands)
            {
                dispatcher.Register(name, p => HandlerOutcome.Ok(null));
            }
            var report = CatalogConformance.Check(CommandCatalog.EditorCommands, dispatcher);
            Assert.IsTrue(report.Ok, report.ToString());
        }

        [Test]
        public void Handshake_RoundTrips_WithGeneratedProtocolVersion()
        {
            var handshake = new Handshake
            {
                UnityVersion = "2020.3.0f1",
                ProjectPath = "C:/p",
                AvailableCommands = new[] { "ping" },
            };
            var back = Handshake.FromJson(handshake.ToJson());
            Assert.AreEqual("2020.3.0f1", back.UnityVersion);
            Assert.AreEqual(CommandCatalog.ProtocolVersion, back.ProtocolVersion);
        }
    }
}
