import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Lists the project's UPM packages: the directly-requested `dependencies` (from Packages/manifest.json)
 * and the full `resolved` set with each package's source (from Packages/packages-lock.json). Read-only,
 * file-based — no async PackageManager API, so it works the same on every editor version.
 */
export class ListPackagesToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'list_packages',
      'List the Unity project\'s UPM packages: directly-requested dependencies (from manifest.json) plus the full resolved set with each package\'s source (from packages-lock.json).',
      { type: 'object', properties: {}, required: [] }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('list_packages', params);
  }
}
