# Texture Import Platform Overrides (0.20.0, E-tail slice 2) Design

> Status: design (autonomous). Section **E** tail, slice 2. `manage_asset_import_settings` handles the DEFAULT
> texture/model/audio settings but has **no per-platform texture overrides** — a core legacy/build-size need
> (Android → ETC2/ASTC, iOS → ASTC, shrinking `maxTextureSize` per platform to hit a build budget). Adds two
> actions to the existing action-dispatch handler; texture importers only.

## Actions (added to `AssetImportSettingsHandler.HandleCommand`)

- **`get_platform`** — `{ assetPath, platform? }`. With `platform`: returns that platform's
  `TextureImporterPlatformSettings` as `{ platform, overridden, maxTextureSize, format, textureCompression,
  compressionQuality, crunchedCompression }`. Without `platform`: returns `defaultPlatform` + the common set
  (Standalone / Android / iPhone / WebGL). Read-only.
- **`set_platform`** — `{ assetPath, platform (required), overridden (default true), maxTextureSize?, format?,
  textureCompression?, compressionQuality? }`. `GetPlatformTextureSettings(platform)` → apply provided fields →
  `SetPlatformTextureSettings` → `SaveAndReimport`. Returns `applied` + the re-read settings.

Platform aliases (Unity's texture-platform names): `iOS`→`iPhone`, `Windows`/`OSX`→`Standalone`. Non-texture
importer → `INVALID_STATE`. Unknown `format`/`textureCompression` string → `VALIDATION_ERROR`. Missing
`platform` on set → `VALIDATION_ERROR`.

## Floor-safety

`TextureImporter.GetPlatformTextureSettings` / `SetPlatformTextureSettings` / `GetDefaultPlatformTextureSettings`
and `TextureImporterPlatformSettings` / `TextureImporterFormat` / `TextureImporterCompression` are floor-safe
(Unity 5.5+, all supported floors). No `#if` guards; nothing for COMPATIBILITY.md. `assetPath` is already
`PathSafety`-guarded by the top-level `HandleCommand` check. No play-mode guard (matches the existing
`modify`/`apply_preset` actions; this is an asset, not scene state).

## Catalog & testing

Extend the `manage_asset_import_settings` action enum with `get_platform`/`set_platform` + params (`platform`,
`format`, `textureCompression`, `compressionQuality`, `maxTextureSize`, `overridden`). EditMode test creates a
temp PNG + a temp `.txt` in `Assets/`: set_platform Android (maxTextureSize+compression) → get_platform reflects
`overridden`+values → no-platform lists default+common → invalid format VALIDATION_ERROR → non-texture
INVALID_STATE → missing-platform VALIDATION_ERROR; deletes both temp assets in TearDown. Dogfood on 2020.3.

## Cadence

Branch toward batched **0.20.0** (slice 2). Remaining: granular prefab-override apply, dependency paging.
