import { describe, it } from 'node:test';
import assert from 'node:assert';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  fnv1a,
  derivePort,
  normalizeProjectPath,
  instanceFileName,
  defaultRegistryDirectory,
  readInstances,
  findInstanceByProjectPath,
  isFresh,
  isProcessAlive,
  isLive,
  reapStale,
  resolveUnityPort,
  DEFAULT_BASE_PORT,
  DEFAULT_PORT_RANGE,
} from '../../../src/core/discovery.js';
import { hostname } from 'node:os';

const slashes = (p) => p.replace(/\\/g, '/');

describe('discovery', () => {
  describe('fnv1a', () => {
    it('matches the canonical vectors locked by the C# Core tests', () => {
      // Must stay byte-identical with EndpointAddressingTests.cs.
      assert.strictEqual(fnv1a(''), 2166136261);
      assert.strictEqual(fnv1a('a'), 0xe40c292c);
      assert.strictEqual(fnv1a('foobar'), 0xbf9cf968);
    });
  });

  describe('normalizeProjectPath', () => {
    it('lowercases, forward-slashes, and trims trailing slashes', () => {
      assert.strictEqual(normalizeProjectPath('C:\\Projects\\Game\\'), 'c:/projects/game');
      assert.strictEqual(normalizeProjectPath(''), '');
      assert.strictEqual(normalizeProjectPath(null), '');
    });

    it('folds only ASCII A-Z, leaving Unicode (U+0130) intact for C# parity', () => {
      // Must match EndpointAddressing.Normalize exactly; toLowerCase() would split
      // U+0130 into 'i' + combining dot and break the hash/filename across halves.
      assert.strictEqual(normalizeProjectPath('C:/PROİJECT'), 'c:/proİject');
      assert.strictEqual(normalizeProjectPath('C:\\PROİJECT\\'), 'c:/proİject');
    });
  });

  describe('derivePort', () => {
    it('is deterministic and normalization-insensitive', () => {
      assert.strictEqual(derivePort('C:/projects/game'), derivePort('C:\\Projects\\Game\\'));
    });

    it('stays within [base, base+range)', () => {
      for (let i = 0; i < 100; i++) {
        const port = derivePort(`C:/projects/p${i}`);
        assert.ok(port >= DEFAULT_BASE_PORT && port < DEFAULT_BASE_PORT + DEFAULT_PORT_RANGE);
      }
    });

    it('differs between projects', () => {
      assert.notStrictEqual(derivePort('C:/projects/2020'), derivePort('C:/projects/2021'));
    });
  });

  describe('instanceFileName', () => {
    it('is 8 lowercase hex digits + .json, stable across path spellings', () => {
      const name = instanceFileName('C:/projects/game');
      assert.match(name, /^[0-9a-f]{8}\.json$/);
      assert.strictEqual(name, instanceFileName('C:\\Projects\\Game\\'));
    });
  });

  describe('defaultRegistryDirectory', () => {
    it('honors the env override', () => {
      assert.strictEqual(defaultRegistryDirectory({ UNITY_MCP_REGISTRY_DIR: 'X:/o' }), 'X:/o');
    });

    it('uses platform-conventional base dirs ending in unity-editor-mcp/instances', () => {
      assert.ok(slashes(defaultRegistryDirectory({ LOCALAPPDATA: 'C:\\U\\AppData\\Local' }, 'win32')).endsWith('unity-editor-mcp/instances'));
      assert.ok(slashes(defaultRegistryDirectory({ HOME: '/Users/x' }, 'darwin')).includes('Library/Application Support'));
      assert.ok(slashes(defaultRegistryDirectory({ HOME: '/home/x' }, 'linux')).includes('.local/share'));
      assert.ok(slashes(defaultRegistryDirectory({ XDG_DATA_HOME: '/xdg/data', HOME: '/home/x' }, 'linux')).startsWith('/xdg/data'));
    });
  });

  describe('registry reading', () => {
    const withRegistry = (fn) => {
      const dir = mkdtempSync(join(tmpdir(), 'mcp-reg-test-'));
      try {
        return fn(dir);
      } finally {
        rmSync(dir, { recursive: true, force: true });
      }
    };

    const descriptor = (overrides = {}) => ({
      schemaVersion: 1,
      projectPath: 'C:/projects/game',
      port: 6512,
      pid: 4242,
      unityVersion: '2020.3.49f1',
      protocolVersion: '1.0.0',
      // .NET 'o' round-trip format — what the C# side actually writes.
      startedAt: '2026-06-13T10:00:00.0000000Z',
      lastHeartbeat: new Date().toISOString(),
      ...overrides,
    });

    it('reads descriptors and skips corrupt files', () => {
      withRegistry((dir) => {
        const desc = descriptor();
        writeFileSync(join(dir, instanceFileName(desc.projectPath)), JSON.stringify(desc));
        writeFileSync(join(dir, 'corrupt.json'), '{ nope');
        const all = readInstances(dir);
        assert.strictEqual(all.length, 1);
        assert.strictEqual(all[0].port, 6512);
      });
    });

    it('returns [] for a missing directory', () => {
      assert.deepStrictEqual(readInstances(join(tmpdir(), 'does-not-exist-xyz')), []);
    });

    it('finds a project by any path spelling and checks the payload path', () => {
      withRegistry((dir) => {
        const desc = descriptor();
        writeFileSync(join(dir, instanceFileName(desc.projectPath)), JSON.stringify(desc));
        assert.strictEqual(findInstanceByProjectPath(dir, 'C:\\Projects\\Game').port, 6512);
        assert.strictEqual(findInstanceByProjectPath(dir, 'C:/projects/other'), null);
      });
    });

    it('parses .NET o-format dates and applies the staleness window', () => {
      assert.ok(isFresh(descriptor()));
      assert.ok(!isFresh(descriptor({ lastHeartbeat: new Date(Date.now() - 301_000).toISOString() })));
      assert.ok(!isFresh({}));
      assert.ok(Number.isFinite(Date.parse(descriptor().startedAt)));
    });

    it('resolveUnityPort: explicit > registry > derived > legacy 6400', () => {
      withRegistry((dir) => {
        const desc = descriptor();
        writeFileSync(join(dir, instanceFileName(desc.projectPath)), JSON.stringify(desc));
        assert.strictEqual(resolveUnityPort({ UNITY_PORT: '7777' }), 7777);
        assert.strictEqual(resolveUnityPort({ UNITY_PROJECT_PATH: 'C:/projects/game', UNITY_MCP_REGISTRY_DIR: dir }), 6512);
        assert.strictEqual(
          resolveUnityPort({ UNITY_PROJECT_PATH: 'C:/projects/other', UNITY_MCP_REGISTRY_DIR: dir }),
          derivePort('C:/projects/other'));
        assert.strictEqual(resolveUnityPort({}), DEFAULT_BASE_PORT);
      });
    });

    it('resolveUnityPort ignores a stale registry entry and derives instead', () => {
      withRegistry((dir) => {
        const desc = descriptor({ lastHeartbeat: new Date(Date.now() - 400_000).toISOString() });
        writeFileSync(join(dir, instanceFileName(desc.projectPath)), JSON.stringify(desc));
        assert.strictEqual(
          resolveUnityPort({ UNITY_PROJECT_PATH: 'C:/projects/game', UNITY_MCP_REGISTRY_DIR: dir }),
          derivePort('C:/projects/game'));
      });
    });

    it('resolveUnityPort ignores a same-host descriptor whose pid is dead', () => {
      withRegistry((dir) => {
        // Fresh heartbeat, but the process is gone — must not be used.
        const desc = descriptor({ host: hostname(), pid: 999_999_999 });
        writeFileSync(join(dir, instanceFileName(desc.projectPath)), JSON.stringify(desc));
        assert.strictEqual(
          resolveUnityPort({ UNITY_PROJECT_PATH: 'C:/projects/game', UNITY_MCP_REGISTRY_DIR: dir }),
          derivePort('C:/projects/game'));
      });
    });
  });

  describe('liveness', () => {
    it('isProcessAlive: true for this process, false for a dead/invalid pid', () => {
      assert.strictEqual(isProcessAlive(process.pid), true);
      assert.strictEqual(isProcessAlive(999_999_999), false);
      assert.strictEqual(isProcessAlive(0), false);
      assert.strictEqual(isProcessAlive(-1), false);
    });

    const desc = (over = {}) => ({
      projectPath: 'p', port: 1, pid: 4242, host: 'HOST-A',
      lastHeartbeat: new Date().toISOString(), ...over,
    });

    it('isLive: same host trusts pid liveness over the heartbeat', () => {
      const ancient = desc({ lastHeartbeat: new Date(Date.now() - 9_999_000).toISOString() });
      assert.strictEqual(isLive(ancient, Date.now(), 'host-a', () => true), true);
      assert.strictEqual(isLive(desc(), Date.now(), 'HOST-A', () => false), false);
    });

    it('isLive: other/unknown host falls back to the heartbeat', () => {
      assert.strictEqual(isLive(desc({ host: 'OTHER' }), Date.now(), 'HOST-A', () => false), true);
      const stale = desc({ host: 'OTHER', lastHeartbeat: new Date(Date.now() - 400_000).toISOString() });
      assert.strictEqual(isLive(stale, Date.now(), 'HOST-A', () => true), false);
      assert.strictEqual(isLive(desc({ host: undefined }), Date.now(), 'HOST-A', () => false), true);
    });

    it('reapStale removes dead-pid descriptors and keeps live ones', () => {
      const dir = mkdtempSync(join(tmpdir(), 'mcp-reap-'));
      try {
        writeFileSync(join(dir, instanceFileName('C:/a')), JSON.stringify(desc({ projectPath: 'C:/a', pid: 1111 })));
        writeFileSync(join(dir, instanceFileName('C:/b')), JSON.stringify(desc({ projectPath: 'C:/b', pid: 2222 })));
        const reaped = reapStale(dir, Date.now(), 'HOST-A', (pid) => pid === 1111);
        assert.strictEqual(reaped, 1);
        assert.strictEqual(readInstances(dir).length, 1);
      } finally {
        rmSync(dir, { recursive: true, force: true });
      }
    });
  });
});
