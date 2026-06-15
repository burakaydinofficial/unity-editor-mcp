// Drift gate: verify both halves of the bridge conform to the canonical catalog.
//
// This is the structural guarantee that replaces "two hand-maintained dispatch
// tables that happen to share strings". It fails (exit 1) when:
//   - a catalog command declares a side that has no implementation, or
//   - an implemented command is absent from the catalog (or omits that side).
//
// Intended to run in CI on every PR. Dependency-free, read-only.

import { readFile } from 'node:fs/promises';
import { getServerTools, getEditorCommands, loadCatalog } from './lib/sources.mjs';
import { buildCatalogSource, OUTPUT as GENERATED_CS } from './generate-csharp-catalog.mjs';

const VERSION = (await readFile(new URL('../VERSION', import.meta.url), 'utf8')).trim();

const catalog = await loadCatalog();
const serverTools = await getServerTools();
const editorCmds = await getEditorCommands();

// Light structural guard for hand-edits (full schema is catalog/commands.schema.json).
const shapeErrors = [];
if (!Array.isArray(catalog.commands)) {
  shapeErrors.push('catalog.commands must be an array.');
} else {
  for (const c of catalog.commands) {
    const where = c && c.name ? `command "${c.name}"` : 'an unnamed command';
    if (!c || typeof c.name !== 'string') shapeErrors.push(`${where}: missing string "name".`);
    if (!c || typeof c.category !== 'string') shapeErrors.push(`${where}: missing string "category".`);
    if (!c || !Array.isArray(c.sides) || c.sides.length === 0) shapeErrors.push(`${where}: "sides" must be a non-empty array.`);
    else if (c.sides.some((s) => s !== 'server' && s !== 'editor')) shapeErrors.push(`${where}: "sides" may only contain "server"/"editor".`);
    if (!c || typeof c.params !== 'object' || c.params === null) shapeErrors.push(`${where}: missing object "params".`);
  }
}
if (shapeErrors.length) {
  console.error(`protocol: catalog is malformed (${shapeErrors.length})`);
  for (const e of shapeErrors) console.error(`  - ${e}`);
  process.exit(1);
}

const serverNames = new Set(serverTools.map((t) => t.name));
const editorNames = new Set(editorCmds);

const catalogByName = new Map(catalog.commands.map((c) => [c.name, c]));
const catalogServer = new Set(catalog.commands.filter((c) => c.sides.includes('server')).map((c) => c.name));
const catalogEditor = new Set(catalog.commands.filter((c) => c.sides.includes('editor')).map((c) => c.name));

const problems = [];
const warnings = [];

// Baselined, intentionally-tracked gaps (see catalog.knownGaps). Keyed as
// `<missing-side>:<command>`. A matching gap is reported as a warning, not a
// failure, so the gate enforces "no NEW drift" on a brownfield.
const baselined = new Map((catalog.knownGaps ?? []).map((g) => [`${g.missing}:${g.command}`, { ...g, hit: false }]));

const note = (side, name, message) => {
  const key = `${side}:${name}`;
  if (baselined.has(key)) {
    baselined.get(key).hit = true;
    warnings.push(`known gap — ${message}`);
  } else {
    problems.push(message);
  }
};

if (catalog.protocol !== VERSION) {
  problems.push(`Catalog protocol version "${catalog.protocol}" != VERSION file "${VERSION}".`);
}

// The committed, generated C# catalog must match the canonical catalog + VERSION.
{
  const expected = (await buildCatalogSource()).replace(/\r\n?/g, '\n');
  let actual = null;
  try {
    actual = (await readFile(GENERATED_CS, 'utf8')).replace(/\r\n?/g, '\n');
  } catch {
    /* missing file handled below */
  }
  if (actual === null) {
    problems.push('Generated CommandCatalog.g.cs is missing — run: node protocol/scripts/generate-csharp-catalog.mjs');
  } else if (actual !== expected) {
    problems.push('Generated CommandCatalog.g.cs is out of date — run: node protocol/scripts/generate-csharp-catalog.mjs');
  }
}

