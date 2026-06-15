import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Finds textual (syntactic) references to an identifier across the project's Assets scripts —
 * comments and string literals are excluded, but there is no semantic resolution.
 */
export class FindReferencesToolHandler extends BaseToolHandler {
  constructor(unityConnection) {
    super(
      'find_references',
      'Find textual references to an identifier across the project Assets scripts (comments and string literals excluded). Syntactic: same-named members across types/overloads are not disambiguated. Returns file, line, and the matching line text.',
      {
        type: 'object',
        properties: {
          name: { type: 'string', description: 'Identifier to search for (word-boundary match).' },
          maxResults: { type: 'number', description: 'Maximum references to return (default 200).' }
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
    return await this.unityConnection.sendCommand('find_references', params);
  }
}
