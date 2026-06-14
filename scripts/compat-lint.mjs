// Floor-divergent Unity API lint.
//
// Flags a curated set of Unity APIs that do NOT exist (or differ) on the
// supported floor (Unity 2020.3) when they are used OUTSIDE an `#if` preprocessor
// guard. This is the cheap, pure-Node PR gate that catches the failure class that
// broke the floor before (an unguarded PrefabStageUtility namespace), without
// compiling Unity. It is a heuristic, not a compiler: keep RULES in sync with
// COMPATIBILITY.md. Dependency-free; exits 1 on any violation.

import { readFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join, relative } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..');
const SCAN_DIR = resolve(ROOT, 'unity-editor-mcp');

// Each rule: a regex matching the risky token + why it is floor-divergent.
const RULES = [
  { re: /UnityEditor\.(Experimental\.)?SceneManagement\.PrefabStageUtility/, api: 'PrefabStageUtility (qualified)', reason: 'namespace moved in 2021.2 — guard / alias it (UNITY_2021_2_OR_NEWER)' },
  { re: /\bFindObjectsByType\b/, api: 'FindObjectsByType', reason: 'added 2021.3.18 / 2022.2 — use FindObjectsOfType on the floor' },
  { re: /\bFindFirstObjectByType\b/, api: 'FindFirstObjectByType', reason: 'added 2021.3.18 / 2022.2' },
  { re: /\bFindAnyObjectByType\b/, api: 'FindAnyObjectByType', reason: 'added 2021.3.18 / 2022.2' },
  { re: /\.linearDamping\b/, api: 'Rigidbody.linearDamping', reason: 'renamed from .drag in 6000 — guard with UNITY_6000_0_OR_NEWER' },
  { re: /\.angularDamping\b/, api: 'Rigidbody.angularDamping', reason: 'renamed from .angularDrag in 6000' },
  { re: /\bLightType\.Rectangle\b/, api: 'LightType.Rectangle', reason: 'differs from legacy LightType.Area' },
  { re: /\.Contains\([^)]*,[^)]*StringComparison/, api: 'string.Contains(value, StringComparison)', reason: 'the 2-arg Contains overload is netstandard2.1+ — use IndexOf(value, StringComparison) >= 0 on the floor' },
];

// Known-safe exceptions: { file: '<repo-relative path>', line: <number> }.
// Now that scan() lints the FLOOR branch of a version guard, the one rule that matches
// BOTH namespace forms (PrefabStageUtility) flags the correct old-namespace alias in the
// #else floor branch. That line is intentional and floor-correct — allowlist it. (If the
// file shifts, the lint fails loudly and this line number must be updated — not a silent miss.)
const ALLOWLIST = [
  { file: 'unity-editor-mcp/Editor/Handlers/AssetManagementHandler.cs', line: 16, api: 'PrefabStageUtility (qualified)' },
];

async function* walkCs(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const p = join(dir, entry.name);
    if (entry.isDirectory()) yield* walkCs(p);
    else if (entry.isFile() && p.endsWith('.cs')) yield p;
  }
}

/**
 * Returns risky-token hits that sit in FLOOR-compiled code — either unguarded, or in
 * the floor branch of a version guard. A line is exempt (skipped) only when it is in a
 * branch the floor (old Unity) does NOT compile: the #if of a positive
 * `UNITY_x_OR_NEWER` guard, or the #else of a negated one. The floor branches — the
 * #else of a positive guard and the #if of a negated one — ARE scanned, because a new
 * API misplaced there silently breaks the floor (audit finding: the old depth-only
 * scan skipped every #else). Non-version guards keep the historical "skip everything
 * guarded" behaviour, so this never adds a false positive on correct code (floor
 * branches use the OLD APIs, which are not in RULES).
 */
function scan(source) {
  const lines = source.split(/\r\n|\r|\n/);
  const hits = [];
  const stack = []; // { exempt: bool, verGuard: bool }
  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trimStart();
    const mIf = trimmed.match(/^#\s*if\b(.*)$/);
    if (mIf) {
      const isVer = /UNITY_\d[\w]*_OR_NEWER/.test(mIf[1]);
      const negated = /!\s*UNITY_\d[\w]*_OR_NEWER/.test(mIf[1]);
      // positive version guard => #if branch is the NEW (exempt) path; negated => #if
      // branch is the FLOOR (scanned) path; non-version => exempt (conservative).
      stack.push({ exempt: isVer ? !negated : true, verGuard: isVer });
      continue;
    }
    if (/^#\s*endif\b/.test(trimmed)) { stack.pop(); continue; }
    if (/^#\s*else\b/.test(trimmed)) {
      const top = stack[stack.length - 1];
      if (top && top.verGuard) top.exempt = !top.exempt; // the opposite branch
      continue;
    }
    if (/^#\s*elif\b/.test(trimmed)) {
      const top = stack[stack.length - 1];
      if (top) top.exempt = true; // precise #elif floor-detection out of scope — stay safe
      continue;
    }
    if (stack.some((f) => f.exempt)) continue; // inside a non-floor branch
    const code = lines[i].replace(/\/\/.*$/, ''); // ignore line comments
    for (const rule of RULES) {
      if (rule.re.test(code)) hits.push({ line: i + 1, api: rule.api, reason: rule.reason });
    }
  }
  return hits;
}

const violations = [];
for await (const file of walkCs(SCAN_DIR)) {
  const rel = relative(ROOT, file).replace(/\\/g, '/');
  for (const h of scan(await readFile(file, 'utf8'))) {
    if (ALLOWLIST.some((a) => a.file === rel && a.line === h.line && (a.api === undefined || a.api === h.api))) continue;
    violations.push({ file: rel, ...h });
  }
}

if (violations.length === 0) {
  console.log('compat-lint: OK — no unguarded floor-divergent Unity APIs in unity-editor-mcp/.');
  process.exit(0);
}

console.error(`compat-lint: ${violations.length} unguarded floor-divergent API use(s):`);
for (const v of violations) console.error(`  - ${v.file}:${v.line}  ${v.api} — ${v.reason}`);
console.error('Wrap each in an #if UNITY_x_y_OR_NEWER guard (both branches) and record it in COMPATIBILITY.md.');
process.exit(1);
