# Mutation Audit Log (0.12.0) Design

> Status: design (autonomous). Section H, requirement **H5**: "Audit log option — every mutating command
> appended to a local journal (op, target, time) for post-hoc review." The accountability companion to the
> 0.11.0 H3 confirm-gate.

## 1. Scope

**In 0.12.0:** a local, append-only command journal + two commands to read/clear it.
- Every dispatched command is journaled (a complete trail — a superset of mutations; the reader filters by
  type for the mutating subset, which avoids the false-negatives of a verb heuristic).
- Entry: `{ "t": <ISO-8601 UTC>, "type": <command>, "target": <hint>, "ok": <bool> }`.
- Stored as **JSONL** at `Library/UnityEditorMCP/audit-log.jsonl` (survives domain reloads + editor restarts),
  **size-capped** (~2 MB; on overflow the oldest half is dropped) so it never grows unbounded.
- `get_audit_log` reads recent entries (filtered); `clear_audit_log` empties it.
- Always-on, disablable via env `UNITY_MCP_AUDIT_LOG=0`.

**Deferred (later H slices):** the shared-secret token (H1), allow/deny policy for menu/static-invoke (H2),
path-sandbox completeness sweep (H4).

## 2. Core: `AuditLog` (Unity-independent, dotnet-tested)

A static utility in `unity-editor-mcp/Core/AuditLog.cs` — path-injectable so it runs under `dotnet test`. All
methods are **fail-safe** (swallow IO errors): audit logging must never break command dispatch or reads.
- `Append(string filePath, string type, string target, bool ok, long capBytes = 2_097_152)` — stamps
  `DateTime.UtcNow` (ISO-8601 "o"), writes one JSON line. Before appending, if the file exceeds `capBytes`, it
  rewrites the file keeping the **last half** of its lines (crude rotation; the truncate is occasional, so most
  appends are O(1)).
- `Read(string filePath, int max, string typeFilter, string since)` — parses the JSONL, applies the filters
  (`typeFilter` = case-insensitive substring of `type`; `since` = entries with `t >= since`), returns the last
  `max` (default 100, ceiling 1000) entries in chronological order as a `JArray`. Unparseable lines are skipped.
- `Clear(string filePath)` — deletes the file.

## 3. Editor: the journal hook

In `UnityEditorMCP.DispatchViaCore`, capture the `Dispatch` result, journal it, then respond:
```
var result = _dispatcher.Dispatch(request);
AuditLogBridge.Record(command?.Type, command?.Parameters, !result.IsError);
respond(result.ToJson());
```
`AuditLogBridge` (editor-side) resolves the path once (`Library/UnityEditorMCP/audit-log.jsonl` via
`Application.dataPath`), honors `UNITY_MCP_AUDIT_LOG`, extracts a **target hint** from the params (first of
`assetPath`, `gameObjectPath`, `path`, `scenePath`, `prefabPath`, `variantPath`, `typeName`, `name`, or
`target.scenePath`/`target.assetPath`/`target.instanceId`), and calls `Core.AuditLog.Append` inside a
try/catch. The journal write is on the editor thread (not the Node hot path) and is cheap (one append).

## 4. Editor: `get_audit_log` + `clear_audit_log`

A small `AuditLogHandler`:
- `get_audit_log` — params `{ max?, type?, since? }` → `Core.AuditLog.Read` → `{ entries: [...], count }`.
- `clear_audit_log` — `destructive:true` (so it rides the H3 confirm-gate — clearing the trail is itself a
  sensitive op) → `Core.AuditLog.Clear` → `{ cleared: true }`.

Both cataloged (`sides:["editor"]`, category `system`), registered on the dispatcher. `get_audit_log` is
read-only; `clear_audit_log` is marked `requiresConfirm` (H3) — you confirm before erasing the trail.

## 5. Error model & floor-safety

No new error codes (read-only get; clear rides CONFIRMATION_REQUIRED). All APIs are BCL `System.IO` +
`DateTime` + Newtonsoft — floor-safe (C# 8 / netstandard 2.0). `AuditLog` lives in Core (no Unity refs);
the editor supplies the Unity path. Nothing for COMPATIBILITY.md.

## 6. Testing

- **Core dotnet (`AuditLogTests`):** Append then Read round-trips an entry; `typeFilter` + `since` filter;
  `max` caps the returned count; the size cap drops the oldest half when exceeded; `Clear` empties it;
  a malformed line is skipped (not fatal); Append on a bad/locked path does not throw.
- **Editor EditMode:** `get_audit_log` after a couple of dispatched commands returns them; `clear_audit_log`
  empties it. (The hook itself is exercised by the live bridge.)
- **Floor dogfood:** run a few commands over the bridge, then `get_audit_log` shows them; `clear_audit_log`
  (with confirm) empties it.
