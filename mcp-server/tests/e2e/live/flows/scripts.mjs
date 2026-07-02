// Script CRUD + recompile flow: create_script / read_script / update_script / delete_script / refresh_assets /
// get_compilation_state. A refresh that SUCCESSFULLY recompiles crosses a DOMAIN RELOAD (the reload-recovery fix must
// let the server reconnect); a compile ERROR does NOT reload (no new assembly), so only create/restore cross a reload.
// Verified by OUTCOME through mixed channels: the filesystem (.cs on disk), bridge read-back (read_script content,
// get_compilation_state.isCompiling), and the version-robust `error CS` grammar in the editor log. The LOG is the
// authoritative error/clean signal: get_compilation_state.errorCount is populated from monitoring (a static field that
// resets on every domain reload) + a default-Editor.log parse (the wrong file when the editor runs with -logFile), so
// it reads 0 here regardless. parseCompile on the -logFile persists across reloads and reflects the latest episode.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileExists, parseCompile } from '../verify.mjs';
import { resultJson } from '../mcpDriver.mjs';
import { retry } from '../retry.mjs';

const ASSET = 'Assets/Scripts/E2EScratch.cs';
const VALID = 'using UnityEngine;\npublic class E2EScratch : MonoBehaviour { void Awake() { } }';
const BROKEN = 'public class E2EScratch { this is not valid C# @@@ }';

const compileLogErrors = (logPath) => {
  try { return parseCompile(readFileSync(logPath, 'utf8')).errors; } catch { return []; }
};
const compState = async (driver) => resultJson(await driver.call('get_compilation_state'));
const waitUntil = (label, fn, timeoutMs) =>
  retry(async () => { if (!(await fn())) throw new Error(`timed out waiting: ${label}`); return true; },
    { timeoutMs: timeoutMs ?? Number(process.env.E2E_TIMEOUT ?? 120000), intervalMs: 750 });

// refresh + wait until the editor reports settled with zero errors (crosses a reload on a successful compile).
async function recompileClean(driver, logPath, label) {
  await driver.call('refresh_assets');
  await waitUntil(`${label} (isCompiling:false AND no error CS in the log's latest episode)`,
    async () => (await compState(driver)).isCompiling === false && compileLogErrors(logPath).length === 0);
}

export async function run(ctx) {
  const { driver, hostPath, logPath } = ctx;
  const diskPath = join(hostPath, 'Assets', 'Scripts', 'E2EScratch.cs');
  try {
    // CREATE a valid MonoBehaviour (template) — filesystem + read-back channels.
    await driver.call('create_script', { scriptName: 'E2EScratch', scriptType: 'MonoBehaviour' });
    assert.ok(fileExists(diskPath), 'create_script wrote the .cs to disk');
    const read = resultJson(await driver.call('read_script', { scriptPath: ASSET }));
    assert.match(read.scriptContent, /class E2EScratch/, 'read_script returns the new class');

    // RECOMPILE — a successful compile crosses a domain reload.
    await recompileClean(driver, logPath, 'compiled clean after create');

    // INTRODUCE a compile error (update_script raw content; H3 confirm). A failed compile does NOT reload; verify the
    // error via BOTH channels: the editor's live errorCount AND the version-robust `error CS` grammar in the log.
    await driver.call('update_script', { scriptPath: ASSET, scriptContent: BROKEN, confirm: true });
    await driver.call('refresh_assets');
    await waitUntil('compile error surfaced (error CS in the log; a failed compile does not reload)',
      async () => { const s = await compState(driver); return s.isCompiling === false && compileLogErrors(logPath).length > 0; });

    // RESTORE valid content — clean again (via read-back; the log still holds the prior error). Crosses a reload.
    await driver.call('update_script', { scriptPath: ASSET, scriptContent: VALID, confirm: true });
    await recompileClean(driver, logPath, 'compiled clean after restore');

    // DELETE — filesystem gone.
    await driver.call('delete_script', { scriptPath: ASSET, confirm: true });
    assert.ok(!fileExists(diskPath), 'delete_script removed the .cs from disk');
  } finally {
    // Best-effort cleanup: remove the scratch script + settle to a clean compile for the next flow / next run.
    try { await driver.call('delete_script', { scriptPath: ASSET, confirm: true }); } catch { /* already gone */ }
    try { await recompileClean(driver, logPath, 'cleanup recompile'); } catch { /* best effort */ }
  }
}
run.flowName = 'scripts';
