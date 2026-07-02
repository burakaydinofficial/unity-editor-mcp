// TASK 5 SMOKE STUB — replaced by the real play-mode flow in Task 6. Just proves the launch->connect->call->quit
// wiring works end-to-end.
export async function run(ctx) {
  const r = await ctx.driver.call('get_editor_state');
  if (r.isError) throw new Error('get_editor_state returned an error');
}
run.flowName = 'playmode';
