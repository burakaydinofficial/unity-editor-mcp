import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Finds C# symbol declarations by name across the project's Assets scripts (syntactic).
 */
export class FindSymbolToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'find_symbol',
      'Find C# symbol declarations by exact name across the project Assets scripts. Returns each match\'s file, line range, kind, and signature. Syntactic (no semantic resolution).',
      {
        type: 'object',
        properties: {
          name: { type: 'string', description: 'Exact symbol name to find.' },
          kind: {
            type: 'string',
            enum: ['class', 'struct', 'interface', 'enum', 'method', 'property'],
            description: 'Optional: only return declarations of this kind.'
          },
          maxResults: { type: 'number', description: 'Maximum matches to return (default 200).' }
        },
        required: ['name']
      }
    );
    this.unityConnection = unityConnection;
  }

  async execute(params) {
    if (!this.unityConnection.isConnected()) {
      throw new Error('Unity connection not available');
    }
    return await this.unityConnection.sendCommand('find_symbol', params);
  }
}
