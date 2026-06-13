import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Handler for creating new scenes in Unity
 */
export class CreateSceneToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'create_scene',
      'Create a new scene in Unity',
      {
        type: 'object',
        properties: {
          sceneName: {
            type: 'string',
            description: 'Name of the scene to create'
          },
          path: {
            type: 'string',
            description: 'Path where the scene should be saved (e.g., "Assets/Scenes/"). If not specified, defaults to "Assets/Scenes/"'
          },
          loadScene: {
            type: 'boolean',
            description: 'Whether to load the scene after creation (default: true)'
          },
          addToBuildSettings: {
            type: 'boolean',
            description: 'Whether to add the scene to build settings (default: false)'
          }
        },
        required: ['sceneName']
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
    super.validate(params);
    
    // Additional validation for scene name
    if (!params.sceneName || params.sceneName.trim() === '') {
      throw new Error('Scene name cannot be empty');
    }
    
    // Check for invalid characters in scene name
    if (params.sceneName.includes('/') || params.sceneName.includes('\\')) {
      throw new Error('Scene name contains invalid characters');
    }
  }

  /**
   * Executes the create scene command
   * @param {object} params - Validated input parameters
   * @returns {Promise<object>} Creation result
   */
  async execute(params) {
    // Ensure connected
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    
    // sendCommand already unwraps the wire envelope and resolves with the editor
    // payload directly (a handler-level error rejects via isHandlerLevelError).
    const result = await this.unityConnection.sendCommand('create_scene', params);

    // Defensive: surface an error that arrived as a payload field rather than a rejection.
    if (result && result.error) {
      const error = new Error(result.error);
      error.code = 'UNITY_ERROR';
      throw error;
    }

    return result;
  }
}