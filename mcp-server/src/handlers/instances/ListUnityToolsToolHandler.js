import { BaseToolHandler } from '../base/BaseToolHandler.js';
import { editorToolSurface } from '../../core/editorToolSurface.js';
import { mergeNodeLogicSurface } from '../../core/nodeLogicTools.js';

/**
 * Lists the tools a connected Unity editor actually supports, with their schemas — learned from the
 * editor's handshake manifest at runtime (ADR 0004), so it reflects that exact instance/version.
 * The discovery half of the generic surface: the agent calls this, then call_unity_tool. Degrades to
 * names-only (schemasAvailable:false) for an editor running an older package build.
 */
export class ListUnityToolsToolHandler extends BaseToolHandler {
  constructor(unityConnection, manager) {
    super(
      'list_unity_tools',
      'List the tools a connected Unity editor actually supports, with their schemas (learned from the editor at runtime). Discover what call_unity_tool can invoke on a given instance. Returns names + descriptions by default; pass "name" for one tool\'s full parameter schema, or "category" to filter.',
      {
        type: 'object',
        properties: {
          instance: { type: 'string', description: 'REQUIRED — the target editor (a project path or port). There is no default instance: every call must name its editor. Use list_unity_instances to see what is running.' },
          category: { type: 'string', description: 'Only return tools in this category (e.g. "gameobject", "scene").' },
          name: { type: 'string', description: 'Return the full {name, category, description, params} schema for just this one tool.' },
        },
        required: ['instance'],
      },
    );
    this.unityConnection = unityConnection;
    this.manager = manager;
  }

  async execute(params = {}) {
    const conn = this.manager.requireConnection(params.instance);
    await this.manager.ensureReady(conn);
    // The editor manifest surface, with the Node-logic tools (execute_menu_item/create_script/
    // analyze_screenshot) overridden by their Node handler's schema — the agent-facing contract (ADR 0006).
    const { tools: raw, hasSchemas } = editorToolSurface(conn.editorInfo);
    const surface = mergeNodeLogicSurface(raw);

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
      tools: tools.map((t) => ({ name: t.name, category: t.category ?? null, description: t.description ?? '' })),
      schemasAvailable: hasSchemas,
    };
  }
}
