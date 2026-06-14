import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Handler for getting Unity editor state
 */
export class GetEditorStateToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_editor_state',
      'Get current Unity editor state including play mode status',
      {
        type: 'object',
        properties: {},
        required: []
      }
    );
    this.unityConnection = unityConnection;
  }

  /**
   * Executes the get editor state command
   * @param {object} params - Empty object for this command
   * @returns {Promise<object>} Editor state information
   */
  async execute(params) {
    // Ensure connected
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    
    // Send get state command to Unity
    const result = await this.unityConnection.sendCommand('get_editor_state', params);
    
    // Defensive: surface an error that arrived as a payload field (handler-level
    // errors normally reject in sendCommand). The old result.status check was dead
    // after the R3 envelope change — the unwrapped payload carries no status key.
    if (result && result.error) {
      const error = new Error(result.error);
      error.code = 'UNITY_ERROR';
      throw error;
    }
    
    // Return the state information
    return result;
  }
}