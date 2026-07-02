// Self-check flow: NEGATIVE CONTROLS — prove the harness CAN fail (design §7: "a harness that always passes is
// worthless"). Each control asserts that a call which MUST fail actually surfaces as an error through the real chain.
import assert from 'node:assert/strict';

export async function run(ctx) {
  const { driver } = ctx;

  // 1. A nonexistent tool must be rejected (the Node gate: "not available on this instance").
  await assert.rejects(
    driver.call('e2e_no_such_tool', {}, { once: true }),
    /error/i,
    'negative control: a bogus tool name must fail');

  // 2. Schema-invalid params must be rejected by the Node validation gate (INVALID_PARAMS), never forwarded. Use a
  //    READ-ONLY tool with a type violation (`scriptPath` must be a string) — a mutating vehicle could change editor
  //    state if the rejection ever regressed (this control originally used play_game and did exactly that), and the
  //    reserved `fields` meta-param is deliberately EXCLUDED from the gate, so it is not a valid control.
  await assert.rejects(
    driver.call('read_script', { scriptPath: 12345 }, { once: true }),
    /error/i,
    'negative control: schema-invalid params must fail validation');

  // 3. An invalid state transition must fail honestly: pause_game outside play mode is INVALID_STATE.
  await assert.rejects(
    driver.call('pause_game', {}, { once: true }),
    /error/i,
    'negative control: pause outside play mode must fail (INVALID_STATE)');
}
run.flowName = 'selfcheck';
