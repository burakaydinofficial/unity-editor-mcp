// Summarize the LAST compilation episode from Unity's Editor.log — a dev aid for
// observing editor-side compiles WITHOUT the bridge (it reads native `error CS`
// lines, so it works even when a broken package has killed the in-editor bridge).
// Pure Node, cross-platform, read-only. Node's fs opens the file shared, so it
// reads the log even while Unity holds it open for writing.
//
//   node scripts/read-editor-log.mjs            # default Editor.log for this OS
//   UNITY_EDITOR_LOG=/path/to/Editor.log node scripts/read-editor-log.mjs
//
// Exit code: 0 = last compile clean (or unknown), 1 = compile errors found.

import { readFileSync, existsSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

function editorLogPath(platform = process.platform, env = process.env) {
  if (env.UNITY_EDITOR_LOG) return env.UNITY_EDITOR_LOG;
  if (platform === 'win32') {
    return join(env.LOCALAPPDATA || join(homedir(), 'AppData', 'Local'), 'Unity', 'Editor', 'Editor.log');
  }
  if (platform === 'darwin') return join(homedir(), 'Library', 'Logs', 'Unity', 'Editor.log');
  return join(homedir(), '.config', 'unity3d', 'Editor.log');
}

// START-of-episode markers only (NOT "Compilation finished", which is logged
// AFTER the error lines). On a FAILED compile Unity does not reload, so the last
// start marker is the previous good compile and the new `error CS` lines fall
// after it — exactly the window we scan.
const BOUNDARY = /Reloading assemblies|Begin MonoManager ReloadAssembly|\[CompilationHandler\] Compilation started/;
const ERROR_RE = /(.+\.cs)\((\d+),(\d+)\): error (CS\d+): (.+)/;
const ASM_RE = /Assembly compilation finished: (\S+) \((\d+) messages\)/;

export function summarize(text) {
  const lines = text.split(/\r?\n/);

  // Compile errors: only within the latest compile episode (after the last start).
  let boundary = -1;
  for (let i = lines.length - 1; i >= 0 && i >= lines.length - 4000; i--) {
    if (BOUNDARY.test(lines[i])) { boundary = i; break; }
  }
  const window = lines.slice(boundary >= 0 ? boundary : Math.max(0, lines.length - 800));
  const errors = [];
  for (const line of window) {
    const e = line.match(ERROR_RE);
    if (e) errors.push({ file: e[1], line: e[2], col: e[3], code: e[4], msg: e[5] });
  }
  // De-dupe (Unity logs each error twice: message + "(Filename:)" form).
  const seen = new Set();
  const uniqueErrors = errors.filter((e) => {
    const k = `${e.file}:${e.line}:${e.code}`;
    return seen.has(k) ? false : (seen.add(k), true);
  });

  // Per-assembly message counts (bridge-only marker): last value seen per assembly
  // across the recent tail, independent of the error window.
  const asmMap = new Map();
  for (const line of lines.slice(Math.max(0, lines.length - 2000))) {
    const a = line.match(ASM_RE);
    if (a) asmMap.set(a[1], Number(a[2]));
  }
  const assemblies = [...asmMap].map(([assembly, messages]) => ({ assembly, messages }));
  return { errors: uniqueErrors, assemblies };
}

// Run the CLI only when invoked directly, not when imported (e.g. by the E2E harness verify helpers).
if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  const path = editorLogPath();
  if (!existsSync(path)) {
    console.error(`Editor.log not found at: ${path}\nSet UNITY_EDITOR_LOG to override.`);
    process.exit(0);
  }

  const { mtime } = statSync(path);
  const { errors, assemblies } = summarize(readFileSync(path, 'utf8'));

  console.log(`Editor.log: ${path}`);
  console.log(`last written: ${mtime.toISOString()}`);
  if (assemblies.length) {
    console.log('assemblies (last episode):');
    for (const a of assemblies) console.log(`  ${a.messages === 0 ? 'OK ' : '!! '}${a.assembly} (${a.messages} messages)`);
  }
  if (errors.length) {
    console.log(`\nFAIL — ${errors.length} compile error(s):`);
    for (const e of errors) console.log(`  ${e.file}(${e.line},${e.col}): ${e.code}: ${e.msg}`);
    process.exit(1);
  }
  console.log('\nPASS — no compile errors in the last episode.');
  process.exit(0);
}
