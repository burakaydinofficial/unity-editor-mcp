import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { RoslynSidecarClient, makeRoslynClientFactory, platformRid } from '../../../src/core/roslynSidecar.js';

function fakeChild() {
  const child = new EventEmitter();
  child.stdin = { written: [], write(s) { this.written.push(s); return true; } };
  child.stdout = new EventEmitter();
  child.stdout.setEncoding = () => {};
  child.killed = false;
  child.kill = () => { child.killed = true; };
  return child;
}

describe('RoslynSidecarClient', () => {
  it('round-trips a call by id', async () => {
    const child = fakeChild();
    const client = new RoslynSidecarClient(child);
    const p = client.call('ping', {});
    const req = JSON.parse(child.stdin.written[0]);
    child.stdout.emit('data', JSON.stringify({ id: req.id, result: { pong: true } }) + '\n');
    assert.deepEqual(await p, { pong: true });
  });

  it('correlates two concurrent calls by id (out-of-order replies)', async () => {
    const child = fakeChild();
    const client = new RoslynSidecarClient(child);
    const p1 = client.call('a', {});
    const p2 = client.call('b', {});
    const id1 = JSON.parse(child.stdin.written[0]).id;
    const id2 = JSON.parse(child.stdin.written[1]).id;
    child.stdout.emit('data', JSON.stringify({ id: id2, result: 'B' }) + '\n');
    child.stdout.emit('data', JSON.stringify({ id: id1, result: 'A' }) + '\n');
    assert.equal(await p1, 'A');
    assert.equal(await p2, 'B');
  });

  it('handles a reply split across chunks', async () => {
    const child = fakeChild();
    const client = new RoslynSidecarClient(child);
    const p = client.call('x', {});
    const id = JSON.parse(child.stdin.written[0]).id;
    const line = JSON.stringify({ id, result: 42 }) + '\n';
    child.stdout.emit('data', line.slice(0, 5));
    child.stdout.emit('data', line.slice(5));
    assert.equal(await p, 42);
  });

  it('rejects on an error envelope', async () => {
    const child = fakeChild();
    const client = new RoslynSidecarClient(child);
    const p = client.call('x', {});
    const id = JSON.parse(child.stdin.written[0]).id;
    child.stdout.emit('data', JSON.stringify({ id, error: { code: 'NO_MODEL', message: 'nope' } }) + '\n');
    await assert.rejects(() => p, /nope/);
  });

  it('dispose() kills the child and fails pending calls', async () => {
    const child = fakeChild();
    const client = new RoslynSidecarClient(child);
    const p = client.call('x', {});
    await client.dispose();
    assert.equal(child.killed, true);
    await assert.rejects(() => p, /disposed/);
  });
});

describe('makeRoslynClientFactory', () => {
  it('returns null (unavailable) when the binary is absent', async () => {
    const factory = makeRoslynClientFactory({ ensureBinary: async () => null, readModel: async () => '{}', spawn: () => { throw new Error('should not spawn'); } });
    const conn = { sendCommand: async () => ({ modelPath: '/x/model.json' }) };
    assert.equal(await factory(conn), null);
  });

  it('returns null when the editor export yields no modelPath', async () => {
    const factory = makeRoslynClientFactory({ ensureBinary: async () => '/fake/bin', readModel: async () => '{}', spawn: () => { throw new Error('should not spawn'); } });
    const conn = { sendCommand: async () => ({}) };
    assert.equal(await factory(conn), null);
  });

  it('exports the model, spawns, loads, and returns the client when the binary is present', async () => {
    const child = fakeChild();
    let loaded = null;
    const origWrite = child.stdin.write.bind(child.stdin);
    child.stdin.write = (s) => {
      origWrite(s);
      const req = JSON.parse(s);
      if (req.method === 'load_model') { loaded = req.params.modelJson; queueMicrotask(() => child.stdout.emit('data', JSON.stringify({ id: req.id, result: { loaded: true } }) + '\n')); }
      return true;
    };
    let exported = false;
    const conn = { sendCommand: async (t) => { if (t === 'export_roslyn_model') { exported = true; return { modelPath: '/x/model.json' }; } } };
    const factory = makeRoslynClientFactory({ ensureBinary: async () => '/fake/bin', readModel: async () => '{"generation":1,"assemblies":[]}', spawn: () => child });
    const client = await factory(conn);
    assert.ok(client, 'returns a client');
    assert.equal(exported, true);
    assert.equal(loaded, '{"generation":1,"assemblies":[]}');
  });
});

describe('platformRid', () => {
  it('returns a known rid or null', () => {
    const rid = platformRid();
    assert.ok(rid === null || ['win-x64', 'osx-x64', 'osx-arm64', 'linux-x64'].includes(rid));
  });
});
