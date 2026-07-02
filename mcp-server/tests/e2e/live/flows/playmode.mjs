// Play-mode flow: play_game / pause_game / stop_game / get_editor_state. Transition tools cross a domain reload, so
// each effect is verified by OUTCOME (never the transition's own response) through BOTH channels — read-back
// (isPlaying) AND the independent probe (EnteredPlayMode/ExitingPlayMode/pause) — a mismatch is the false-success.
import assert from 'node:assert/strict';
import { readProbeEvents } from '../verify.mjs';
import { resultJson } from '../mcpDriver.mjs';
import { retry } from '../retry.mjs';

// get_editor_state returns { state: { isPlaying, isPaused, isCompiling, ... } } — unwrap the nested `state`.
const stateOf = async (driver) => {
  const r = resultJson(await driver.call('get_editor_state'));
  return r.state ?? r;
};
const probeHas = (probeFile, event) => readProbeEvents(probeFile).some(e => e.event === event);
const probePaused = (probeFile) => readProbeEvents(probeFile).some(e => e.event === 'pause' && e.paused === true);

// Poll a condition until it holds (rides out the async transition + the reload-reconnect window). Throws a labelled
// timeout so a genuine failure is fast + legible, not a hang.
const waitUntil = (label, fn, timeoutMs) =>
  retry(async () => { if (!(await fn())) throw new Error(`timed out waiting: ${label}`); return true; },
    { timeoutMs: timeoutMs ?? Number(process.env.E2E_TIMEOUT ?? 120000), intervalMs: 500 });

export async function run(ctx) {
  const { driver, probeFile } = ctx;
  try {
    assert.equal((await stateOf(driver)).isPlaying, false, 'precondition: editor not playing');

    // ENTER play mode — crosses a domain reload.
    await driver.call('play_game');
    await waitUntil('entered play mode (read-back isPlaying:true AND probe EnteredPlayMode)',
      async () => (await stateOf(driver)).isPlaying === true && probeHas(probeFile, 'EnteredPlayMode'));

    // PAUSE — pause_game is a TOGGLE, so use the no-retry path (a retried lost response would un-pause). No reload
    // here. Both channels: read-back isPaused AND the independent probe pause event.
    await driver.call('pause_game', {}, { once: true });
    await waitUntil('paused (read-back isPaused:true AND probe pause:true)',
      async () => (await stateOf(driver)).isPaused === true && probePaused(probeFile), 30000);

    // EXIT play mode — crosses a domain reload.
    await driver.call('stop_game');
    await waitUntil('exited play mode (read-back isPlaying:false AND probe ExitingPlayMode)',
      async () => (await stateOf(driver)).isPlaying === false && probeHas(probeFile, 'ExitingPlayMode'));
  } finally {
    // Leave the shared editor in edit mode for the next flow, regardless of outcome.
    try { if ((await stateOf(driver)).isPlaying) await driver.call('stop_game'); } catch { /* best effort */ }
  }
}
run.flowName = 'playmode';
