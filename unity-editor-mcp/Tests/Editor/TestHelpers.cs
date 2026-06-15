using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Test-only adapter that reconstructs the legacy { success, ...payload } / { success:false, error,
    /// code } JObject shape from a <see cref="HandlerOutcome"/>. The v0.4.0 dispatch-rail migration changed
    /// the editor handlers to return HandlerOutcome (a discriminated result that cannot serialize an error
    /// as a success); these handler unit tests still assert on the pre-migration JObject shape, so this
    /// adapter lets them do so without each test unpacking the outcome. It mirrors how the dispatcher
    /// envelopes an outcome on the wire (success spreads the payload; failure carries error + code).
    /// </summary>
    public static class TestHelpers
    {
        public static JObject Result(HandlerOutcome outcome)
        {
            if (outcome.IsError)
            {
                var err = new JObject { ["success"] = false, ["error"] = outcome.Error };
                if (outcome.Code != null) err["code"] = outcome.Code;
                if (outcome.Details != null) err["details"] = JToken.FromObject(outcome.Details);
                return err;
            }
            var ok = outcome.Payload != null ? JObject.FromObject(outcome.Payload) : new JObject();
            ok["success"] = true;
            return ok;
        }
    }
}
