import { BaseToolHandler } from '../base/BaseToolHandler.js';
import { editorToolSurface } from '../../core/editorToolSurface.js';
import { mergeNodeLogicSurface } from '../../core/nodeLogicTools.js';
import { roslynManager } from '../../core/roslynManager.js';
import { mergeRoslynSurface } from '../../core/roslynTools.js';

/**
 * Lists the tools a connected Unity editor actually supports, with their schemas — learned from the
 * editor's handshake manifest at runtime (ADR 0004), so it reflects that exact instance/version.
 * The discovery half of the generic surface: the agent calls this, then call_unity_tool. Degrades to
 * names-only (schemasAvailable:false) for an editor running an older package build.
 */
export class ListUnityToolsToolHandler extends BaseToolHandler {
  constructor(manager, roslynMgr = roslynManager) {
    super(
      'list_unity_tools',
      'List the tools a connected Unity editor actually supports, with their schemas (learned from the editor at runtime). Discover what call_unity_tool can invoke on a given instance. Returns names + descriptions by default; pass "name" for one tool\'s full parameter schema AND result-field hints (its response shape — read these to drive call_unity_tool\'s `fields` projection), or "category" to filter.',
      {
        type: 'object',
        properties: {
          instance: { type: 'string', description: 'REQUIRED — the target editor (a project path or port). There is no default instance: every call must name its editor. Use list_unity_instances to see what is running.' },
          category: { type: 'string', description: 'Only return tools in this category (e.g. "gameobject", "scene").' },
          name: { type: 'string', description: 'Return the full {name, category, description, params, result} schema for just this one tool — including its result-field hints (the response shape).' },
        },
        required: ['instance'],
      },
    );
    this.manager = manager;
    this.roslynMgr = roslynMgr;
  }

  async execute(params = {}) {
    const conn = this.manager.requireConnection(params.instance);
    await this.manager.ensureReady(conn);
    // The editor manifest surface, with the Node-logic tools (execute_menu_item/create_script/
    // analyze_screenshot) overridden by their Node handler's schema — the agent-facing contract (ADR 0006).
    const { tools: raw, hasSchemas } = editorToolSurface(conn.editorInfo);
    // + the Roslyn capability commands (lifecycle always-available; gated annotated requires/available
    // per the per-instance backend state) — advertised here, never in the static catalog (ADR 0006).
    const instanceKey = (conn.targetPort != null) ? `${conn.targetHost || 'localhost'}:${conn.targetPort}` : String(params.instance);
    const surface = mergeRoslynSurface(mergeNodeLogicSurface(raw), instanceKey, this.roslynMgr);

    if (params.name) {
      const tool = surface.find((t) => t.name === params.name);
      if (!tool) throw new Error(`Tool "${params.name}" is not available on this instance. Use list_unity_tools to see what is.`);
      return { instance: params.instance ?? null, tool, schemasAvailable: hasSchemas };
    }

    let tools = surface;
    if (params.category) tools = tools.filter((t) => t.category === params.category);
    return {
      instance: params.instance ?? null,
      count: tools.length,
      tools: tools.map((t) => ({ name: t.name, category: t.category ?? null, description: t.description ?? '', ...(t.requires ? { requires: t.requires, available: t.available !== false } : {}) })),
      schemasAvailable: hasSchemas,
    };
  }
}
