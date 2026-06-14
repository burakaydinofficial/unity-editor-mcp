import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Handler for saving scenes in Unity
 */
export class SaveSceneToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'save_scene',
      'Save the current scene in Unity',
      {
        type: 'object',
        properties: {
          scenePath: {
            type: 'string',
            description: 'Path where to save the scene. If not provided, saves to current scene path. Required if saveAs is true.'
          },
          saveAs: {
            type: 'boolean',
            description: 'Whether to save as a new scene (creates a copy). Default: false'
          }
        },
        required: []
      }
    );
    this.unityConnection = unityConnection;
  }

  /**
   * Validates the input parameters
   * @param {object} params - Input parameters
   * @throws {Error} If validation fails
   */
  validate(params) {
    // Don't call super.validate() since we have no required fields
    
    // Validate saveAs requires scenePath
    if (params.saveAs && !params.scenePath) {
      throw new Error('scenePath is required when saveAs is true');
    }
  }

  /**
   * Executes the save scene command
   * @param {object} params - Validated input parameters
   * @returns {Promise<object>} Save result
   */
  async execute(params) {
    // Ensure connected
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    
    // sendCommand already unwraps the wire envelope and resolves with the editor
    // payload directly (a handler-level error rejects via isHandlerLevelError).
    // Same fix as load_scene/create_scene — save_scene was missed in batch A.
    const result = await this.unityConnection.sendCommand('save_scene', params);

    // Defensive: surface an error that arrived as a payload field rather than a rejection.
    if (result && result.error) {
      const error = new Error(result.error);
      error.code = 'UNITY_ERROR';
      throw error;
    }

    return result;
  }
}