import { BaseToolHandler } from '../base/BaseToolHandler.js';
import * as discovery from '../../core/discovery.js';

/**
 * Lists the Unity editor instances discoverable in the per-user registry
 * (ADR 0003 / 0004). LOCAL-ONLY: it reads the filesystem registry, never the
 * wire — the one tool that works with no editor connected, and the foundation of
 * the capability-driven surface (the agent picks an instance, then targets it).
 *
 * The discovery functions are injected (default = the real module) so the handler
 * is unit-tested without a filesystem or a live editor — the same dependency-
 * injection idiom the other handlers use for `unityConnection`.
 */
export class ListUnityInstancesToolHandler extends BaseToolHandler {
  constructor(unityConnection, deps = discovery) {
    super(
      'list_unity_instances',
      'List the Unity editor instances currently running and discoverable (project path, Unity version, port, and which one this server targets by default). Use this to see what editors are available before acting; works even when no editor is connected.',
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
    this.unityConnection = unityConnection;
    this.deps = deps;
  }

  async execute(params = {}) {
    const env = process.env;
    const registryDir = this.deps.defaultRegistryDirectory(env);
    const all = this.deps.readInstances(registryDir);

    let activePort = null;
    try {
      activePort = this.deps.resolveUnityPort(env);
    } catch {
      activePort = null; // discovery should never break a read-only listing
    }

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
        active: Number.isFinite(d.port) && activePort != null && d.port === activePort,
      }))
      .sort((a, b) => String(a.projectPath).localeCompare(String(b.projectPath)));

    return { instances, count: instances.length, registryDir, activePort };
  }
}
