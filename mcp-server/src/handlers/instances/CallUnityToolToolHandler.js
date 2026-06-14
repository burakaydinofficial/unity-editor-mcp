import { BaseToolHandler } from '../base/BaseToolHandler.js';
import { validateAgainstSchema } from '../../core/schemaValidator.js';
import { editorToolSurface } from '../../core/editorToolSurface.js';

/**
 * Invokes any tool a connected Unity editor supports, by name — the generic dispatch half of the
 * version-agnostic surface (ADR 0004). The MCP client cannot validate the inner params (they are an
 * opaque object in this envelope), so the Node side is the sole gate: params are validated against
 * the editor-advertised schema before the call, with precise field-pathed errors for self-correction.
 */
export class CallUnityToolToolHandler extends BaseToolHandler {
  constructor(unityConnection, manager) {
    super(
      'call_unity_tool',
      'Invoke any tool a connected Unity editor supports, by name (discover names + schemas with list_unity_tools). Params are validated against the editor-advertised schema before the call, so a validation error tells you exactly what to fix. Use "instance" to target a specific editor (project path or port); omit it for the active/default one.',
      {
        type: 'object',
        properties: {
          instance: { type: 'string', description: 'Target editor: a project path or port. Omit for the active/default instance.' },
          tool: { type: 'string', description: 'The tool name to invoke (see list_unity_tools).' },
          params: { type: 'object', description: 'Parameters for the tool, matching its advertised schema.' },
        },
        required: ['tool'],
      },
    );
    this.unityConnection = unityConnection;
    this.manager = manager;
  }

  validate(params) {
    super.validate(params); // 'tool' is required
    if (typeof params.tool !== 'string' || params.tool.trim() === '') {
      throw new Error('tool must be a non-empty string (the tool name to invoke; see list_unity_tools)');
    }
  }

  async execute(params = {}) {
    const conn = this.manager.getConnectionForInstance(params.instance);
    if (!conn) {
      throw new Error(`No Unity instance found for "${params.instance}". Use list_unity_instances to see what's running.`);
    }
    await this.manager.ensureReady(conn);
    const { tools } = editorToolSurface(conn.editorInfo);
    const entry = tools.find((t) => t.name === params.tool);
    if (!entry) {
      throw new Error(`Tool "${params.tool}" is not available on this instance. Use list_unity_tools to see what is.`);
    }

    const callParams = params.params || {};
    // Validate against the editor-advertised schema when one is known. An editor running an older
    // package build advertises names only (entry.params === null) — pass the params through
    // unvalidated, exactly as a typed tool does (the editor itself validates). Graceful degradation.
    if (entry.params) {
      const { valid, errors } = validateAgainstSchema(callParams, entry.params, 'params');
      if (!valid) {
        const err = new Error(`Invalid params for "${params.tool}": ${errors.join('; ')}`);
        err.code = 'INVALID_PARAMS';
        throw err;
      }
    }

    // Routes to the resolved instance's connection (ADR 0005). sendCommand unwraps the result and
    // rejects on a wire/handler error, so the generic call surfaces the same outcome as a typed tool.
    return await conn.sendCommand(params.tool, callParams);
  }
}
