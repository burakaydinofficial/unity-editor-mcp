/**
 * Normalizes a connection's advertised tool surface, with graceful degradation for editors that
 * predate the rich handshake manifest (ADR 0004 — version-agnostic across PACKAGE versions, not just
 * Unity versions).
 *
 * A v0.3.0+ editor advertises `commands` ({ name, category, description, params, result }); an older build
 * advertises only `availableCommands` (names). When the rich manifest is absent we fall back to the
 * names, so the generic surface can still drive that editor — those tools are callable, just without
 * Node-side schema validation (the editor itself validates, exactly as the typed tools rely on).
 *
 * @param {object|null} editorInfo - conn.editorInfo (the handshake reply)
 * @returns {{ tools: Array<{name:string, category:string|null, description:string, params:object|null, result:object|null}>, hasSchemas: boolean }}
 *   `params`/`result` carry the JSON schemas when known (rich manifest) and are null in degraded mode.
 *   `result` is the response-shape hint the agent reads to drive `fields` projection (ADR 0006).
 */
export function editorToolSurface(editorInfo) {
  const info = editorInfo || {};
  const rich = Array.isArray(info.commands) ? info.commands.filter((c) => c && typeof c.name === 'string') : [];
  if (rich.length > 0) {
    return {
      tools: rich.map((c) => ({
        name: c.name,
        category: c.category ?? null,
        description: c.description ?? '',
        params: c.params ?? null,
        result: c.result ?? null,
      })),
      hasSchemas: true,
    };
  }
  const names = Array.isArray(info.availableCommands) ? info.availableCommands.filter((n) => typeof n === 'string') : [];
  return {
    tools: names.map((n) => ({ name: n, category: null, description: '', params: null, result: null })),
    hasSchemas: false,
  };
}
