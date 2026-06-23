#!/usr/bin/env node
// Generates docs/tools-reference.md from protocol/catalog/commands.json (the contract source of truth) —
// a full per-tool reference (params + result per command) with no hand-maintained drift (roadmap J1).
// Run: node protocol/scripts/generate-tools-reference.mjs
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const catalog = JSON.parse(readFileSync(join(here, '..', 'catalog', 'commands.json'), 'utf8'));
const outPath = join(here, '..', '..', 'docs', 'tools-reference.md');

const commands = catalog.commands.filter((c) => !c.internal);
const categories = [...new Set(commands.map((c) => c.category))].sort();

const oneLine = (s) => (s || '').replace(/\s+/g, ' ').trim();

function typeOf(schema) {
  if (!schema) return 'any';
  if (schema.type) return Array.isArray(schema.type) ? schema.type.join('|') : schema.type;
  if (schema.enum) return 'enum';
  if (schema.oneOf || schema.anyOf) return 'oneOf';
  return 'object';
}

function paramLines(cmd) {
  const p = cmd.params;
  if (!p || !p.properties || Object.keys(p.properties).length === 0) return ['  - _none_'];
  const required = new Set(p.required || []);
  return Object.entries(p.properties).map(([name, s]) => {
    const req = required.has(name) ? ', required' : '';
    const en = s.enum ? ` — one of: ${s.enum.join(', ')}` : '';
    const desc = oneLine(s.description);
    return `  - \`${name}\` (${typeOf(s)}${req})${desc ? ` — ${desc}` : ''}${en}`;
  });
}

function resultSummary(cmd) {
  const r = cmd.result;
  if (!r) return '—';
  if (r.oneOf || r.anyOf) return 'varies (one of several shapes)';
  if (r.properties) return Object.keys(r.properties).map((k) => `\`${k}\``).join(', ');
  if (r.type) return r.type;
  return '—';
}

const out = [];
out.push('# Unity Editor MCP — Tool Reference');
out.push('');
out.push(`> **Generated** from \`protocol/catalog/commands.json\` (protocol \`${catalog.protocol}\`) — do not edit by hand.`);
out.push('> Regenerate with `node protocol/scripts/generate-tools-reference.mjs`.');
out.push('');
out.push(`**${commands.length} commands across ${categories.length} categories.** Each is reached via the generic`);
out.push('`call_unity_tool(instance, name, params)` meta-tool after on-demand discovery with `list_unity_tools` —');
out.push('the connected editor advertises these, not the MCP server (ADR 0006). Two internal commands are omitted.');
out.push('');

for (const cat of categories) {
  const inCat = commands.filter((c) => c.category === cat).sort((a, b) => a.name.localeCompare(b.name));
  out.push(`## ${cat.charAt(0).toUpperCase() + cat.slice(1)} (${inCat.length})`);
  out.push('');
  for (const cmd of inCat) {
    const flag = cmd.destructive ? ' — _⚠️ destructive (confirm-gated)_' : '';
    out.push(`### \`${cmd.name}\`${flag}`);
    const desc = oneLine(cmd.description);
    if (desc) {
      out.push(desc);
      out.push('');
    }
    out.push('- **Params:**');
    for (const l of paramLines(cmd)) out.push(l);
    out.push(`- **Result:** ${resultSummary(cmd)}`);
    out.push('');
  }
}

writeFileSync(outPath, out.join('\n') + '\n');
console.log(`Wrote ${outPath} — ${commands.length} commands, ${categories.length} categories.`);
