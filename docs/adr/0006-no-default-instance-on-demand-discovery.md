# ADR 0006 — No default instance; on-demand tool discovery (the lean client)

Status: Accepted — implemented in v0.5.0 (the "lean Adaptive-Node client": the MCP surface is the 3
generic meta-tools; `instance` is required on every call; the editor advertises params + result-field
hints, learned at runtime) · Date: 2026-06

## Context

v0.3.0 made the bridge any-to-any (ADR 0005): a Node `UnityConnectionManager` pools a connection
per editor instance, and `call_unity_tool({ instance, tool, params })` routes to one. Two things from
that era are now liabilities:

1. **A default/active instance.** `set_active_unity_instance` + the manager's "active connection" let
   an omitted `instance` fall back to a current target. In a system whose *default reality is several
   editors at once* (the legacy-dev use case — 2020.3 + 2021.3 + 2022.3 open together), an implicit
   current instance is a safety hazard. An agent **revived after context compaction** — or one that
   discovered the active instance in a since-discarded thinking turn — can issue a destructive call
   (`delete_gameobject`, `update_script`, `manage_packages`, `quit_editor`) against the **wrong live
   project**. Those edits are unrecoverable. "Usually correct" is not good enough when the failure is
   catastrophic and silent.

2. **An advertised typed surface duplicating the editor.** ADR 0004's typed tools are ~76 Node
   passthrough handlers, each hardcoding an `inputSchema` that duplicates the protocol catalog (the
   sole reason the catalog↔JS drift check exists) and, when advertised, costing the model context for
   tools it may never use. The editor already advertises its command manifest on the handshake; Node
   re-deriving it by hand is boilerplate, and it makes Node depend on a static command list rather
   than on the connected editor.

A handler audit (2026-06-15) found **76 of 79 typed handlers are pure passthroughs**; only 3 carry
genuine Node-side logic (`execute_menu_item`, `create_script`, `analyze_screenshot`).

## Decision

**1. No default/active instance — the instance identifier is required on every instance-bound call.**
Remove `set_active_unity_instance` and the manager's active/default connection. `call_unity_tool` and
`list_unity_tools` **require** an explicit `instance` (project path or port); a missing or ambiguous
instance is a hard, clearly-worded error, never a silent default — **even when only one editor is
running**. The agent must always state which editor it is acting on. This makes wrong-instance
operations *structurally impossible* rather than merely unlikely. Discovery of valid identifiers is
`list_unity_instances`.

**2. On-demand discovery, not advertisement.** The MCP tool surface is exactly three meta-tools:
`list_unity_instances`, `list_unity_tools(instance)`, `call_unity_tool(instance, tool, params,
[fields])`. `list_unity_tools` **returns the instance's tools as data** (names, descriptions, params,
and result-field hints) — read into the agent's context on demand. It never mutates the MCP tool
list: no `notifications/tools/list_changed`, no dynamic registration. The model is self-healing — if
context is compacted or discovery happened in a discarded reasoning step, the agent simply calls
`list_unity_tools` again. This also eliminates the connection-timing problem that an advertised,
manifest-generated surface would have had (the editor manifest only exists after a connect+handshake;
discovery sidesteps it because it reads the *live* manifest at call time).

**3. Delete the passthrough surface; keep only genuine Node logic, behind the same interface.** The 76
passthrough handlers are removed; their capabilities are reached via `call_unity_tool` after
discovery. The 3 Node-logic tools are kept but invoked **through `call_unity_tool`** (Node-side
interception by tool name) so the agent's interface stays uniform and the advertised surface is just
the 3 meta-tools. `create_script` (C# template generation) and `analyze_screenshot` (thin base64
branch) are flagged to migrate into the editor later; `execute_menu_item`'s input-edge security
normalization is deliberately kept in Node.

## Consequences

- **Safety:** an agent cannot act on an editor without naming it; the revived-agent / wrong-instance
  fatal-edit class is closed by construction.
- **Leaner + adaptive:** ~76 fewer Node files; the catalog↔JS drift class is gone; Node exposes
  whatever the connected editor advertises (including commands the Node package predates), at the cost
  of three tools' worth of context instead of seventy-seven.
- **Breaking contract change (acceptable pre-1.0):** `instance` becomes required; `set_active_unity_instance`
  and `UNITY_MCP_TYPED_TOOLS` are removed. Clients/agents must discover then act.
- **One extra hop:** acting on an unfamiliar instance costs a `list_unity_tools` call first — the
  deliberate safety/economics trade; the result is cached in context until compacted.
- **Drift gate re-pointed:** with the JS passthroughs gone, the drift check shifts from catalog↔JS to
  catalog↔editor-manifest (or the JS-side check is retired); detailed in the v0.5.0 plan.
- **A small Node-logic exception** to "every capability is an editor command" remains (the 3 kept
  tools), flagged for later migration so the end-state is meta-tools only.
