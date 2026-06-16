/**
 * The Node-side Roslyn sidecar client + lazy-download factory (Plan 3 / ADR 0006). Replaces Plan 2's
 * null default factory: on start_roslyn we export the editor's project model, ensure the platform binary
 * (downloaded once from the public GitHub release), spawn it, and load the model. Any failure (no binary,
 * no network, unsupported platform) resolves to null → the RoslynManager reports 'unavailable' and the
 * lite editor layer keeps working. The base npm install ships no .NET.
 */
import { spawn as cpSpawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { promises as fs, createWriteStream } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import https from 'node:https';

// The release tag this client targets + the public release asset base. The maintainer cuts `roslyn-v*`.
export const SIDECAR_VERSION = 'roslyn-v0.6.0';
const RELEASE_BASE = 'https://github.com/burakaydinofficial/unity-editor-mcp/releases/download';

/** Newline-delimited JSON-RPC client over a spawned sidecar's stdio. The RoslynManager client contract:
 *  `call(method, params) -> Promise<result>` and `dispose()`. */
export class RoslynSidecarClient {
  constructor(child) {
    this._child = child;
    this._pending = new Map();
    this._nextId = 1;
    this._buf = '';
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', (chunk) => this._onData(chunk));
    child.on('exit', () => this._failAll(new Error('roslyn sidecar exited')));
  }

  _onData(chunk) {
    this._buf += chunk;
    let nl;
    while ((nl = this._buf.indexOf('\n')) >= 0) {
      const line = this._buf.slice(0, nl).trim();
      this._buf = this._buf.slice(nl + 1);
      if (!line) continue;
      let msg;
      try { msg = JSON.parse(line); } catch { continue; } // ignore non-JSON (e.g. stray stdout)
      const p = this._pending.get(msg.id);
      if (!p) continue;
      this._pending.delete(msg.id);
      clearTimeout(p.timer);
      if (msg.error) p.reject(Object.assign(new Error(msg.error.message || 'roslyn error'), { code: msg.error.code }));
      else p.resolve(msg.result);
    }
  }

  call(method, params = {}) {
    const id = String(this._nextId++);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this._pending.delete(id); reject(new Error(`roslyn ${method} timed out`)); }, 30000);
      this._pending.set(id, { resolve, reject, timer });
      try { this._child.stdin.write(JSON.stringify({ id, method, params }) + '\n'); }
      catch (e) { this._pending.delete(id); clearTimeout(timer); reject(e); }
    });
  }

  _failAll(err) {
    for (const [, p] of this._pending) { clearTimeout(p.timer); p.reject(err); }
    this._pending.clear();
  }

  async dispose() {
    this._failAll(new Error('roslyn sidecar disposed'));
    try { this._child.kill(); } catch { /* ignore */ }
  }
}

export function platformRid() {
  const p = process.platform, a = process.arch;
  if (p === 'win32' && a === 'x64') return 'win-x64';
  if (p === 'darwin' && a === 'x64') return 'osx-x64';
  if (p === 'darwin' && a === 'arm64') return 'osx-arm64';
  if (p === 'linux' && a === 'x64') return 'linux-x64';
  return null;
}

function httpsGetJson(url) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'unity-editor-mcp' } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) { httpsGetJson(res.headers.location).then(resolve, reject); return; }
      if (res.statusCode !== 200) { reject(new Error(`HTTP ${res.statusCode}`)); return; }
      let d = ''; res.on('data', (c) => (d += c)); res.on('end', () => { try { resolve(JSON.parse(d)); } catch (e) { reject(e); } });
    }).on('error', reject);
  });
}

function defaultDownload(url, dest) {
  return new Promise((resolve, reject) => {
    const file = createWriteStream(dest);
    let settled = false;
    const fail = (e) => { if (settled) return; settled = true; file.destroy(); fs.rm(dest, { force: true }).catch(() => {}).finally(() => reject(e)); };
    const go = (u) => https.get(u, { headers: { 'User-Agent': 'unity-editor-mcp' } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) { go(res.headers.location); return; }
      if (res.statusCode !== 200) { fail(new Error(`HTTP ${res.statusCode}`)); return; }
      res.on('error', fail);
      res.pipe(file);
      file.on('finish', () => { if (settled) return; settled = true; file.close(() => resolve()); });
    }).on('error', fail);
    file.on('error', fail);
    go(url);
  });
}

/**
 * Resolve the cached sidecar binary, downloading it once from the public release if absent.
 * Returns the executable path, or null on ANY failure (unsupported platform, no network, bad hash) so
 * the caller maps to 'unavailable'. Network deps are injectable for tests.
 */
export async function ensureBinary({ version = SIDECAR_VERSION, fetchManifest = (v) => httpsGetJson(`${RELEASE_BASE}/${v}/manifest.json`), download = defaultDownload, cacheRoot } = {}) {
  const rid = platformRid();
  if (!rid) return null; // unsupported platform → lite only
  const exe = process.platform === 'win32' ? 'unity-editor-mcp-roslyn.exe' : 'unity-editor-mcp-roslyn';
  const root = cacheRoot || path.join(os.homedir(), '.cache', 'unity-editor-mcp-roslyn', version);
  const binPath = path.join(root, rid, exe);
  try { await fs.access(binPath); return binPath; } catch { /* not cached → download */ }
  try {
    await fs.mkdir(path.dirname(binPath), { recursive: true });
    const manifest = await fetchManifest(version);
    const asset = manifest && manifest.assets && manifest.assets[rid];
    if (!asset || !asset.url || !asset.sha256) return null; // refuse an asset with no integrity hash — never spawn unverified
    await download(asset.url, binPath);
    const sha = createHash('sha256').update(await fs.readFile(binPath)).digest('hex');
    if (sha.toLowerCase() !== String(asset.sha256).toLowerCase()) { await fs.rm(binPath, { force: true }); return null; }
    if (process.platform !== 'win32') await fs.chmod(binPath, 0o755);
    return binPath;
  } catch { try { await fs.rm(binPath, { force: true }); } catch { /* ignore */ } return null; } // clean up any partial download
}

/**
 * Build the RoslynManager client factory: (conn) => sidecar client | null. Steps: export the editor model,
 * ensure the binary, spawn it, load the model. Returns null on any failure → 'unavailable'. Deps injectable.
 */
export function makeRoslynClientFactory(deps = {}) {
  const ensure = deps.ensureBinary || ensureBinary;
  const spawnFn = deps.spawn || cpSpawn;
  const readModel = deps.readModel || ((p) => fs.readFile(p, 'utf8'));
  return async (conn) => {
    let client = null, child = null;
    try {
      const exportRes = await conn.sendCommand('export_roslyn_model', {});
      const modelPath = exportRes && exportRes.modelPath;
      if (!modelPath) return null;
      const modelJson = await readModel(modelPath);
      const binPath = await ensure();
      if (!binPath) return null;
      child = spawnFn(binPath, [], { stdio: ['pipe', 'pipe', 'pipe'] });
      client = new RoslynSidecarClient(child);
      await client.call('load_model', { modelJson });
      return client; // satisfies the RoslynManager client contract: call(tool, params) + dispose()
    } catch {
      // export/spawn/load failed → unavailable; dispose any spawned child so no zombie sidecar is left running
      if (client) { try { await client.dispose(); } catch { /* ignore */ } }
      else if (child && child.kill) { try { child.kill(); } catch { /* ignore */ } }
      return null;
    }
  };
}
