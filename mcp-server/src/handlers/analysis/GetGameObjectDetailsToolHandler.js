import { BaseToolHandler } from '../base/BaseToolHandler.js';
import { getGameObjectDetailsToolDefinition } from '../../tools/analysis/getGameObjectDetails.js';

/**
 * Handler for get_gameobject_details.
 *
 * Returns the editor's RAW payload; BaseToolHandler.handle() wraps it once. The
 * previous path returned only `result.summary` in an MCP `{ content }` shape,
 * discarding the component/detail payload and double-wrapping the result.
 */
export class GetGameObjectDetailsToolHandler extends BaseToolHandler {
    constructor(unityConnection) {
        super(
            getGameObjectDetailsToolDefinition.name,
            getGameObjectDetailsToolDefinition.description,
            getGameObjectDetailsToolDefinition.inputSchema
        );
        this.unityConnection = unityConnection;
    }

    async execute(args) {
        if (!this.unityConnection.isConnected()) {
            throw new Error('Unity connection not available');
        }
        // sendCommand already unwraps the result and rejects on a handler-level error.
        return await this.unityConnection.sendCommand('get_gameobject_details', args);
    }
}
