import { BaseToolHandler } from '../base/BaseToolHandler.js';
import * as discovery from '../../core/discovery.js';

/**
 * Lists the Unity editor instances discoverable in the per-user registry (ADR 0003 / 0004).
 * LOCAL-ONLY: it reads the filesystem registry, never the wire — the one tool that works with no
 * editor connected, and the entry point of the surface: the agent picks an instance here, then names
 * it as the required `instance` on list_unity_tools / call_unity_tool (ADR 0006 — there is no default).
 *
 * The discovery functions are injected (default = the real module) so the handler is unit-tested
 * without a filesystem or a live editor.
 */
export class ListUnityInstancesToolHandler extends BaseToolHandler {
  constructor(manager, deps = discovery) {
    super(
      'list_unity_instances',
      'List the Unity editor instances currently running and discoverable (project path, Unity version, port). Use this to see what editors are available, then pass an instance\'s project path or port as the required "instance" on list_unity_tools / call_unity_tool. Works even when no editor is connected.',
      {
        type: 'object',
        properties: {
          includeStale: {
            type: 'boolean',
            description: 'Also include descriptors whose process is gone / heartbeat is stale (for diagnosing a missing editor). Default: false.',
          },
        },
        required: [],
      },
    );
    this.manager = manager;
    this.deps = deps;
  }

  async execute(params = {}) {
    const env = process.env;
    // Opportunistic cleanup: drop pooled connections whose editor is no longer live (their reconnect
    // timers would otherwise linger for the server's lifetime). This read-only listing is the natural
    // call-site since it already inspects the live registry. (Audit finding — prune() was never called.)
    if (this.manager && typeof this.manager.prune === 'function') this.manager.prune();
    const registryDir = this.deps.defaultRegistryDirectory(env);
    const all = this.deps.readInstances(registryDir);

    const includeStale = params.includeStale === true;
    const instances = all
      .map((d) => ({ d, live: this.deps.isLive(d) }))
      .filter((r) => includeStale || r.live)
      .map(({ d, live }) => ({
        projectPath: d.projectPath,
        unityVersion: d.unityVersion ?? null,
        port: Number.isFinite(d.port) ? d.port : null,
        pid: Number.isFinite(d.pid) ? d.pid : null,
        protocolVersion: d.protocolVersion ?? null,
        host: d.host ?? null,
        startedAt: d.startedAt ?? null,
        lastHeartbeat: d.lastHeartbeat ?? null,
        live,
      }))
      .sort((a, b) => String(a.projectPath).localeCompare(String(b.projectPath)));

    return { instances, count: instances.length, registryDir };
  }
}
