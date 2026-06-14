import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Handler for stopping Unity play mode
 */
export class StopToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'stop_game',
      'Stop Unity play mode and return to edit mode',
      {
        type: 'object',
        properties: {},
        required: []
      }
    );
    this.unityConnection = unityConnection;
  }

  /**
   * Executes the stop command
   * @param {object} params - Empty object for this command
   * @returns {Promise<object>} Play mode state
   */
  async execute(params) {
    // Ensure connected
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    
    // Send stop command to Unity
    const result = await this.unityConnection.sendCommand('stop_game', params);
    
    // Defensive: surface an error that arrived as a payload field (handler-level
    // errors normally reject in sendCommand). The old result.status check was dead
    // after the R3 envelope change — the unwrapped payload carries no status key.
    if (result && result.error) {
      const error = new Error(result.error);
      error.code = 'UNITY_ERROR';
      throw error;
    }
    
    // Return the result with state information
    return result;
  }
}