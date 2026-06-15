import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Sets a single, curated project setting (writes through PlayerSettings). Scoped to a known,
 * version-safe key set rather than arbitrary reflection writes.
 */
export class SetProjectSettingToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'set_project_setting',
      'Set one project setting by key (PlayerSettings). Supported keys: productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, runInBackground, colorSpace, scriptingDefineSymbols.',
      {
        type: 'object',
        properties: {
          key: {
            type: 'string',
            enum: ['productName', 'companyName', 'bundleVersion', 'defaultScreenWidth', 'defaultScreenHeight', 'runInBackground', 'colorSpace', 'scriptingDefineSymbols'],
            description: 'Which setting to set.'
          },
          value: {
            description: 'New value; type depends on the key (string, number, or boolean).'
          }
        },
        required: ['key', 'value']
      }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('set_project_setting', params);
  }
}
