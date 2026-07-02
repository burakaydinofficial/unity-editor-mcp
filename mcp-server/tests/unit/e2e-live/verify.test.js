import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileExists, readProbeEvents, parseCompile } from '../../e2e/live/verify.mjs';

const dir = mkdtempSync(join(tmpdir(), 'e2e-verify-'));
after(() => rmSync(dir, { recursive: true, force: true }));

test('fileExists true/false', () => {
  const p = join(dir, 'a.txt'); writeFileSync(p, 'x');
  assert.equal(fileExists(p), true);
  assert.equal(fileExists(join(dir, 'nope.txt')), false);
});

test('readProbeEvents keeps good JSONL lines, drops junk, handles CRLF + missing file', () => {
  const p = join(dir, 'probe.jsonl');
  // CRLF (Unity on Windows), a malformed line mixed with good ones, and blank lines.
  writeFileSync(p, '{"event":"a"}\r\nGARBAGE NOT JSON\r\n{"event":"b"}\r\n\r\n');
  assert.deepEqual(readProbeEvents(p).map(e => e.event), ['a', 'b']);
  assert.deepEqual(readProbeEvents(join(dir, 'missing.jsonl')), []); // missing file -> []
});

test('parseCompile: a clean log yields no errors', () => {
  assert.deepEqual(parseCompile('Reloading assemblies\nAll good\n').errors, []);
});

test('parseCompile: finds error CS, de-dupes Unity double-logging, keeps distinct, no-boundary fallback', () => {
  const one = parseCompile('Reloading assemblies\nAssets/X.cs(3,5): error CS0103: broken\n');
  assert.equal(one.errors.length, 1);
  assert.equal(one.errors[0].code, 'CS0103');
  // Unity logs each error twice (message + "(Filename:)" form); summarize de-dupes on file:line:code.
  const dup = parseCompile('Reloading assemblies\n' +
    'Assets/X.cs(3,5): error CS0103: broken\nAssets/X.cs(3,5): error CS0103: broken\n');
  assert.equal(dup.errors.length, 1);
  // Two distinct errors are both kept.
  const two = parseCompile('Reloading assemblies\n' +
    'Assets/X.cs(3,5): error CS0103: a\nAssets/Y.cs(1,2): error CS1002: b\n');
  assert.equal(two.errors.length, 2);
  // An error with no boundary marker is still found (last-lines window fallback).
  assert.equal(parseCompile('Assets/Z.cs(9,9): error CS0000: x\n').errors.length, 1);
});
