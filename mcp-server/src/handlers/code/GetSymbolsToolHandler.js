import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Returns a syntactic outline of a C# file: its types, methods, and properties with line ranges.
 */
export class GetSymbolsToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_symbols',
      'Outline a C# file: its types (class/struct/interface/enum), methods, and properties with line ranges. Syntactic (no semantic resolution). Accepts an "Assets/..." path or an absolute .cs path under the project.',
      {
        type: 'object',
        properties: {
          path: { type: 'string', description: 'Path to the .cs file ("Assets/..." or absolute).' }
        },
        required: ['path']
      }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('get_symbols', params);
  }
}
