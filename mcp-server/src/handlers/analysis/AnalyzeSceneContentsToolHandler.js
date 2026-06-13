import { BaseToolHandler } from '../base/BaseToolHandler.js';
import { analyzeSceneContentsToolDefinition } from '../../tools/analysis/analyzeSceneContents.js';

/**
 * Handler for analyze_scene_contents.
 *
 * Returns the editor's RAW payload; BaseToolHandler.handle() wraps it once in
 * { status, result } and the server formats it. The previous path delegated to a
 * tool function that returned only `result.summary` inside an MCP `{ content }`
 * shape — which both discarded the full analysis payload and got double-wrapped
 * (the handler's result became `{ content: [...] }` rather than the data).
 */
export class AnalyzeSceneContentsToolHandler extends BaseToolHandler {
    constructor(unityConnection) {
        super(
            analyzeSceneContentsToolDefinition.name,
            analyzeSceneContentsToolDefinition.description,
            analyzeSceneContentsToolDefinition.inputSchema
        );
        this.unityConnection = unityConnection;
    }

    async execute(args) {
        if (!this.unityConnection.isConnected()) {
            throw new Error('Unity connection not available');
        }
        // sendCommand already unwraps the result and rejects on a handler-level error.
        return await this.unityConnection.sendCommand('analyze_scene_contents', args);
    }
}
