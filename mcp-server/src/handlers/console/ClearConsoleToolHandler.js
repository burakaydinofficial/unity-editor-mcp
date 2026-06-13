import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Handler for clearing Unity Editor console logs
 */
export class ClearConsoleToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'clear_console',
      'Clear Unity Editor console logs',
      {
        type: 'object',
        properties: {
          clearOnPlay: {
            type: 'boolean',
            default: true,
            description: 'Clear console when entering play mode'
          },
          clearOnRecompile: {
            type: 'boolean',
            default: true,
            description: 'Clear console on script recompilation'
          },
          clearOnBuild: {
            type: 'boolean',
            default: true,
            description: 'Clear console when building'
          },
          preserveWarnings: {
            type: 'boolean',
            default: false,
            description: 'Preserve warning messages when clearing'
          },
          preserveErrors: {
            type: 'boolean',
            default: false,
            description: 'Preserve error messages when clearing'
          }
        },
        required: []
      }
    );
    
    this.unityConnection = unityConnection;
  }

  /**
   * Validates the input parameters
   * @param {Object} params - The input parameters
   * @throws {Error} If validation fails
   */
  validate(params) {
    const {
      clearOnPlay,
      clearOnRecompile,
      clearOnBuild,
      preserveWarnings,
      preserveErrors
    } = params;

    // Validate boolean parameters
    if (clearOnPlay !== undefined && typeof clearOnPlay !== 'boolean') {
      throw new Error('clearOnPlay must be a boolean');
    }

    if (clearOnRecompile !== undefined && typeof clearOnRecompile !== 'boolean') {
      throw new Error('clearOnRecompile must be a boolean');
    }

    if (clearOnBuild !== undefined && typeof clearOnBuild !== 'boolean') {
      throw new Error('clearOnBuild must be a boolean');
    }

    if (preserveWarnings !== undefined && typeof preserveWarnings !== 'boolean') {
      throw new Error('preserveWarnings must be a boolean');
    }

    if (preserveErrors !== undefined && typeof preserveErrors !== 'boolean') {
      throw new Error('preserveErrors must be a boolean');
    }

    // Validate logical consistency
    const isClearing = clearOnPlay !== false || clearOnRecompile !== false || clearOnBuild !== false;
    const isPreserving = preserveWarnings === true || preserveErrors === true;
    
    if (isPreserving && !isClearing) {
      throw new Error('Cannot preserve specific log types when not clearing console');
    }
  }

  /**
   * Executes the console clear operation
   * @param {Object} params - The input parameters
   * @returns {Promise<Object>} The result of the clear operation
   */
  async execute(params) {
    const {
      clearOnPlay = true,
      clearOnRecompile = true,
      clearOnBuild = true,
      preserveWarnings = false,
      preserveErrors = false
    } = params;

    // Ensure connection to Unity
    if (!this.unityConnection.isConnected()) {
      await this.unityConnection.connect();
    }

    // Prepare command parameters
    const commandParams = {
      clearOnPlay,
      clearOnRecompile,
      clearOnBuild,
      preserveWarnings,
      preserveErrors
    };

    // Send command to Unity
    const response = await this.unityConnection.sendCommand('clear_console', commandParams);

    // Handle Unity response
    if (response.success === false) {
      throw new Error(response.error || 'Failed to clear console');
    }

    // The nine required fields are defaulted (the editor may omit some), so the
    // response is always contract-valid rather than conditionally dropping them.
    const result = {
      success: response.success !== false,
      message: response.message || 'Console cleared successfully',
      clearedCount: response.clearedCount ?? 0,
      remainingCount: response.remainingCount ?? 0,
      settingsUpdated: response.settingsUpdated ?? false,
      // Default to the values we SENT (which default to true), not false — otherwise an
      // omitted echo would report the opposite of what was requested/applied.
      clearOnPlay: response.clearOnPlay ?? clearOnPlay,
      clearOnRecompile: response.clearOnRecompile ?? clearOnRecompile,
      clearOnBuild: response.clearOnBuild ?? clearOnBuild,
      timestamp: response.timestamp || new Date().toISOString()
    };
    // Optional extras the editor sends when preserving logs (catalog: not required).
    if (response.preservedWarnings !== undefined) result.preservedWarnings = response.preservedWarnings;
    if (response.preservedErrors !== undefined) result.preservedErrors = response.preservedErrors;
    return result;
  }
}