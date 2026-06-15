import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Returns Unity editor environment info — version, platform, project path, active build target,
 * product/company name, and quick play/compile state. Complements get_editor_state (which is the
 * live play-mode state) with the stable environment/identity of the editor.
 */
export class GetEditorInfoToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_editor_info',
      'Get Unity editor environment info: Unity version, platform, project path, active build target, product/company name, and quick play/compile state.',
      { type: 'object', properties: {}, required: [] }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('get_editor_info', params);
  }
}
