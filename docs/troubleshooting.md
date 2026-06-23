# Troubleshooting

The bridge has three links — **MCP client ⇄ Node server ⇄ (TCP loopback) ⇄ Unity editor** — so most issues come
down to "which link is down." This matrix lists the common failure smells, what they mean, and the fix.

## Connection

| Symptom | Likely cause | Fix |
|---|---|---|
| `list_unity_instances` returns empty | No editor running with the package installed, or a stale discovery registry | Open the Unity project; the Console should log `[Unity Editor MCP]` on load. Instances are published to the registry dir (`%LOCALAPPDATA%/unity-editor-mcp/instances` on Windows, OS equivalents elsewhere). |
| `ECONNREFUSED` on a call | Editor not running, **a domain reload is in progress** (the listener re-arms after every reload), or wrong port | Wait a moment and retry — recompiles/play-mode transitions briefly drop the listener. Confirm the editor is up and not mid-compile. |
| Calls hit the wrong project | Several editors open; there is **no default instance** (ADR 0006) | Every call must name its target (`instance` = project path or port). Get it from `list_unity_instances`. |
| Connection drops mid-command | A domain reload (script recompile, entering play mode) tore down + re-armed the listener | Expected. The Node side reconnects with backoff; re-issue the command. Long ops (test runs) journal results so they survive a reload. |
| MCP client sees garbage / protocol errors | Something wrote to **stdout**, which is reserved for MCP JSON-RPC | All server logging must go to stderr. Check for stray `console.log`; `LOG_LEVEL=debug` logging still goes to stderr. |

## Ports & discovery

| Symptom | Likely cause | Fix |
|---|---|---|
| Want a fixed port instead of the derived one | Default is a **per-project derived port** (range 6400–7423) | Pin it: `UNITY_MCP_PORT` (editor side) + `UNITY_PORT` (server side, wins over discovery). |
| Server can't resolve the editor by project | Registry dir mismatch | Set `UNITY_PROJECT_PATH` (resolves via the registry / derived port) or `UNITY_MCP_REGISTRY_DIR`. |
| "Port already in use" | Another editor/process on the derived port | Close the other editor, or pin distinct ports. |

## Compilation & commands

| Symptom | Likely cause | Fix |
|---|---|---|
| `UNKNOWN_COMMAND` | The command isn't registered on this editor's version, or a typo | `list_unity_tools(instance)` shows what this editor actually supports. |
| Commands fail right after editing scripts | The editor is compiling; most ops need a settled domain | Check `get_compilation_state`; trigger `refresh_assets`, wait for the compile to finish, then retry. |
| `PLAY_MODE` error | A scene/asset/prefab mutation was attempted in play mode | Exit play mode (`stop_game`) before scene/component/prefab mutations. |
| `INVOKE_DENIED` from `invoke_static_method` | Static invoke is **default-deny** (arbitrary code execution) | Allow-list the method via the `UNITY_MCP_INVOKE_ALLOW` env var or `ProjectSettings/UnityEditorMcpInvokePolicy.json` (`{"allowInvoke":[...]}`); patterns: exact, `Ns.Type.*`, or `*`. |
| A destructive op refuses | Confirm-gate (H3) | Re-issue with the confirmation the error message specifies. |
| `VALIDATION_ERROR` "must stay within the project root" | Path sandbox (H4) | Use project-relative `Assets/...` paths — no `..` escapes. |

## Code intelligence (Roslyn)

| Symptom | Likely cause | Fix |
|---|---|---|
| `resolve_symbol` / `get_type_members` / `find_implementations` unavailable | The Roslyn sidecar isn't running (capability-gated) | `start_roslyn`, then poll `roslyn_status` until `ready`. The lite symbol search/outline works without it; `find_references` upgrades to `resolution: "semantic"` once ready. |

## CI (floor-matrix)

| Symptom | Likely cause | Fix |
|---|---|---|
| The floor-matrix job can't activate Unity | License secret missing/expired | Set the repo secrets `UNITY_LICENSE` (the Personal `.ulf`), `UNITY_EMAIL`, `UNITY_PASSWORD` (GameCI activates Personal from these — Unity deprecated offline manual activation for Personal). |
| `Resource not accessible by integration` | The default workflow token is read-only | The workflow needs `permissions: checks: write` (unity-test-runner posts results as a check run). |
