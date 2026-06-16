/**
 * The Roslyn command registry + routing (ADR 0006 capability handshake). These commands are NOT in the
 * static protocol catalog — they are dynamic, per-instance, sidecar-dependent (see the Plan 2 design
 * refinement: getServerTools() couples the server-catalog to the MCP ListTools surface, so cataloging
 * them would break the 3-meta-tool surface). list_unity_tools advertises them per instance;
 * call_unity_tool routes them here.
 */

/** Sentinel: this tool is not a Roslyn command (or find_references with no sidecar) — let the caller fall through. */
export const NOT_HANDLED = Symbol('roslyn:not-handled');

const objSchema = (props = {}, required = []) => ({ type: 'object', properties: props, required });

// Lifecycle — always available (they manage the backend itself).
export const ROSLYN_LIFECYCLE = {
  start_roslyn: {
    description: 'Activate the Roslyn semantic backend for this instance (spawns the sidecar; async — poll roslyn_status). Returns "unavailable" if the backend is not installed.',
    inputSchema: objSchema(),
  },
  stop_roslyn: { description: 'Tear down the Roslyn sidecar for this instance.', inputSchema: objSchema() },
  roslyn_status: { description: 'Report the Roslyn backend state for this instance (off | indexing | ready | unavailable).', inputSchema: objSchema() },
};

// Gated — advertised always (so the agent learns they exist + that start_roslyn unlocks them) but only
// invokable when ready. Params mirror the spec's command surface (§4).
export const ROSLYN_GATED = {
  goto_definition: {
    description: 'Semantic go-to-definition (overload-resolved). Requires the Roslyn backend (start_roslyn).',
    inputSchema: objSchema({ path: { type: 'string' }, position: { type: 'object' } }, ['path', 'position']),
  },
  rename_symbol: {
    description: 'Cross-file safe rename. dryRun returns the edit set without writing. Requires the Roslyn backend.',
    inputSchema: objSchema({ path: { type: 'string' }, position: { type: 'object' }, newName: { type: 'string' }, dryRun: { type: 'boolean' } }, ['path', 'position', 'newName']),
  },
  get_diagnostics: {
    description: 'Compiler errors/warnings for a file or the whole compilation. Requires the Roslyn backend.',
    inputSchema: objSchema({ path: { type: 'string' } }),
  },
  get_type_hierarchy: {
    description: 'Base / derived / implemented types across the compilation. Requires the Roslyn backend.',
    inputSchema: objSchema({ typeName: { type: 'string' } }, ['typeName']),
  },
};

export const isRoslynLifecycle = (name) => Object.prototype.hasOwnProperty.call(ROSLYN_LIFECYCLE, name);
export const isRoslynGated = (name) => Object.prototype.hasOwnProperty.call(ROSLYN_GATED, name);
/** True for commands roslynTools OWNS outright (lifecycle + gated). find_references is editor-owned + handled specially. */
export const isRoslynCommand = (name) => isRoslynLifecycle(name) || isRoslynGated(name);

/** Append the Roslyn commands to a per-instance tool surface, annotated requires/available. */
export function mergeRoslynSurface(surface, instanceKey, roslynMgr) {
  const ready = roslynMgr.isReady(instanceKey);
  const lifecycle = Object.entries(ROSLYN_LIFECYCLE).map(([name, d]) => ({
    name, category: 'roslyn', description: d.description, params: d.inputSchema, available: true,
  }));
  const gated = Object.entries(ROSLYN_GATED).map(([name, d]) => ({
    name, category: 'roslyn', description: d.description, params: d.inputSchema, requires: 'roslyn', available: ready,
  }));
  return [...surface, ...lifecycle, ...gated];
}

/**
 * Route a tool through the Roslyn layer. Returns NOT_HANDLED for anything this layer does not own
 * (so call_unity_tool continues to its Node-logic/editor paths). `conn` carries the resolved connection.
 */
export async function roslynDispatch(tool, params, conn, instanceKey, roslynMgr) {
  if (isRoslynLifecycle(tool)) {
    if (tool === 'start_roslyn') {
      const state = await roslynMgr.start(instanceKey, conn);
      const { error } = roslynMgr.statusOf(instanceKey);
      return { instance: instanceKey, state, ...(error ? { error } : {}) };
    }
    if (tool === 'stop_roslyn') { await roslynMgr.stop(instanceKey); return { instance: instanceKey, state: 'off' }; }
    return { instance: instanceKey, ...roslynMgr.statusOf(instanceKey) }; // roslyn_status
  }
  if (isRoslynGated(tool)) {
    if (!roslynMgr.isReady(instanceKey)) {
      const err = new Error('Roslyn backend not ready for this instance — call start_roslyn first (ROSLYN_NOT_READY).');
      err.code = 'ROSLYN_NOT_READY';
      throw err;
    }
    return await roslynMgr.client(instanceKey).call(tool, params);
  }
  if (tool === 'find_references' && roslynMgr.isReady(instanceKey)) {
    return await roslynMgr.client(instanceKey).call('find_references', params); // semantic upgrade
  }
  return NOT_HANDLED; // not ours (or find_references with no sidecar → editor syntactic)
}
