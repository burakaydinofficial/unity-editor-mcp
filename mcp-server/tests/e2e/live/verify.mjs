// Version-robust verification helpers for the live E2E harness. These use ONLY signals that are stable or that we
// control: file existence, the probe's own JSON, and the stable `error CS` compiler grammar (via the shared
// read-editor-log parser). Never parse serialized YAML or version-specific log prose.
import { existsSync, readFileSync } from 'node:fs';
import { summarize } from '../../../../scripts/read-editor-log.mjs';

export function fileExists(path) {
  return existsSync(path);
}

export function readProbeEvents(path) {
  if (!existsSync(path)) return [];
  return readFileSync(path, 'utf8')
    .split(/\r?\n/)
    .map(l => l.trim())
    .filter(Boolean)
    .flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } });
}

// Returns the de-duped `error CS` list from the latest compile episode. (No `compiled` flag — it could not
// distinguish "clean compile" from "no compile ran", so callers key off `errors.length`.)
export function parseCompile(logText) {
  const { errors } = summarize(logText);
  return { errors };
}
