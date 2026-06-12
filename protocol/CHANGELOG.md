# Protocol Changelog

All notable changes to the communication contract. This version line is
independent of the npm/Unity release versions (see README → Versioning).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to semantic versioning **for the wire contract**.

## [1.0.0] — initial baseline

Seeded from the existing implementation ("describe what exists").

### Added
- Canonical command catalog (`catalog/commands.json`): 66 MCP tools + 1 internal
  editor command, with mechanically-extracted parameter schemas.
- Drift gate (`scripts/check-drift.mjs`): fails on new divergence between the
  catalog, the Node MCP handler registry, and the Unity dispatch switch.
- Target wire envelope and machine error-code vocabulary.
- Per-command success `result` schemas (66) derived from the Unity handler
  returns (`resultSchemaSource: derived-from-handlers-v1`), via
  `scripts/import-result-schemas.mjs`. The structural backlog they surfaced is
  captured in `docs/quality-roadmap.md`.

### Known gaps (baselined)
- `get_component_types` — registered MCP tool with no editor dispatch case.
