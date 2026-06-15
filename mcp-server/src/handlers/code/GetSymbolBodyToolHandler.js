import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Returns the source text of a named symbol within a C# file (declaration through matching brace).
 */
export class GetSymbolBodyToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'get_symbol_body',
      'Return the source text of a named symbol (type/method/property) within a C# file — the declaration through its matching closing brace. Syntactic. Accepts an "Assets/..." path or an absolute .cs path under the project.',
      {
        type: 'object',
        properties: {
          path: { type: 'string', description: 'Path to the .cs file ("Assets/..." or absolute).' },
          name: { type: 'string', description: 'Exact symbol name within the file.' }
        },
        required: ['path', 'name']
      }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('get_symbol_body', params);
  }
}