// The Node server embeds the protocol version too (it can't read protocol/VERSION
// at npm runtime); keep it in lockstep.
{
  const handshakePath = new URL('../../mcp-server/src/core/handshake.js', import.meta.url);
  try {
    const src = await readFile(handshakePath, 'utf8');
    const match = src.match(/PROTOCOL_VERSION\s*=\s*'([^']+)'/);
    if (!match) {
      problems.push('mcp-server/src/core/handshake.js: PROTOCOL_VERSION constant not found.');
    } else if (match[1] !== VERSION) {
      problems.push(`mcp-server PROTOCOL_VERSION "${match[1]}" != VERSION file "${VERSION}".`);
    }
  } catch {
    problems.push('mcp-server/src/core/handshake.js could not be read for the PROTOCOL_VERSION check.');
  }
}

for (const name of catalogServer) {
  if (!serverNames.has(name)) note('server', name, `Catalog declares server side for "${name}" but no MCP handler is registered.`);
}
for (const name of catalogEditor) {
  if (!editorNames.has(name)) note('editor', name, `Catalog declares editor side for "${name}" but no editor dispatch case exists.`);
}

// Param-schema drift: a server command's catalog `params` is a verbatim copy of
// the JS tool's inputSchema (bootstrap-catalog.mjs), so they must stay equal —
// editing a handler's inputSchema without re-bootstrapping is silent drift the
// presence checks above can't see. Order-insensitive (key order is not contract).
const canonical = (v) => {
  if (Array.isArray(v)) return `[${v.map(canonical).join(',')}]`;
  if (v && typeof v === 'object') {
    return `{${Object.keys(v).sort().map((k) => `${JSON.stringify(k)}:${canonical(v[k])}`).join(',')}}`;
  }
  return JSON.stringify(v);
};
const serverToolByName = new Map(serverTools.map((t) => [t.name, t]));
for (const c of catalog.commands) {
  if (!c.sides.includes('server')) continue;
  const tool = serverToolByName.get(c.name);
  if (!tool) continue; // missing-handler case already reported above
  const catalogParams = c.params ?? { type: 'object' };
  const liveSchema = tool.inputSchema ?? { type: 'object' };
  if (canonical(catalogParams) !== canonical(liveSchema)) {
    problems.push(`Param schema drift for "${c.name}": catalog params differ from the server tool inputSchema — re-run bootstrap-catalog.mjs (or reconcile the handler).`);
  }
}

// KNOWN LIMITATION: param-schema drift is checked JS inputSchema -> catalog for the 3 server-side
// meta-tools ONLY. Two editor-side gaps are NOT caught: (a) a C# handler reading a parameter the
// catalog doesn't declare (or ignoring one it does) — needs a static pass over the C# handler sources;
// and (b, new in v0.5.0) the 3 Node-logic tools (execute_menu_item / create_script / analyze_screenshot)
// are cataloged sides:["editor"], so this loop skips them and their Node handler inputSchema is NOT
// compared to the catalog params — editing one without updating the catalog drifts silently. Both are
// future work (the b-gap could be closed by importing NODE_LOGIC_TOOLS and comparing handler.getDefinition).

// A baselined gap that no longer occurs is stale: the side was implemented, so
// the baseline entry should be removed to let the gate enforce it going forward.
for (const [key, g] of baselined) {
  if (!g.hit) warnings.push(`stale knownGap "${key}" — the missing side now exists; remove it from catalog.knownGaps so the gate enforces it.`);
}
for (const name of serverNames) {
  if (!catalogServer.has(name)) {
    problems.push(
      catalogByName.has(name)
        ? `MCP handler "${name}" exists but the catalog does not list "server" in its sides.`
        : `MCP handler "${name}" is not in the catalog.`,
    );
  }
}
for (const name of editorNames) {
  if (!catalogEditor.has(name)) {
    problems.push(
      catalogByName.has(name)
        ? `Editor command "${name}" exists but the catalog does not list "editor" in its sides.`
        : `Editor command "${name}" is not in the catalog.`,
    );
  }
}

const counts = {
  catalog: catalog.commands.length,
  server: serverNames.size,
  editor: editorNames.size,
};

for (const w of warnings) console.warn(`  ! ${w}`);

if (problems.length === 0) {
  console.log(
    `protocol ${VERSION}: OK — catalog ${counts.catalog} commands, ` +
      `server ${counts.server} tools, editor ${counts.editor} commands, ` +
      `no new drift (${warnings.length} known gap${warnings.length === 1 ? '' : 's'}).`,
  );
  process.exit(0);
}

console.error(`protocol ${VERSION}: ${problems.length} NEW drift problem(s) found`);
console.error(`  (catalog ${counts.catalog}, server ${counts.server}, editor ${counts.editor})`);
for (const p of problems) console.error(`  - ${p}`);
process.exit(1);
