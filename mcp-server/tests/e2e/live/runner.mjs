// Editor lifecycle + suite entry for the live E2E harness. Launches ONE headed editor on the warm e2e-host, drives
// the flows through the MCP chain, cleans up. Env: UNITY_PATH (required), E2E_HOST_PROJECT (default ci/e2e-host),
// E2E_TIMEOUT (bridge-ready bound, ms). Run: `UNITY_PATH=<editor> node tests/e2e/live/runner.mjs [--flow=playmode|scripts] [--selfcheck]`.
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { waitForBridge } from './waitForBridge.mjs';
import { readProbeEvents } from './verify.mjs';
import { McpDriver } from './mcpDriver.mjs';

// Launch a HEADED editor (no -batchmode — play-mode freezes in batch). The launched Unity inherits E2E_PROBE_FILE so
// the in-editor probe writes there. Returns { proc, port } once the bridge's TCP listener is armed.
// Robust teardown: on Windows proc.kill() terminates only the ROOT process; Unity spawns child workers
// (AssetImportWorker, shader/compiler helpers) that keep holding the project lock + TCP port. Tree-kill and wait for
// actual exit so the NEXT run can open the host.
async function killEditor(proc) {
  if (!proc || proc.exitCode != null || proc.signalCode != null) return;
  if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(proc.pid), '/T', '/F']);
  else proc.kill('SIGKILL');
  await Promise.race([once(proc, 'exit'), new Promise(r => setTimeout(r, 15000))]);
}

export async function launchEditor({ unityPath, hostPath, logPath, probeFile }) {
  writeFileSync(probeFile, ''); // truncate probe scratch for this run
  const proc = spawn(unityPath, ['-projectPath', hostPath, '-logFile', logPath], {
    env: { ...process.env, E2E_PROBE_FILE: probeFile },
    stdio: 'ignore',
  });
  try {
    const port = await waitForBridge(logPath, { timeoutMs: Number(process.env.E2E_TIMEOUT ?? 180000) });
    return { proc, port };
  } catch (e) {
    await killEditor(proc); // bridge never came up — don't orphan the headed editor (it holds the lock + port)
    throw e;
  }
}

export async function shutdown(state) {
  // Kill the editor FIRST (tree-kill + wait) so its children release the host before we exit + clean up.
  if (state.editor) { try { await killEditor(state.editor.proc); } catch { /* already gone */ } }
  try { await state.driver?.stop(); } catch { /* ignore */ }
}

async function loadFlow(name) {
  return (await import(`./flows/${name}.mjs`)).run;
}

async function main() {
  const unityPath = process.env.UNITY_PATH;
  // Default host = repo-root/ci/e2e-host, resolved relative to THIS file (the CWD varies — do NOT resolve against it).
  const hostPath = process.env.E2E_HOST_PROJECT
    ? resolve(process.env.E2E_HOST_PROJECT)
    : resolve(dirname(fileURLToPath(import.meta.url)), '../../../../ci/e2e-host');
  if (!unityPath) { console.error('UNITY_PATH is required (the Unity editor executable)'); process.exit(2); }

  const scratch = mkdtempSync(join(tmpdir(), 'e2e-live-'));
  const probeFile = join(scratch, 'probe.jsonl');
  const logPath = join(scratch, 'editor.log');
  const flagFlow = process.argv.find(a => a.startsWith('--flow='));
  const which = flagFlow ? flagFlow.split('=')[1] : 'all';
  const selfcheck = process.argv.includes('--selfcheck');
  if (!['all', 'playmode', 'scripts', 'selfcheck'].includes(which)) {
    console.error(`unknown --flow=${which} (expected: all | playmode | scripts | selfcheck)`);
    process.exit(2); // never silently pass on a typo'd flow (the whole point of the negative controls)
  }

  const state = {};
  let failed = 0;
  try {
    console.log(`launching editor on ${hostPath} ...`);
    state.editor = await launchEditor({ unityPath, hostPath, logPath, probeFile });
    console.log(`bridge up on port ${state.editor.port}; connecting driver ...`);
    state.driver = new McpDriver();
    await state.driver.start({ port: state.editor.port });

    // Probe liveness: the [InitializeOnLoad] probe must have written probeLoaded during editor boot. Assert it HERE —
    // before the per-flow truncation below wipes it — so a dead probe fails fast instead of failing every flow.
    if (!readProbeEvents(probeFile).some(e => e.event === 'probeLoaded')) {
      throw new Error('probe not live: no probeLoaded event in E2E_PROBE_FILE (is E2EProbe.cs in the host project?)');
    }

    const ctx = { driver: state.driver, hostPath, probeFile, logPath };
    const names = [];
    if (which === 'all' || which === 'selfcheck') names.push('selfcheck'); // negative controls first: prove we CAN fail
    if (which === 'all' || which === 'playmode') names.push('playmode');
    if (which === 'all' || which === 'scripts') names.push('scripts');
    if (selfcheck && which !== 'all' && which !== 'selfcheck') names.unshift('selfcheck');
    if (selfcheck) names.push('playmode'); // reconnect stability: a second reload cycle through the one editor
    if (names.length === 0) throw new Error('no flows selected'); // guard against a silent zero-flow pass

    for (const name of names) {
      const run = await loadFlow(name);
      writeFileSync(probeFile, ''); // per-flow truncation (Task-4 audit): each run's probe assertions are independent
      try { await run(ctx); console.log(`PASS ${run.flowName}`); }
      catch (e) { failed++; console.error(`FAIL ${run.flowName}: ${e.message}`); }
    }
  } catch (e) {
    failed++; console.error(`FATAL: ${e.message}`);
  } finally {
    await shutdown(state);
    if (process.env.E2E_KEEP) {
      console.log(`E2E_KEEP set — scratch retained: ${scratch} (editor log: ${logPath}, probe: ${probeFile})`);
    } else {
      // Windows may briefly hold the editor log after kill; never let cleanup failure fail the run.
      try { rmSync(scratch, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch { /* OS reclaims temp */ }
    }
  }
  process.exit(failed ? 1 : 0);
}

if (process.argv[1] && process.argv[1].endsWith('runner.mjs')) main();
