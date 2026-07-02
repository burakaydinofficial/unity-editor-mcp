import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdtempSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileExists, readProbeEvents, parseCompile } from '../../e2e/live/verify.mjs';

const dir = mkdtempSync(join(tmpdir(), 'e2e-verify-'));

test('fileExists true/false', () => {
  const p = join(dir, 'a.txt'); writeFileSync(p, 'x');
  assert.equal(fileExists(p), true);
  assert.equal(fileExists(join(dir, 'nope.txt')), false);
});

test('readProbeEvents parses jsonl, tolerates blank/partial lines', () => {
  const p = join(dir, 'probe.jsonl');
  writeFileSync(p, '{"event":"probeLoaded"}\n{"event":"EnteredPlayMode"}\n\n');
  const ev = readProbeEvents(p);
  assert.equal(ev.length, 2);
  assert.equal(ev[1].event, 'EnteredPlayMode');
  assert.deepEqual(readProbeEvents(join(dir, 'missing.jsonl')), []); // missing file -> []
});

test('parseCompile detects a clean episode vs an error CS', () => {
  assert.equal(parseCompile('Reloading assemblies\nAll good\n').compiled, true);
  const bad = parseCompile('Reloading assemblies\nAssets/X.cs(3,5): error CS0103: broken\n');
  assert.equal(bad.errors.length, 1);
  assert.equal(bad.errors[0].code, 'CS0103');
});
