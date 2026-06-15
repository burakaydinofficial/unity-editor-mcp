/**
 * Controls which tools appear in MCP `tools/list`. The generic instance meta-tools are always
 * listed (the version-agnostic canonical surface — ADR 0004); the static typed tools are gated by
 * `UNITY_MCP_TYPED_TOOLS` so a client isn't born carrying ~66 definitions it may never use (the
 * context-economics argument behind the generic surface).
 *
 * Hidden typed tools remain *callable* by name (and via call_unity_tool) — this only affects what is
 * advertised in the list, which is what actually costs the model context.
 *
 * `UNITY_MCP_TYPED_TOOLS`: true/1/all/on/yes -> typed tools listed; anything else (or, when unset,
 * the caller's default) -> only the meta-tools. Filtering by tool list, not by registration, so the
 * typed surface stays reachable.
 */
export const META_TOOL_NAMES = new Set([
  'list_unity_instances',
  'list_unity_tools',
  'call_unity_tool',
]);

export function isMetaTool(name) {
  return META_TOOL_NAMES.has(name);
}

/** Whether the static typed tools are advertised. `defaultExposed` applies when the env var is unset. */
export function typedToolsExposed(env = process.env, defaultExposed = true) {
  const raw = (env.UNITY_MCP_TYPED_TOOLS ?? '').trim().toLowerCase();
  if (raw === '') return defaultExposed;
  return raw === 'true' || raw === '1' || raw === 'all' || raw === 'on' || raw === 'yes';
}

/** Filters tool definitions for tools/list: meta-tools always, typed tools per the flag/default. */
export function filterListedTools(definitions, env = process.env, defaultExposed = true) {
  if (typedToolsExposed(env, defaultExposed)) return definitions;
  return definitions.filter((d) => META_TOOL_NAMES.has(d.name));
}
