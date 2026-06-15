import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Quits the Unity editor (for CI / automation). The exit is deferred one tick so the success
 * response flushes first; the bridge connection will then drop as the editor process exits.
 */
export class QuitEditorToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'quit_editor',
      'Quit the Unity editor (intended for CI/automation). The editor exits after the response is sent, so the connection will drop. Unsaved changes are NOT saved.',
      { type: 'object', properties: {}, required: [] }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('quit_editor', params);
  }
}
