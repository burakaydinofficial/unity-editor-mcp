using Newtonsoft.Json.Linq;

namespace UnityEditorMCP.Core
{
    /// <summary>
    /// Classifies whatever a legacy handler returned into a <see cref="HandlerOutcome"/>
    /// (the wire-truth decision: success payload vs. real error). Unity-independent and
    /// pure, so it is covered by fast <c>dotnet test</c> rather than only editor-bound
    /// tests — this is where the editor's <c>Response.Result</c> bug ("inline-payload
    /// success envelopes collapsed to {}") could and now does have direct coverage.
    ///
    /// The editor's Response.Result delegates here and then serializes the outcome with
    /// its existing SuccessResult/ErrorResult helpers, so the wire shape is unchanged.
    /// The error predicate mirrors the Node boundary's isHandlerLevelError
    /// (mcp-server/src/core/unityConnection.js) — keep the two in sync.
    /// </summary>
    public static class ResponseClassifier
    {
        public static HandlerOutcome Classify(object handlerResult)
        {
            // Two legacy handlers (ScriptHandler, TestRunnerHandler) return an ALREADY
            // -serialized envelope STRING. Reparse ONLY a string that looks like a JSON
            // object envelope (leading '{' AND carrying status/success/error), so genuine
            // string data — a serialized array, or a non-envelope object — keeps its type.
            if (handlerResult is string str)
            {
                if (str.TrimStart().StartsWith("{"))
                {
                    try
                    {
                        var parsed = JToken.Parse(str) as JObject;
                        if (parsed != null &&
                            (parsed["status"] != null || parsed["success"] != null || parsed["error"] != null))
                        {
                            handlerResult = parsed;
                        }
                    }
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

                // Error-shaped: { error: "..." } unless an explicit success:true overrides.
                if (!hasSuccessTrue &&
                    shape.TryGetValue("error", out var error) && error.Type == JTokenType.String)
                {
                    var code = shape.TryGetValue("code", out var codeToken) && codeToken.Type == JTokenType.String
                        ? (string)codeToken
                        : "EDITOR_ERROR";
                    // The original object rides along as details so no fields are lost.
                    return HandlerOutcome.Fail((string)error, code, details: shape);
                }

                // A legacy success envelope ({status:"success",…} / {success:true,…}).
                var isSuccessEnvelope = hasSuccessTrue ||
                    (shape.TryGetValue("status", out var status) &&
                     status.Type == JTokenType.String && (string)status == "success");
                if (isSuccessEnvelope)
                {
                    // Unwrap an EXPLICIT payload wrapper if present...
                    if (shape.TryGetValue("result", out var resultToken)) return HandlerOutcome.Ok(resultToken);
                    if (shape.TryGetValue("data", out var dataToken)) return HandlerOutcome.Ok(dataToken);
                    // ...otherwise the payload is INLINE ({ success:true, isCompiling:… } /
                    // { status:"success", state:… }); pass the whole object through so the
                    // fields survive (do NOT collapse to an empty success).
                }
            }

            return HandlerOutcome.Ok(handlerResult);
        }
    }
}
