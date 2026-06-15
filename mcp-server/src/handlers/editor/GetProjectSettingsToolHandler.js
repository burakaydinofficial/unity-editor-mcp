import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Returns a curated, version-safe set of project settings (product/company/version, color space,
 * default screen size, scripting backend, API compatibility level, scripting define symbols, active
 * build target). Read-only.
 */
export class GetProjectSettingsToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_project_settings',
      'Get key Unity project settings: product/company name, bundle version, color space, default screen size, scripting backend, API compatibility level, scripting define symbols, and the active build target.',
      { type: 'object', properties: {}, required: [] }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('get_project_settings', params);
  }
}
