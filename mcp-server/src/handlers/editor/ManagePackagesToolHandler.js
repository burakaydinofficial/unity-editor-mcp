import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Adds or removes a UPM package. The PackageManager resolves asynchronously (the editor recompiles /
 * reloads), so this returns once the request is queued — verify the outcome with list_packages.
 */
export class ManagePackagesToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'manage_packages',
      'Add or remove a UPM package. Resolution is asynchronous (the editor recompiles/reloads); the call returns once the request is queued — verify with list_packages afterwards.',
      {
        type: 'object',
        properties: {
          action: {
            type: 'string',
            enum: ['add', 'remove'],
            description: 'Whether to add or remove the package.'
          },
          packageId: {
            type: 'string',
            description: 'Package identifier, e.g. "com.unity.textmeshpro" or "com.unity.textmeshpro@3.0.6" or a git URL.'
          }
        },
        required: ['action', 'packageId']
      }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('manage_packages', params);
  }
}
