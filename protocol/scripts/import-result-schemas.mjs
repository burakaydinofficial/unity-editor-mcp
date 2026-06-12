// Merge a bundle of derived result schemas into the catalog's `result` fields.
//
// Usage: node scripts/import-result-schemas.mjs <bundle.json>
//
// The bundle may be either { resultSchemas: [...] } or a workflow output file
// wrapping it under `.result`. Commands listed in catalog.knownGaps (no real
// handler) and empty schemas are skipped so we never enshrine a fabricated shape.

import { readFile, writeFile } from 'node:fs/promises';
import { loadCatalog, PATHS } from './lib/sources.mjs';

const bundlePath = process.argv[2];
if (!bundlePath) {
  console.error('usage: node scripts/import-result-schemas.mjs <bundle.json>');
  process.exit(1);
}

let bundle = JSON.parse(await readFile(bundlePath, 'utf8'));
if (bundle.result !== undefined) bundle = typeof bundle.result === 'string' ? JSON.parse(bundle.result) : bundle.result;
const resultSchemas = bundle.resultSchemas ?? [];

const catalog = await loadCatalog();
const byName = new Map(catalog.commands.map((c) => [c.name, c]));
const gapNames = new Set((catalog.knownGaps ?? []).map((g) => g.command));

let merged = 0;
const skipped = [];
const mismatches = [];
for (const cat of resultSchemas) {
  for (const cmd of cat.commands ?? []) {
    if (cmd.paramMismatch && cmd.paramMismatch.trim() && cmd.paramMismatch.trim().toLowerCase() !== 'none') {
      mismatches.push(`${cmd.name}: ${cmd.paramMismatch.trim()}`);
    }
    const entry = byName.get(cmd.name);
    if (!entry) { skipped.push(`${cmd.name} (not in catalog)`); continue; }
    if (gapNames.has(cmd.name)) { skipped.push(`${cmd.name} (known gap)`); continue; }
    if (!cmd.resultSchema || Object.keys(cmd.resultSchema).length === 0) { skipped.push(`${cmd.name} (empty schema)`); continue; }
    entry.result = cmd.resultSchema;
    merged++;
  }
}

// Rebuild with a clean, stable key order.
const out = {
  $schema: catalog.$schema,
  protocol: catalog.protocol,
  resultSchemaSource: 'derived-from-handlers-v1',
  description: catalog.description,
  knownGaps: catalog.knownGaps,
  commands: catalog.commands,
};
await writeFile(PATHS.catalog, JSON.stringify(out, null, 2) + '\n', 'utf8');

console.log(`Merged ${merged} result schemas. Skipped ${skipped.length}: ${skipped.join(', ') || '(none)'}`);
if (mismatches.length) {
  console.log(`\nParameter-contract notes (${mismatches.length}):`);
  for (const m of mismatches) console.log(`  - ${m}`);
}
