import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Lists the tools a connected Unity editor actually supports, with their schemas — learned from the
 * editor's handshake manifest at runtime (ADR 0004), so it reflects that exact instance/version.
 * The discovery half of the generic surface: the agent calls this, then call_unity_tool.
 */
export class ListUnityToolsToolHandler extends BaseToolHandler {
  constructor(unityConnection, manager) {
    super(
      'list_unity_tools',
      'List the tools a connected Unity editor actually supports, with their schemas (learned from the editor at runtime). Discover what call_unity_tool can invoke on a given instance. Returns names + descriptions by default; pass "name" for one tool\'s full parameter schema, or "category" to filter.',
      {
        type: 'object',
        properties: {
          instance: { type: 'string', description: 'Target editor: a project path or port. Omit for the active/default instance.' },
          category: { type: 'string', description: 'Only return tools in this category (e.g. "gameobject", "scene").' },
          name: { type: 'string', description: 'Return the full {name, category, description, params} schema for just this one tool.' },
        },
        required: [],
      },
    );
    this.unityConnection = unityConnection;
    this.manager = manager;
  }

  async execute(params = {}) {
    const conn = this.manager.getConnectionForInstance(params.instance);
    if (!conn) {
      throw new Error(`No Unity instance found for "${params.instance}". Use list_unity_instances to see what's running.`);
    }
    await this.manager.ensureReady(conn);
    const manifest = conn.editorInfo && Array.isArray(conn.editorInfo.commands) ? conn.editorInfo.commands : [];

    if (params.name) {
      const tool = manifest.find((t) => t && t.name === params.name);
      if (!tool) throw new Error(`Tool "${params.name}" is not available on this instance. Use list_unity_tools to see what is.`);
      return { instance: params.instance ?? null, tool };
    }

    let tools = manifest;
    if (params.category) tools = tools.filter((t) => t && t.category === params.category);
    return {
      instance: params.instance ?? null,
      count: tools.length,
      tools: tools.map((t) => ({ name: t.name, category: t.category ?? null, description: t.description ?? '' })),
    };
  }
}
