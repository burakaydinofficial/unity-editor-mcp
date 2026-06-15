import { BaseToolHandler } from '../base/BaseToolHandler.js';
import { validateAgainstSchema } from '../../core/schemaValidator.js';
import { editorToolSurface } from '../../core/editorToolSurface.js';
import { NODE_LOGIC_TOOLS, isNodeLogicTool } from '../../core/nodeLogicTools.js';

/**
 * Invokes any tool a connected Unity editor supports, by name — the generic dispatch half of the
 * version-agnostic surface (ADR 0004). The MCP client cannot validate the inner params (they are an
 * opaque object in this envelope), so the Node side is the sole gate: params are validated against
 * the editor-advertised schema before the call, with precise field-pathed errors for self-correction.
 */
export class CallUnityToolToolHandler extends BaseToolHandler {
  constructor(manager) {
    super(
      'call_unity_tool',
      'Invoke any tool a connected Unity editor supports, by name (discover names + schemas with list_unity_tools). Params are validated against the editor-advertised schema before the call, so a validation error tells you exactly what to fix. The "instance" (a project path or port) is required — there is no default editor, so every call names its target. To trim the response, pass params.fields — an array of dot-paths (GraphQL-style); omit for the full result.',
      {
        type: 'object',
        properties: {
          instance: { type: 'string', description: 'REQUIRED — the target editor (a project path or port). There is no default instance: every call must name its editor. Use list_unity_instances to see what is running.' },
          tool: { type: 'string', description: 'The tool name to invoke (see list_unity_tools).' },
          params: { type: 'object', description: 'Parameters for the tool, matching its advertised schema. Also accepts an optional reserved "fields": a string[] of dot-paths that projects the result to just those fields (e.g. ["count","objects.name","state.isPlaying"]); arrays are transparent (the path applies to each element). Omit for all fields. Discover the shape by calling once without "fields".' },
        },
        required: ['instance', 'tool'],
      },
    );
    this.manager = manager;
  }

  validate(params) {
    super.validate(params); // 'tool' is required
    if (typeof params.tool !== 'string' || params.tool.trim() === '') {
      throw new Error('tool must be a non-empty string (the tool name to invoke; see list_unity_tools)');
    }
  }

  async execute(params = {}) {
    const conn = this.manager.requireConnection(params.instance);
    await this.manager.ensureReady(conn);
    const callParams = params.params || {};
    // Guard the input boundary: a truthy non-object `params` (e.g. a string or array) would otherwise
    // spread into a char-indexed object that passes schema validation, then reach the editor as a
    // non-object payload. (Audit finding — the MCP SDK does not enforce the inputSchema.)
    if (typeof callParams !== 'object' || Array.isArray(callParams)) {
      throw new Error('params must be an object (a map of the tool\'s parameters)');
    }

    // Node-logic tools (security normalization / template generation / base64 analysis — ADR 0006) are
    // dispatched to a Node handler BOUND TO THE RESOLVED CONNECTION rather than forwarded raw to the
    // editor. The handler validates against its own schema (its agent-facing contract) and returns its
    // raw result, which this tool then surfaces like any other.
    if (isNodeLogicTool(params.tool)) {
      const HandlerClass = NODE_LOGIC_TOOLS[params.tool].handler;
      const h = new HandlerClass(conn);
      h.validate(callParams);
      return await h.execute(callParams);
    }

    const { tools, hasSchemas } = editorToolSurface(conn.editorInfo);
    const entry = tools.find((t) => t.name === params.tool);
    if (!entry) {
      throw new Error(`Tool "${params.tool}" is not available on this instance. Use list_unity_tools to see what is.`);
    }

    // When the editor advertises a rich manifest the Node side is the SOLE gate, so it ALWAYS
    // validates — a per-entry missing schema defaults to {type:object} (as the pre-degradation code
    // did). Only a names-only editor (older build, hasSchemas:false) skips validation and passes
    // through; the editor validates there. Gating on the manifest MODE — not on a per-entry params —
    // stops a rich-manifest entry that merely omits its schema from silently bypassing the gate.
    // (Delta-audit finding.)
    if (hasSchemas) {
      // `fields` is a reserved protocol meta-param (GraphQL-style result projection) honored by every
      // command and described by no per-command schema — exclude it from per-command param validation
      // (so it survives even a command that sets additionalProperties:false). It still rides through to
      // the editor in callParams below.
      const toValidate = { ...callParams };
      delete toValidate.fields;
      const { valid, errors } = validateAgainstSchema(toValidate, entry.params || { type: 'object' }, 'params');
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
