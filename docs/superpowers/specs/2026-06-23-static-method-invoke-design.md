# Static-Method Invoke + default-deny gate (G6 / H2) Design

> Status: design (autonomous, user-approved 2026-06-23). Roadmap **G6 (P0)** — "menu-item execution + static-method
> invoke" — was only half-done (`execute_menu_item` exists; static invoke did not). Adds `invoke_static_method`,
> the standout missing power capability: call any static method by type + name with JSON args. Because it is
> arbitrary code execution, it ships behind **H2 default-deny** — off entirely unless explicitly allow-listed.

## Gate — `InvokePolicy` (H2, default-deny)

`InvokePolicy.IsAllowed(typeName, methodName)` builds `"<FullType>.<Method>"` and matches it against allow
patterns drawn from two sources (union):
- env `UNITY_MCP_INVOKE_ALLOW` — comma/semicolon-separated patterns (CI / quick setups).
- `ProjectSettings/UnityEditorMcpInvokePolicy.json` — `{ "allowInvoke": ["...", ...] }` (project-committed, version-controllable).

Pattern forms: exact `Ns.Type.Method`; prefix `Ns.Type.*` (any method of the type); `*` (allow everything — full
ACE opt-in). **No source / no match → denied** (`INVOKE_DENIED`). Read fresh per call (infrequent op; no caching
so policy edits take effect immediately and tests can set the env var).

## Command — `invoke_static_method` (category `menu`)

`{ typeName (required), methodName (required), args? (JSON array), assemblyName? }`.
1. Validate typeName/methodName (`VALIDATION_ERROR`).
2. **Gate:** `InvokePolicy.IsAllowed` else `INVOKE_DENIED` (message points at both config sources).
3. Resolve the type: `Type.GetType` then scan `AppDomain.CurrentDomain.GetAssemblies()` (optional `assemblyName` filter); miss → `NOT_FOUND`.
4. Find a `static` method (public or non-public) by name with `args.Count` parameters; none → `NOT_FOUND`, >1 → `AMBIGUOUS`.
5. Marshal each arg via `JToken.ToObject(parameterType)`; failure → `VALIDATION_ERROR`.
6. `method.Invoke(null, args)`; a `TargetInvocationException` → `INVOCATION_ERROR` (inner message).
7. Return `{ type, method, returnType, isVoid, result }` — result via `JToken.FromObject` (ToString fallback for non-serializable).

No confirm-gate (the allow-list IS the security boundary — double-gating an allow-listed method is noise). No
play-mode guard (static methods may legitimately run in play mode; the allow-list bounds what's callable).

## Floor-safety

`System.Reflection`, `AppDomain.CurrentDomain.GetAssemblies`, `JToken.ToObject/FromObject`, `Application.dataPath`,
`System.IO` — all floor-safe (netstandard 2.0, all editors). No `#if` guards; nothing for COMPATIBILITY.md.

## Catalog & testing

`invoke_static_method` (category `menu`, `sides:["editor"]`), registered in `BuildDispatcher`. EditMode test uses
a `static` test target in the test assembly: denied-by-default → allow via env → returns result (Add) → void
method → bad type `NOT_FOUND` → throwing method `INVOCATION_ERROR` → missing params `VALIDATION_ERROR`. Dogfood on
2020.3 (deny path over the bridge; allow path with an env-listed editor method).

## Cadence

Branch (now post-0.20.0-prep). This lands in the held release batch (0.20.0 grows, or becomes 0.21.0 — user cuts
when "all" is done). Counterpart H2-for-menu (allow/deny on `execute_menu_item`) + H1 token remain separate, lower-priority.
