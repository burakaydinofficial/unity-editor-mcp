# Structural Quality Roadmap

Remaining structural-quality work for the Unity Editor MCP bridge, beyond the protocol-contract
foundation. Each finding was produced by a read-only audit agent (the `protocol-enrichment`
workflow) and is grounded in `file:line` evidence — a verified backlog, not yet implemented.
These are refactors of existing behavior, **not new features**. Sequence them behind the
contract: the catalog + drift gate is the spine; these bring the implementation up to it.

**Totals:** 38 findings across 6 dimensions — 17 high, 17 medium, 4 low.

## Parameter-contract discrepancies

Found while deriving result schemas (catalog params vs. what the C# handler actually reads):

- **`list_components`** — Catalog schema specifies 'includeInherited' boolean parameter, but handler reads 'includeProperties' at line 286. These are semantically different fields.
- **`analyze_screenshot`** — Handler reads: imagePath, analysisType, base64Data (not shown in catalog but supported). Catalog defines imagePath, base64Data, analysisType, prompt. Handler does not explicitly use the 'prompt' parameter - it extracts imagePath and analysisType only.

## Test Runner wiring

### HIGH — TestRunnerHandler.cs missing .meta file
*effort: small · risk: high*

**Evidence:** C:/Users/burak/Projects/Unity/unity-editor-mcp/unity-editor-mcp/Editor/Handlers/TestRunnerHandler.cs exists (21202 bytes, verified with ls) but no TestRunnerHandler.cs.meta file exists. All other handler files have .meta files present (e.g., AssetDatabaseHandler.cs.meta at line 1 of handler directory listing).

**Recommendation:** Create TestRunnerHandler.cs.meta with standard Unity metadata. Use the same template as other handler .meta files in the directory (e.g., copy from AssetDatabaseHandler.cs.meta which is 243 bytes). The .meta file is required for Unity to recognize the script as a native asset.

### HIGH — UnityEditorMCP.Editor.asmdef missing UnityEditor.TestTools.TestRunner references
*effort: small · risk: medium*

**Evidence:** TestRunnerHandler.cs line 6 imports 'using UnityEditor.TestTools.TestRunner.Api;' and uses TestRunnerApi, Filter, ExecutionSettings, ITestAdaptor, ITestResultAdaptor, ICallbacks interfaces (lines 19, 111, 131, 434, 440, 447, 452). UnityEditorMCP.Editor.asmdef line 4-6 only references 'Newtonsoft.Json'; UnityEditor.TestTools.TestRunner namespace is not declared as a reference.

**Recommendation:** Add 'UnityEditor.TestTools.TestRunner' to the references array in UnityEditorMCP.Editor.asmdef (line 5, after 'Newtonsoft.Json'). Change references from ["Newtonsoft.Json"] to ["Newtonsoft.Json", "UnityEditor.TestTools.TestRunner"].

### HIGH — package.json missing com.unity.test-framework dependency
*effort: small · risk: low*

**Evidence:** TestRunnerHandler.cs imports UnityEditor.TestTools.TestRunner.Api which requires com.unity.test-framework package. Package.json at lines 20-22 only declares 'com.unity.nuget.newtonsoft-json' in dependencies. No com.unity.test-framework entry is present.

**Recommendation:** Add com.unity.test-framework to dependencies in package.json. Change dependencies (line 20-22) from {"com.unity.nuget.newtonsoft-json": "3.2.1"} to {"com.unity.nuget.newtonsoft-json": "3.2.1", "com.unity.test-framework": "1.1.33"} (use version compatible with Unity 2020.3+ as specified in package.json line 5).

## Error / result contract

### HIGH — Error laundering: domain failures wrapped in status:success responses
*effort: large · risk: high*

**Evidence:** UnityEditorMCP.cs:427-749 wraps ALL handler returns (including error objects) via Response.SuccessResult(). E.g., GameObjectHandler.CreateGameObject() returns {error: 'message'} at GameObjectHandler.cs:39-50, which then gets wrapped as {status:'success', result:{error:'message'}} by UnityEditorMCP.cs:427. This causes failures to travel as successful responses.

**Recommendation:** Create a discriminated union response type at the C# layer. Each handler should return either Result<T> or Error<T>, never anonymous objects. Handlers must return Result.Ok(data) for success or Result.Failure(code, message) for failure. The ProcessCommand dispatcher should check the result type before wrapping. Eliminate the pattern of returning {error:...} objects that get laundered into success responses.

### HIGH — Inconsistent error encoding in handlers: anonymous {error} vs Response.Error() calls
*effort: large · risk: medium*

**Evidence:** 16 C# handlers return anonymous {error:message} objects (GameObjectHandler.cs:39, SceneHandler.cs:28, ComponentHandler.cs:31, AssetDatabaseHandler.cs:51, etc.) while Response.cs:60 and :89 provide Response.Error() methods that include code and details. Handlers never use Response.Error(). This creates two parallel error formats traveling through the same pipeline.

**Recommendation:** Standardize on a single handler error interface. Have all handlers return either a discriminated Result type OR require handlers to call Response.Error/ErrorResult when returning errors (but eliminate the object-wrapping pattern first). Define an internal error contract that all handlers must follow.

### HIGH — Node.js unwraps errors inconsistently from success-wrapped failures
*effort: medium · risk: medium*

**Evidence:** BaseToolHandler.js:56 returns {status:'success', result} for ALL outcomes. But Node handlers check result.error at runtime (CreateGameObjectToolHandler.js:124, LoadSceneToolHandler.js:71, CreateSceneToolHandler.js:71), attempting to detect failures buried inside the success result. This defensive unpacking is a code smell indicating the contract is broken.

**Recommendation:** Change BaseToolHandler.handle() to return a true discriminated response: {status:'success', result} OR {status:'error', error, code}. Make the status field trustworthy. Node handlers then use it to determine success/failure without inspection of nested fields.

### MEDIUM — UnityConnection.js::handleData tolerates two response formats with fallback logic
*effort: small · risk: low*

**Evidence:** UnityConnection.js:266-290 checks (response.status === 'success' || response.success === true) and (response.status === 'error' || response.success === false), then falls back to extracting result vs data (line 269). This dual-format tolerance masks the contract breakage and allows malformed responses to propagate unchecked.

**Recommendation:** Enforce a single response contract at the network boundary. Reject responses that don't match {status, result|error}. Remove fallback logic that accepts both 'success' and 'status' fields. Add schema validation to catch C# changes before they break the Node layer.

### MEDIUM — Response.cs has two separate success/error APIs that coexist without migration path
*effort: small · risk: low*

**Evidence:** Response.cs lines 16-118 define old-format methods (Success/Error with status/data/error/code fields), while lines 127-204 define new-format methods (SuccessResult/ErrorResult with status/result/error/code). UnityEditorMCP.cs uses only SuccessResult but both exist. Test file ResponseTests.cs:13-100 only tests old format (Success/Error). Code is mid-migration with both APIs live.

**Recommendation:** Complete the migration to SuccessResult/ErrorResult. Delete the old Success/Error methods. Update ResponseTests.cs to test only the new format. Document the breaking change to any external consumers. This is a prerequisite for fixing the error-laundering issue.

### MEDIUM — No type safety for handler return values; compile-time mismatches go undetected
*effort: large · risk: medium*

**Evidence:** Handlers declared as public static object Method(JObject params) at GameObjectHandler.cs:18, SceneHandler.cs:20, etc. The object return type allows any shape: {error}, {success}, plain data, or structured response. No compiler enforcement of contract. Creates runtime guessing games in consuming code.

**Recommendation:** Define and enforce a sealed Result<T> or Result type in C#. Change handler signatures to return Result<object> or Result<T>. Make the contract statically checkable. Use compiler warnings if handlers return plain objects instead of Result-wrapped values.

## Node handler layering

### HIGH — Double-wrapping of response payloads in legacy handlers
*effort: medium · risk: medium*

**Evidence:** mcp-server/src/handlers/analysis/GetGameObjectDetailsToolHandler.js:18-34 (returns MCP-shaped {content, isError} from execute()), wraps in BaseToolHandler.handle():61-64 (returns {status, result}), then mcp-server/src/core/server.js:111 unwraps and stringifies result.result containing the nested structure. The legacy tool at mcp-server/src/tools/analysis/getGameObjectDetails.js:139-147 returns {content: [{type, text}], isError: false} which gets wrapped twice instead of returning plain data.

**Recommendation:** Complete migration: refactor the 6 legacy tool handlers (analysis/*.js, scene/GetSceneInfoToolHandler.js) to directly implement execute() returning plain data objects matching the BaseToolHandler contract. Remove the intermediate functional layer in mcp-server/src/tools/ after handlers are migrated. All handlers should follow the pattern in CreateSceneToolHandler.js:61-90 where execute() returns {status, sceneName, ...} instead of {content, isError}.

### HIGH — Payload-discarding summary returns in legacy handlers
*effort: medium · risk: high*

**Evidence:** mcp-server/src/tools/analysis/analyzeSceneContents.js:84 returns only result.summary, discarding the full scene analysis data. mcp-server/src/tools/analysis/findByComponent.js:71 returns only summary/count, discarding the complete results array. mcp-server/src/tools/analysis/getComponentValues.js:114 returns only summary text. mcp-server/src/tools/scene/getSceneInfo.js:79 returns only summary. These handlers extract and return to MCP-shaped {content: text} which loses structured data that tools need for downstream processing.

**Recommendation:** After migration, handlers must return the full result object from unityConnection.sendCommand(), not just the summary text. For example, CreateGameObjectToolHandler.js:129 correctly returns result.result (the full object). Ensure all migrated handlers follow this pattern to preserve complete response payloads.

### HIGH — Inconsistent error handling between handler contracts
*effort: medium · risk: medium*

**Evidence:** GetGameObjectDetailsToolHandler.js:28-29 converts legacy handler's {isError, content} to exception by extracting content[0].text, but then returns the original result object (line 33) which still has {content, isError} shape. This means execute() doesn't have a uniform contract - it can return plain data OR MCP-shaped data. BaseToolHandler.handle():70-79 expects {status, error, code, details} but receives {content, isError} from some handlers. server.js:87-96 expects result.status but legacy handlers via execute() return objects without status.

**Recommendation:** Define a single handler execute() contract: return plain result objects (data) on success, throw Error on failure. All handlers must follow this contract. BaseToolHandler.handle() already converts thrown errors to {status: 'error', error, code, details}. Audit execute() implementations: CreateSceneToolHandler follows the pattern (throws or returns data), legacy handlers do not (return MCP-shaped objects that bypass the error handling).

### MEDIUM — Dead parameter-building code in legacy handlers
*effort: small · risk: low*

**Evidence:** mcp-server/src/tools/analysis/getGameObjectDetails.js:98-106 builds a params object with conditionally copied properties, but then passes the original args to sendCommand() on line 109 instead of the built params object. This dead code path creates technical debt and confusion about which parameters are actually sent to Unity.

**Recommendation:** Remove the dead parameter-building code block (lines 98-106) during migration. Verify that handlers directly pass input parameters to sendCommand() without intermediate reconstruction. Create a clear pattern: validate inputs in BaseToolHandler.validate() override, then pass args directly to sendCommand() in execute().

### MEDIUM — Orphaned legacy functional tools layer blocks migration clarity
*effort: medium · risk: medium*

**Evidence:** mcp-server/src/tools/ contains 11 files (analysis/5, scene/5, system/1 unused ping.js) that are imported by 6 of 68 handler classes. The legacy ping.js tool (mcp-server/src/tools/system/ping.js:9-72) is not used - PingToolHandler.js handles its own logic. This dead layer creates two parallel implementation patterns and increases surface area for bugs.

**Recommendation:** Establish completion criteria: (1) Migrate remaining 6 handlers to direct BaseToolHandler subclasses without importing from tools/. (2) After migration, delete all files in mcp-server/src/tools/. (3) Archive the old pattern as a git commit message and link to ADR if migration approach is needed as reference. The new pattern (BaseToolHandler subclass with direct implementation) is the target single contract.

### LOW — Vestigial validators.js not used by legacy handlers
*effort: small · risk: low*

**Evidence:** mcp-server/src/utils/validators.js defines validateVector3(), validateRange(), validateNonEmptyString(), validateBoolean(), validateLayer(), validateGameObjectPath() but is only imported by 2 new handlers (CreateGameObjectToolHandler.js:2, ModifyGameObjectToolHandler.js:2). The 6 legacy handlers perform inline validation instead (getGameObjectDetails.js:57-80, getComponentValues.js:54-77, getSceneInfo.js:46-55) with duplicated logic.

**Recommendation:** After migrating legacy handlers, consolidate all custom validation logic into overridden validate() methods in handler classes. Expand validators.js with domain-specific validators (e.g., validateComponentIndex, validateScenePath) and use them consistently across all handlers. Remove validators.js is only used if all handlers are confirmed migrated.

## Command lifecycle & domain-reload state

### HIGH — Async void dispatch with no domain reload safeguards
*effort: medium · risk: high*

**Evidence:** UnityEditorMCP.cs:326 - ProcessCommand is declared as `private static async void ProcessCommand(Command command, TcpClient client)`. This violates the async void pattern (only appropriate for event handlers). When a domain reload occurs mid-dispatch, the Task continuation is orphaned and any client response state is lost. No mechanism checks if a reload occurred before sending response at line 765.

**Recommendation:** Change ProcessCommand to return Task. Wrap the call in ProcessCommandQueue (line 318) with proper Task tracking and cancellation. Implement a DomainReloadBehavior enum: Task captures result state before reload. Alternatively, use a SessionState wrapper (see below) to journal incomplete commands.

### HIGH — Static field state wiped by domain reload with no recovery
*effort: medium · risk: medium*

**Evidence:** CompilationHandler.cs:18-20 (lastCompilationMessages, isMonitoring, lastCompilationTime). TestRunnerHandler.cs:19-22 (testRunnerApi, currentCallback, lastTestResults, isRunningTests). MenuHandler.cs:BlacklistedMenus, ConsoleHandler.cs reflection caches, ToolManagementHandler.cs:toolCache. When domain reload fires, all these static fields reset to defaults. Commands in flight lose their callbacks; test results vanish; menu blacklist resets. No [InitializeOnLoad] pattern in any handler re-arms event subscriptions.

**Recommendation:** Create a minimal SessionState singleton (e.g. UnityEditorMCP/Editor/State/SessionState.cs) persisted with ScriptableObject.CreateInstance and EditorUtility.SetDirty. Store: (a) in-flight command map {id → (handler, startTime, cancelToken)}, (b) domain reload counter. In CompilationHandler.Initialize() (line 386), re-register listeners and restore isMonitoring from SessionState. Wrap each handler static state read with a check: if lastDomainReloadGeneration < SessionState.generation, reinitialize. Recommendation: track only critical state (compilation monitoring on/off, test execution state).

### HIGH — No interrupted_by_reload result type for async operations
*effort: medium · risk: medium*

**Evidence:** Response.cs defines only Success/Error, with no ReloadInterrupted case. Command.cs:35 ReceivedAt timestamp never checked for reload boundary. ProcessCommand never yields control to ask: was I wiped? Test runner results (TestRunnerHandler.cs:21, lastTestResults Dictionary) persist in memory but lose their registered callbacks (line 125, currentCallback registration). On reload, GetTestResults returns stale in-memory state as if current.

**Recommendation:** Add Response.InterruptedByReload(id, partialResult) → {status: 'interrupted', code: 'DOMAIN_RELOAD', id, result: {...partial state...}, recoveryToken: ...}. Wire into ProcessCommand: before awaiting any async operation, check EditorApplication.isCompiling as a heuristic for domain reload imminence. Store a sessionId in SessionState; on reload, version it. Compare sessionId in GetTestResults/GetCompilationState. If mismatch, return {status: 'interrupted', recoveryToken: sessionId}.

### HIGH — Command queue not persisted; in-flight commands lost on reload
*effort: small · risk: low*

**Evidence:** UnityEditorMCP.cs:27 commandQueue is `private static readonly Queue<(Command command, TcpClient client)>` with no journal. ProcessCommandQueue (line 311) drains it each frame. If a domain reload fires between Enqueue (line 244) and ProcessCommand execution, the command is orphaned. The TCP client reference cannot be serialized anyway. No way for Node side to know reload occurred.

**Recommendation:** Minimal file journal: write each command to EditorPrefs or a .json file in Library/Unity Editor MCP/pending-commands.json before dequeueing. On ProcessCommand completion, remove entry. On Initialize(), scan for orphaned commands older than (e.g.) 30s and send a noop cancel response. Journal format: [{id, type, params, enqueuedAt, sessionId}]. This is durable but not perfect (file I/O overhead); for simplicity, at least log to EditorApplication.logMessageReceived when reload detected (via [InitializeOnLoad] in a ReloadDetector class).

### MEDIUM — No job/task model to track long-running operations
*effort: small · risk: low*

**Evidence:** TestRunnerHandler.RunTests (line 68) sets isRunningTests = true (line 128), then executes testRunnerApi.Execute (line 132) with no correlation to command ID or session. GetTestResults (line 153) returns lastTestResults but has no way to know which command_id triggered the run. If domain reload hits mid-test, currentCallback (line 20) is garbage-collected and RunFinished never fires. isRunningTests stays true forever.

**Recommendation:** Create a minimal JobModel: each long-running operation (test run, compilation) stores {id, type, startTime, status, lastProgressUpdate, result}. TestRunnerHandler keeps a JobRegistry keyed by command.Id. OnDomainReload, check if lastProgressUpdate > (now - reloadGracePeriod); if stale, mark as interrupted. Store in SessionState. GetTestResults should accept a jobId parameter. This is similar to LSP's $/progress notification pattern but simpler.

### MEDIUM — No evidence of domain reload testing
*effort: small · risk: low*

**Evidence:** Integration tests (UnityEditorMCPIntegrationTests.cs) do not trigger EditorApplication.quitting or simulate domain reload. No test for: (a) command in queue at reload → recovery, (b) test running at reload → interrupted result, (c) compilation listener re-armed at reload. CLAUDE.md notes NUnit tests run via Unity Test Runner but no CI matrix compiles C# on the compatibility floor.

**Recommendation:** Add integration test (e.g., DomainReloadTests.cs) that uses EditorApplication.update to inject a fake domain reload event, verifying handlers re-initialize. Cannot force actual reload in test, but can simulate state loss. This is lower priority but would catch regressions.

### MEDIUM — Initialization pattern fragile: InitializeOnLoadMethod runs after static constructor but may race
*effort: small · risk: low*

**Evidence:** CompilationHandler.cs:386 [InitializeOnLoadMethod] Initialize() calls EditorApplication.delayCall (line 389), which defers StartCompilationMonitoring until next frame. If a command arrives before then, monitoring state is undefined. UnityEditorMCP.cs static constructor (line 52) runs at load, but CompilationHandler.Initialize() is not guaranteed to run before first command.

**Recommendation:** Consolidate: (a) UnityEditorMCP cctor subscribes EditorApplication.update, (b) First ProcessCommandQueue call checks a global _initialized flag; if false, call a static Initialize() method that ensures all handlers are ready. Or use an explicit Startup() method callable from editor menu for manual control during reload debugging.

## Transport reliability

### HIGH — Missing message size validation in C# (no upper bound guard)
*effort: small · risk: medium*

**Evidence:** C:/Users/burak/Projects/Unity/unity-editor-mcp/unity-editor-mcp/Editor/Core/UnityEditorMCP.cs:215 reads messageLength from wire without bounds check. Node.js implementation at C:/Users/burak/Projects/Unity/unity-editor-mcp/mcp-server/src/core/unityConnection.js:206 enforces 1MB cap (if messageLength < 0 || messageLength > 1024 * 1024).

**Recommendation:** Add identical size validation in C# immediately after line 215. Reject messages exceeding 1MB and log error with recovery attempt. Example: if (messageLength < 0 || messageLength > 1024 * 1024) { /* error handling */ }. This prevents malformed/adversarial frames from causing unbounded buffer growth or integer overflow on the line 218 count check.

### MEDIUM — O(n) buffer removal using List<byte>.RemoveRange() in hot path
*effort: medium · risk: low*

**Evidence:** C:/Users/burak/Projects/Unity/unity-editor-mcp/unity-editor-mcp/Editor/Core/UnityEditorMCP.cs:189 allocates List<byte>() and line 222 calls messageBuffer.RemoveRange(0, 4 + messageLength) in the per-packet framing loop. RemoveRange shifts all remaining bytes—O(n) per message. Node.js at line 242 uses immutable Buffer.slice(4 + messageLength), which is O(1).

**Recommendation:** Replace List<byte> with a circular buffer or index-based tracking (track read offset rather than removing bytes). Alternatively, keep a byte[] with a head index and only compact when buffer is 75% consumed. This preserves floor-compatible wire behavior while fixing throughput regression under large message volume or malformed partial frames.

### MEDIUM — Shared protocol channel with unframed log output (prefix-sniffing fragility)
*effort: medium · risk: medium*

**Evidence:** Node.js C:/Users/burak/Projects/Unity/unity-editor-mcp/mcp-server/src/core/unityConnection.js:187-195 contains heuristic detection of unframed logs (startsWith('[Unity Editor MCP]') || startsWith('[Unity]')). C# code has 19 Debug.Log() calls (e.g., line 225: Debug.Log("[Unity Editor MCP] Received command...")). Logs are only discarded if messageBuffer is empty (line 188), so a log arriving mid-frame is treated as malformed JSON and causes frame corruption (line 250: warns, continues, discarding valid payload bytes).

**Recommendation:** Separate logging from command TCP stream entirely. Redirect all C# Debug.Log output to a dedicated logging endpoint (file, separate named pipe, or stderr redirector) and remove [Unity*] prefixes from unframed log detection. This eliminates prefix-sniffing race condition and simplifies error recovery. If separation is not feasible immediately, at minimum: (1) reject frames if buffer is non-empty + log-like prefix detected (discard the log, not the buffered frame), (2) require 4-byte framing for ALL output from C#, (3) document log channel separation as a required floor fix.

### MEDIUM — Negative or overflow-inducing messageLength accepted in C# until bounds check
*effort: small · risk: medium*

**Evidence:** C:/Users/burak/Projects/Unity/unity-editor-mcp/unity-editor-mcp/Editor/Core/UnityEditorMCP.cs:215 converts raw bytes to int32 with no prior validation. Line 218 then checks if (messageBuffer.Count >= 4 + messageLength) — if messageLength is negative, the addition overflows. Node.js prevents this at line 206 (if messageLength < 0 || ...). Negative lengths pass the C# check if buffer is large enough (e.g., length=-1 and buffer.Count=10 means 4 + (-1) = 3, which is less than 10, so continues to extract).

**Recommendation:** Add pre-check before using messageLength: if (messageLength < 0) { /* log error, skip frame */ }. Placing this immediately after line 215 matches Node pattern and blocks negative lengths before they can cause silent integer underflow in line 218 or corrupted slice in line 221.

### MEDIUM — No maximum buffer size enforcement in C# accumulation phase
*effort: small · risk: low*

**Evidence:** C# lines 201–204 accumulate bytes into List<byte> with no cumulative size check. A client sending framing header (length=1GB) followed by zero bytes will cause messageBuffer to allocate unbounded memory waiting for completion. Node.js implicitly bounds this via the 1MB validation at line 206 (invalid frames clear buffer at line 233).

**Recommendation:** Add cumulative buffer size guard before processing loop. Before line 207 (while messageBuffer.Count >= 4), add: if (messageBuffer.Count > 2 * 1024 * 1024) { /* clear buffer, log, skip */ }. Use 2MB as the threshold to account for in-flight size headers that may be slightly larger than the 1MB message cap. This prevents slow-rate memory exhaustion attacks or client bugs that lock the handler in buffer accumulation.

### LOW — Duplicate length-prefix framing logic with inconsistent error recovery
*effort: large · risk: low*

**Evidence:** Both implementations (Node: lines 201–236, C#: lines 207–264) reimplement 4-byte big-endian length-prefix framing. Node has frame recovery (lines 211–228) searching for valid frames within corrupted data; C# has none. If a frame header is corrupted, C# silently waits forever; Node attempts resync. This is a compatibility issue if deployments interleave old/new versions.

**Recommendation:** Extract framing logic into a shared protocol utility module (e.g., protocol/lib/framing.js or a C# shared class UnityMcpFraming). Implement identical error recovery in both: on invalid length, scan forward for next valid 4-byte frame. Document recovery strategy in protocol/README.md. This ensures floor compatibility across version boundaries and reduces future divergence. Lower priority than size validation or log separation, but necessary for long-term maintainability.

## Generating the Unity dispatch from the catalog

### HIGH — Switch dispatch is extracted from source by regex, no compile-time guarantee
*effort: medium · risk: low*

**Evidence:** protocol/scripts/lib/sources.mjs:43–49 — `getEditorCommands()` uses regex `/case\s+"([a-z0-9_]+)"\s*:/g` to extract case labels. UnityEditorMCP.cs:335–760 implements 67 cases hand-written. Any case typo (e.g., `case "create_prefb"`) or missing case for a catalog command compiles silently and returns UNKNOWN_COMMAND at runtime (line 753–758: default case).

**Recommendation:** Generate a C# registry (enum + dictionary) from `protocol/catalog/commands.json` that maps command type strings to handler delegates. Emit compile-time errors when a catalog command is missing or a handler signature doesn't match. This converts runtime dispatch errors into compile errors.

### HIGH — Catalog declaration vs. implementation drift is detected only at CI time
*effort: medium · risk: low*

**Evidence:** protocol/README.md line 21–22: 'The catalog makes the command surface a single declared thing, and `check-drift` fails CI when either half disagrees with it.' Current known gap: `get_component_types` declared in catalog but has no editor case (protocol/catalog/commands.json line 3–10, baselined in knownGaps). The editor code at UnityEditorMCP.cs:751–758 returns UNKNOWN_COMMAND; developers must wait for CI to discover this (check-drift.mjs:72).

**Recommendation:** A code generator that parses the catalog and emits C# constants (CommandRegistry.cs) with: (1) a static class holding const strings for each command name (`public const string CreateGameObject = "create_gameobject"`), (2) an enum of all command types, and (3) a metadata dictionary with per-command destructive flag, category, and sides. Missing a case causes a compiler error when the handler delegate is not found.

### HIGH — Catalog commands without editor implementation do not fail the build
*effort: small · risk: low*

**Evidence:** Known gap baselined in protocol/catalog/commands.json:5–10: `get_component_types` is a server-side tool with `sides: ["server", "editor"]` but no C# dispatch case. At runtime, UnityEditorMCP.cs:753–758 hits the default case and returns `UNKNOWN_COMMAND`. The drift gate (check-drift.mjs) catches this at CI time (line 72: `Catalog declares editor side for "get_component_types" but no editor dispatch case exists`), but the C# code compiles without error.

**Recommendation:** Code generator should emit a `CatalogAssertion` class with a static constructor that iterates over all catalog commands and asserts each one that declares `sides: ["editor"]` has a corresponding handler in the dispatcher registry. Failure throws an initialization exception before any commands can be processed. This converts the post-CI discovery into a pre-runtime check that runs at assembly load.

### MEDIUM — No type safety for command parameter contracts
*effort: large · risk: medium*

**Evidence:** Command model (Models/Command.cs:29) uses `public JObject Parameters` — untyped JSON. Each handler manually parses parameters via `parameters["key"]?.ToString()` with no validation (e.g., GameObjectHandler.cs:23–29, SceneHandler.cs:25–29). Catalog specifies JSON Schema for params (commands.json: each command has a `params` JSON Schema), but C# has no compile-time model matching.

**Recommendation:** The code generator should emit optional strongly-typed command request models (e.g., `CreateGameObjectRequest { Name, PrimitiveType, Position, ... }`) from the catalog's param schemas. Handler methods can then accept these as optional inputs alongside the generic `JObject`. This provides compile-time safety without breaking existing code. Handlers that use `HandleCommand(action, params)` dispatch pattern (tags, layers, asset database) need action-specific request types.

### MEDIUM — Handler signature variability allows silent contract violations
*effort: medium · risk: medium*

**Evidence:** Two dispatch patterns coexist: (1) one-method-per-command, e.g., `GameObjectHandler.CreateGameObject(JObject)` (UnityEditorMCP.cs:426–428), (2) action-based multiplexing, e.g., `TagManagementHandler.HandleCommand(string action, JObject)` (UnityEditorMCP.cs:684–686). The action parameter is passed via `command.Parameters["action"]?.ToString()` with no validation. If a handler expects an action and the command omits it, it receives null and returns an error inside the handler rather than failing at the dispatch boundary.

**Recommendation:** Code generator should emit a sealed `CommandDispatcher` or `ICommandHandler` interface registry where each handler is wrapped in a delegate with a known signature `(Command) -> object`. The dispatcher validates required parameters before calling each handler. For action-based handlers, emit helper classes that parse and validate the action enum upfront.

### MEDIUM — Response wrapping hides domain errors as successes
*effort: medium · risk: medium*

**Evidence:** protocol/README.md, 'Known deviations' (line 110–113): 'The editor dispatcher wraps every handler result — including `{ error: ... }` — in `SuccessResult`, so domain failures arrive with `status:"success"`.' Example: handlers return `new { error = "Scene name cannot be empty" }` (SceneHandler.cs:28) and this is wrapped in `Response.SuccessResult(command.Id, result)` (UnityEditorMCP.cs:461), arriving at the client as `{ status: "success", result: { error: "..." } }` instead of the target envelope `{ status: "error", code: "...", error: "..." }`.

**Recommendation:** Code generator should emit a `Result<T>` discriminated union type (success + value, or error + code + message). The dispatcher's responsibility becomes: (1) validate the handler returned a `Result<T>`, (2) unwrap it into the wire envelope format. This is orthogonal to the command dispatch generator but should be done in the same refactor to avoid churn.

### MEDIUM — Generated code must integrate with existing handler static classes without breaking them
*effort: medium · risk: medium*

**Evidence:** All current handlers are static classes (GameObjectHandler, SceneHandler, etc., in Editor/Handlers/*). The dispatch switch (UnityEditorMCP.cs:335–760) directly calls static methods on each. Any code generator refactor must preserve this interface — handlers cannot be rewritten as instance classes without breaking the entire dispatch layer and violating the architecture pattern already in use.

**Recommendation:** Code generator should emit a thin `CommandDispatcher` that (1) maps command types to pre-bound delegates calling the existing static handler methods (e.g., `GameObjectHandler.CreateGameObject`), (2) validates parameters before dispatch, (3) unwraps error results into the correct envelope. The dispatcher replaces the switch statement but does not touch the handlers themselves. Handler signatures remain unchanged: `public static object MethodName(JObject parameters)`.

### MEDIUM — Integration point: TCP framing and error response path must support discriminated responses
*effort: medium · risk: medium*

**Evidence:** UnityEditorMCP.cs:326–793 (ProcessCommand) wraps all handler results in `Response.SuccessResult(command.Id, result)` regardless of whether result contains an error object. The SendFramedMessage path (line 765) has no way to detect and transform an error response. Protocol target envelope (protocol/README.md:43–44) specifies error responses must have `status: "error"` and a `code` field, but the current flow sends `status: "success"` for domain errors.

**Recommendation:** Code generator should work with a refactored Response.cs that emits discriminated envelope types. A new overload `Response.SuccessOrError(command.Id, Result<T> result)` should inspect the result and emit the correct envelope. The dispatcher's try-catch (line 768–792) should also be refactored to catch handler exceptions and emit proper error envelopes. This is a breaking change to Response.cs that the generator should facilitate by emitting example usage patterns.

### LOW — No command metadata available to the dispatcher
*effort: small · risk: low*

**Evidence:** Catalog declares per-command metadata: `destructive` flag (commands.json: CreatScene, DeleteGameObject, etc.), `category` (analysis, asset, gameobject, etc.), and `description`. UnityEditorMCP.cs has no access to this. The dispatcher cannot, e.g., log destruction metadata, enforce confirmation gates, or report command category stats.

**Recommendation:** Code generator should emit a `CommandMetadata` class with a static dictionary mapping command type strings to immutable metadata records (`name`, `category`, `destructive`, `description`, `paramSchema`). The dispatcher's `ProcessCommand` method can consult this before dispatch to enable structured logging, validation, and auditing.

### LOW — C# 8 / netstandard 2.0 constraints limit generator implementation options
*effort: small · risk: low*

**Evidence:** CLAUDE.md:18–20: C# must stay within C# 8 / netstandard 2.0 (2020.3 Mono). This rules out: (1) records (`record` syntax, C# 9+), (2) nullable reference types (`#nullable enable`, C# 8 but Mono 2020 support incomplete), (3) `init` properties, (4) source generators (require C# 9+ and targeting netstandard 2.1+). Code generator output must use plain `public class` / `public struct`, explicit null checks, and factory methods.

**Recommendation:** Code generator (a Node.js script reading `commands.json`) should emit C# 8 safe types: sealed classes with readonly properties, factory methods, explicit ToString() overrides. Use `Dictionary<string, Action<Command>>` for delegates rather than generics with constraints. Emit no attributes beyond `[Serializable]` if needed for Unity reflection.

