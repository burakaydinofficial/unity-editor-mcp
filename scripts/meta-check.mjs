#!/usr/bin/env node
// Fails if any .cs or .asmdef in the UPM package lacks a sibling .meta. A missing .meta ships to consumers through
// the git-URL install and makes Unity warn on every import (production feedback #5 — ManagePackagesTests.cs shipped a
// window without its .meta). Pure Node, no deps — same footing as scripts/compat-lint.mjs. Run: node scripts/meta-check.mjs
import { readdirSync, existsSync, statSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const pkgRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'unity-editor-mcp');
const CHECK = ['.cs', '.asmdef'];
const missing = [];

function walk(dir) {
  for (const name of readdirSync(dir)) {
    // Unity ignores folders ending in '~' and hidden dot-folders — they need no .meta.
    if (name.endsWith('~') || name.startsWith('.')) continue;
    const p = join(dir, name);
    const st = statSync(p);
    if (st.isDirectory()) { walk(p); continue; }
    if (CHECK.some((ext) => name.endsWith(ext)) && !existsSync(p + '.meta')) missing.push(p);
  }
}

walk(pkgRoot);

if (missing.length > 0) {
  console.error(`meta-check: ${missing.length} file(s) missing a .meta (would warn in every consumer's editor on import):`);
  for (const m of missing) console.error('  ' + m.replace(/\\/g, '/'));
  process.exit(1);
}
console.log(`meta-check: OK — every ${CHECK.join('/')} in unity-editor-mcp/ has a .meta.`);
