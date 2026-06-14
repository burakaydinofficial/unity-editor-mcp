import { BaseToolHandler } from '../base/BaseToolHandler.js';

/**
 * Sets which Unity editor is the default target for calls that don't name an instance (ADR 0005).
 * Lets a multi-editor session pin its working instance once instead of passing `instance` every call.
 */
export class SetActiveUnityInstanceToolHandler extends BaseToolHandler {
  constructor(unityConnection, manager) {
    super(
      'set_active_unity_instance',
      'Set which Unity editor is the default target for tool calls that do not name an "instance". Pass a project path or port; omit "instance" to reset to the auto-resolved default. Use list_unity_instances to see the options.',
      {
        type: 'object',
        properties: {
          instance: { type: 'string', description: 'Project path or port to make active. Omit to reset to the auto-resolved default.' },
        },
        required: [],
      },
    );
    this.unityConnection = unityConnection;
    this.manager = manager;
  }

  async execute(params = {}) {
    const ref = params.instance === undefined || params.instance === '' ? null : params.instance;
    const target = this.manager.setActiveInstance(ref);
    if (ref !== null && !target) {
      throw new Error(`No Unity instance found for "${params.instance}". Use list_unity_instances to see what's running.`);
    }
    return {
      active: target, // {host, port} or null (auto-resolved default)
      message: target
        ? `Active instance set to ${target.host}:${target.port}`
        : 'Active instance reset to the auto-resolved default',
    };
  }
}
