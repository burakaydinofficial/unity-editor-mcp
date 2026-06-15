using Newtonsoft.Json.Linq;
using UnityEditorMCP.Core;

namespace UnityEditorMCP.Tests
{
    /// <summary>
    /// Test-only adapter that reconstructs the legacy { success, ...payload } / { success:false, error,
    /// code } JObject shape from a <see cref="HandlerOutcome"/>. The v0.4.0 dispatch-rail migration changed
    /// the editor handlers to return HandlerOutcome (a discriminated result that cannot serialize an error
    /// as a success); these handler unit tests still assert on the pre-migration JObject shape, so this
    /// adapter lets them do so without each test unpacking the outcome. NOTE: this is a TEST-CONVENIENCE
    /// shape, not the wire envelope — the dispatcher emits { id, status, result } and the Node client
    /// unwraps to the payload; here a successful outcome maps to success:true + the spread payload, and a
    /// failure to { success:false, error, code }, to satisfy the legacy assertions.
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
