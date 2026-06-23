# Unity Editor MCP — Tool Reference

> **Generated** from `protocol/catalog/commands.json` (protocol `1.0.0`) — do not edit by hand.
> Regenerate with `node protocol/scripts/generate-tools-reference.mjs`.

**98 commands across 18 categories.** Each is reached via the generic
`call_unity_tool(instance, name, params)` meta-tool after on-demand discovery with `list_unity_tools` —
the connected editor advertises these, not the MCP server (ADR 0006). Two internal commands are omitted.

## Analysis (6)

### `analyze_scene_contents`
Analyze and get statistics about the current scene

- **Params:**
  - `includeInactive` (boolean) — Include inactive objects in analysis. Default: true
  - `groupByType` (boolean) — Group results by component types. Default: true
  - `includePrefabInfo` (boolean) — Include prefab connection info. Default: true
  - `includeMemoryInfo` (boolean) — Include memory usage estimates. Default: false
- **Result:** `sceneName`, `statistics`, `componentDistribution`, `rendering`, `lighting`, `memoryInfo`, `summary`

### `find_by_component`
Find all GameObjects that have a specific component type

- **Params:**
  - `componentType` (string, required) — Component type to search for (e.g., "Light", "Collider", "AudioSource")
  - `includeInactive` (boolean) — Include inactive GameObjects. Default: true
  - `searchScope` (string) — Where to search: current scene, prefabs, or all. Default: "scene" — one of: scene, prefabs, all
  - `matchExactType` (boolean) — Match exact type only (not derived types). Default: true
  - `limit` (number) — Max results returned (default 200); the response signals truncated/totalFound/returned. Caps the response so a big scene can't blow the 1MB frame budget.
- **Result:** `componentType`, `searchScope`, `results`, `totalFound`, `activeCount`, `sceneCount`, `prefabCount`, `summary`

### `find_missing_scripts`
Find GameObjects in the active scene with missing-script MonoBehaviours — a deleted/moved .cs leaves a dangling component (a legacy-project staple, F5). Returns {objects:[{path,missingCount}], totalObjects, totalMissing}; capped by limit (default 200) with a truncated signal. Read-only — clean up with remove_missing_scripts.

- **Params:**
  - `limit` (number) — Max objects returned (default 200); the response signals truncated/totalObjects.
- **Result:** `objects`, `totalObjects`, `totalMissing`, `truncated`

### `get_component_values`
Get all properties and values of a specific component

- **Params:**
  - `gameObjectName` (string, required) — Name of the GameObject
  - `componentType` (string, required) — Type of component (e.g., "Light", "Camera", "Rigidbody")
  - `componentIndex` (number) — Index if multiple components of same type. Default: 0
  - `includePrivateFields` (boolean) — Include non-public fields. Default: false
  - `includeInherited` (boolean) — Include inherited properties. Default: true
- **Result:** `gameObject`, `componentType`, `componentIndex`, `enabled`, `properties`, `summary`

### `get_gameobject_details`
Get detailed information about a specific GameObject

- **Params:**
  - `gameObjectName` (string, required) — Name of the GameObject to inspect
  - `path` (string) — Full hierarchy path to the GameObject (use either name or path)
  - `includeChildren` (boolean) — Include full hierarchy details. Default: false
  - `includeComponents` (boolean) — Include all component details. Default: true
  - `includeMaterials` (boolean) — Include material information. Default: false
  - `maxDepth` (number) — Maximum depth for child traversal. Default: 3, Range: 0-10
- **Result:** `name`, `path`, `isActive`, `isStatic`, `tag`, `layer`, `transform`, `components`, `children`, `prefabInfo`, `summary`

### `get_object_references`
Find all references to and from a GameObject

- **Params:**
  - `gameObjectName` (string, required) — Name of the GameObject to analyze references for
  - `includeAssetReferences` (boolean) — Include references to assets (materials, meshes, etc). Default: true
  - `includeHierarchyReferences` (boolean) — Include parent/child hierarchy references. Default: true
  - `searchInPrefabs` (boolean) — Also search for references in prefab assets. Default: false
- **Result:** `targetObject`, `targetPath`, `isPrefab`, `references`, `stats`, `summary`

## Asset (15)

### `analyze_asset_dependencies`
Analyze Unity asset dependencies (get dependencies, dependents, circular deps, unused assets, size impact)

- **Params:**
  - `action` (string, required) — The analysis action to perform — one of: get_dependencies, get_dependents, analyze_circular, find_unused, analyze_size_impact, validate_references
  - `assetPath` (string) — Path to the asset (required for specific asset analysis)
  - `recursive` (boolean) — Whether to analyze dependencies recursively (for get_dependencies)
  - `includeBuiltIn` (boolean) — Whether to include built-in assets in analysis (for find_unused)
  - `limit` (number) — Max items to return for get_dependencies/get_dependents (default 100). Pair with offset to page large dependency lists; results include total + hasMore.
  - `offset` (number) — Start index for get_dependencies/get_dependents paging (default 0).
- **Result:** varies (one of several shapes)

### `create_material`
Create a new material in Unity with specified shader and properties

- **Params:**
  - `materialPath` (string, required) — Asset path for the material (must start with Assets/ and end with .mat)
  - `shader` (string) — Shader to use (e.g., "Standard", "Unlit/Color", "Universal Render Pipeline/Lit")
  - `properties` (object) — Material properties to set (e.g., {"_Color": [1,0,0,1], "_Metallic": 0.5})
  - `copyFrom` (string) — Path to existing material to copy from
  - `overwrite` (boolean) — Whether to overwrite existing material
- **Result:** `success`, `materialPath`, `shader`, `guid`, `propertiesSet`, `copiedFrom`, `message`

### `create_prefab`
Create a new prefab from a GameObject or from scratch

- **Params:**
  - `gameObjectPath` (string) — Path to GameObject to convert to prefab
  - `prefabPath` (string, required) — Asset path where prefab should be saved (must start with Assets/ and end with .prefab)
  - `createFromTemplate` (boolean) — Create empty prefab without source GameObject
  - `overwrite` (boolean) — Overwrite existing prefab if it exists
- **Result:** `success`, `prefabPath`, `guid`, `message`

### `create_prefab_variant`
Create a prefab variant of a base prefab (E2). Provide basePrefabPath or baseGuid + variantPath (Assets/....prefab). Refuses an existing variantPath unless overwrite. Errors: NOT_FOUND (base), PATH_EXISTS.

- **Params:**
  - `basePrefabPath` (string)
  - `baseGuid` (string)
  - `variantPath` (string, required)
  - `overwrite` (boolean)
- **Result:** `variantPath`, `guid`, `basePrefabPath`

### `create_scriptable_object`
Create a ScriptableObject of a named type and save it as an asset (E1). typeName is a FullName (discover concrete ScriptableObject types via find_implementations of UnityEngine.ScriptableObject); assetPath is Assets/....asset. Refuses an existing path unless overwrite. Pair with set_serialized_properties to populate fields (private [SerializeField] included). Errors: TYPE_NOT_FOUND, NOT_A_SCRIPTABLE_OBJECT, PATH_EXISTS.

- **Params:**
  - `typeName` (string, required) — FullName/AQN of a ScriptableObject subtype.
  - `assetPath` (string, required) — Assets/....asset destination.
  - `overwrite` (boolean) — Replace an existing asset at the path (default false).
- **Result:** `assetPath`, `guid`, `type`

### `exit_prefab_mode`
Exit prefab mode and return to the main scene

- **Params:**
  - `saveChanges` (boolean) — Save changes before exiting (default: true)
- **Result:** `success`, `wasInPrefabMode`, `changesSaved`, `prefabPath`, `message`

### `instantiate_prefab`
Instantiate a prefab in the scene

- **Params:**
  - `prefabPath` (string, required) — Asset path to the prefab (must start with Assets/ and end with .prefab)
  - `position` (object) — World position for the instantiated object
  - `rotation` (object) — Rotation in Euler angles
  - `parent` (string) — Parent GameObject path
  - `name` (string) — Override name for the instantiated object
- **Result:** `success`, `gameObjectPath`, `prefabPath`, `position`, `rotation`, `parent`, `name`, `message`

### `manage_asset_database`
Manage Unity Asset Database operations (find, info, create folders, move, copy, delete, refresh, save). delete_asset is DRY-RUN by default: it returns the asset's dependents + confirmRequired; pass confirm:true to actually delete (H3).

- **Params:**
  - `action` (string, required) — The action to perform — one of: find_assets, get_asset_info, create_folder, delete_asset, move_asset, copy_asset, refresh, save
  - `filter` (string) — Search filter for find_assets (e.g., "t:Texture2D", "l:UI")
  - `searchInFolders` (array) — Folders to search in for find_assets (optional)
  - `assetPath` (string) — Path to the asset (must start with "Assets/")
  - `folderPath` (string) — Path for folder creation (must start with "Assets/")
  - `fromPath` (string) — Source path for move/copy operations
  - `toPath` (string) — Destination path for move/copy operations
  - `confirm` (boolean) — Required for delete_asset: without confirm:true the delete is a dry-run returning dependents + confirmRequired; pass confirm:true to actually delete (H3).
- **Result:** varies (one of several shapes)

### `manage_asset_import_settings`
Manage Unity asset import settings (get, modify, apply presets, reimport, per-platform texture overrides)

- **Params:**
  - `action` (string, required) — The action to perform — one of: get, modify, apply_preset, reimport, get_platform, set_platform
  - `assetPath` (string, required) — Path to the asset (must start with "Assets/")
  - `settings` (object) — Import settings to apply (required for modify action)
  - `preset` (string) — Name of the preset to apply (required for apply_preset action)
  - `platform` (string) — Texture platform name for get_platform/set_platform (Standalone, Android, iPhone, WebGL, ...; aliases iOS->iPhone, Windows/OSX->Standalone). Omit on get_platform to list the default + common platforms.
  - `overridden` (boolean) — set_platform: whether this platform overrides the default (default true).
  - `maxTextureSize` (number) — set_platform: per-platform max texture size (e.g. 512, 1024, 2048).
  - `format` (string) — set_platform: TextureImporterFormat (e.g. ASTC_6x6, ETC2_RGBA8, RGBA32, DXT5, Automatic).
  - `textureCompression` (string) — set_platform: TextureImporterCompression (Uncompressed, Compressed, CompressedHQ, CompressedLQ).
  - `compressionQuality` (number) — set_platform: compression quality 0-100.
- **Result:** varies (one of several shapes)

### `manage_prefab_overrides`
Inspect and granularly apply/revert prefab-instance overrides (vs save_prefab's all-or-nothing). Requires gameObjectPath to a prefab instance. action=list (read-only) returns {propertyModifications:[{target,propertyPath,value,objectReference}], propertyModificationCount, truncated, addedComponents, removedComponents, addedGameObjects, prefabPath} (limit caps the listing, default 100). apply_property/revert_property take componentType (or 'GameObject') + propertyPath. apply_all/revert_all apply or revert the whole instance. Write actions refuse in play mode.

- **Params:**
  - `action` (string) — Operation (default list). — one of: list, apply_property, revert_property, apply_all, revert_all
  - `gameObjectPath` (string, required) — Path to the prefab instance GameObject in the scene (required).
  - `componentType` (string) — apply_property/revert_property: component type name (e.g. Transform, MeshRenderer) or 'GameObject' for the GameObject itself.
  - `propertyPath` (string) — apply_property/revert_property: serialized property path (e.g. m_LocalPosition.x, m_Name). The list action surfaces overridden paths.
  - `limit` (number) — list: max property modifications to return (default 100).
- **Result:** `success`, `action`, `message`

### `modify_material`
Modify properties of an existing material

- **Params:**
  - `materialPath` (string, required) — Asset path to the material (must start with Assets/ and end with .mat)
  - `properties` (object, required) — Material properties to modify (e.g., {"_Color": [1,0,0,1], "_Metallic": 0.5})
  - `shader` (string) — Change the shader (e.g., "Standard", "Unlit/Color")
- **Result:** `success`, `materialPath`, `propertiesModified`, `shaderChanged`, `previousShader`, `newShader`, `message`

### `modify_prefab`
Modify properties of an existing prefab

- **Params:**
  - `prefabPath` (string, required) — Asset path to the prefab (must start with Assets/ and end with .prefab)
  - `modifications` (object, required) — Object containing properties to modify
  - `applyToInstances` (boolean) — Apply changes to scene instances of the prefab
- **Result:** `success`, `prefabPath`, `modifiedProperties`, `affectedInstances`, `message`

### `open_prefab`
Open a prefab asset in prefab mode for editing

- **Params:**
  - `prefabPath` (string, required) — Asset path to the prefab (must start with Assets/ and end with .prefab)
  - `focusObject` (string) — Optional path to object within prefab to focus on (relative to prefab root)
  - `isolateObject` (boolean) — Isolate the focused object in the hierarchy (default: false)
- **Result:** `success`, `prefabPath`, `isInPrefabMode`, `prefabContentsRoot`, `focusedObject`, `wasAlreadyOpen`, `message`

### `save_prefab`
Save current prefab changes in prefab mode or save a GameObject as prefab override

- **Params:**
  - `gameObjectPath` (string) — Path to GameObject to save as prefab override (optional - if not provided, saves current prefab in prefab mode)
  - `includeChildren` (boolean) — Include child object overrides when saving (default: true)
- **Result:** varies (one of several shapes)

### `unpack_prefab` — _⚠️ destructive (confirm-gated)_
Unpack a prefab instance in the scene (E2): mode 'regular' unpacks the outermost root, 'complete' unpacks the whole hierarchy. Target by gameObjectPath or instanceId. Refuses in play mode (PLAY_MODE) and on a non-instance (NOT_A_PREFAB_INSTANCE). Registers Undo.

- **Params:**
  - `gameObjectPath` (string)
  - `instanceId` (number)
  - `mode` (string) — regular | complete (default regular).
- **Result:** `gameObjectPath`, `mode`

## Code (8)

### `export_roslyn_model`
Export Unity's CompilationPipeline project model (per-assembly source paths, reference dll paths, defines) to Library/UnityEditorMCP/roslyn-model.json for the out-of-process Roslyn sidecar. Returns the written path + a generation id.

- **Params:**
  - _none_
- **Result:** `modelPath`, `generation`, `assemblies`

### `find_implementations`
Find subtypes of a named class or implementors of a named interface, via UnityEditor.TypeCache (fast, indexed over the compiled assemblies).

- **Params:**
  - `typeName` (string, required) — Simple or full class/interface name. A simple name resolves to the FIRST match; the result flags "ambiguous" when several types share it — pass a full name to disambiguate.
- **Result:** `type`, `count`, `ambiguous`, `ambiguousMatches`, `implementors`

### `find_references`
Find textual references to an identifier across the project Assets scripts (comments and string literals excluded). Syntactic: same-named members across types/overloads are not disambiguated. Returns file, line, the matching line text, and resolution="syntactic".

- **Params:**
  - `name` (string, required) — Identifier to search for (word-boundary match).
  - `maxResults` (number) — Maximum references to return (default 200).
- **Result:** `name`, `count`, `truncated`, `resolution`, `references`, `note`

### `find_symbol`
Find C# symbol declarations by exact name across the project Assets scripts. Returns each match's file, line range, kind, and signature. Syntactic (no semantic resolution).

- **Params:**
  - `name` (string, required) — Exact symbol name to find.
  - `kind` (string) — Optional: only return declarations of this kind. — one of: class, struct, interface, enum, method, property
  - `maxResults` (number) — Maximum matches to return (default 200).
- **Result:** `name`, `count`, `truncated`, `matches`

### `get_symbol_body`
Return the source text of a named symbol (type/method/property) within a C# file — the declaration through its matching closing brace. Syntactic. Accepts an "Assets/..." path or an absolute .cs path under the project.

- **Params:**
  - `path` (string, required) — Path to the .cs file ("Assets/..." or absolute).
  - `name` (string, required) — Exact symbol name within the file.
- **Result:** `path`, `name`, `kind`, `startLine`, `endLine`, `source`

### `get_symbols`
Outline a C# file: its types (class/struct/interface/enum), methods, and properties with line ranges. Syntactic (no semantic resolution). Accepts an "Assets/..." path or an absolute .cs path under the project.

- **Params:**
  - `path` (string, required) — Path to the .cs file ("Assets/..." or absolute).
- **Result:** `path`, `count`, `symbols`

### `get_type_members`
List the members (fields, properties, methods, events) of a named C# type from Unity's compiled assemblies, with signatures, visibility, and attributes.

- **Params:**
  - `typeName` (string, required) — Simple or full type name (e.g. "Player" or "MyGame.Player"). A simple name resolves to the FIRST match; the result flags "ambiguous" when several types share it — pass a full name to disambiguate.
  - `includeInherited` (boolean) — Include inherited members (default false — declared-only).
- **Result:** `type`, `count`, `ambiguous`, `ambiguousMatches`, `members`

### `resolve_symbol`
Resolve a C# identifier NAME to its declaring type(s) and member(s) using Unity's compiled assemblies (reflection/TypeCache). Name-based: returns a ranked candidate list — same-named symbols across types are NOT disambiguated (that needs the Roslyn sidecar). Pass "name", or "path"+"position" to extract the token name at that source location.

- **Params:**
  - `name` (string) — Identifier name to resolve. Optional if path+position are given.
  - `path` (string) — A .cs file under the project, used with position to extract the token name.
  - `position` (object) — { line, column } (1-based) of the token in path.
  - `maxResults` (number) — Cap on candidates (default 50).
- **Result:** `name`, `count`, `truncated`, `candidates`

## Compilation (3)

### `get_compilation_state`
Get current Unity compilation state, errors, and warnings with enhanced detection

- **Params:**
  - `includeMessages` (boolean) — Include detailed compilation messages (default: true)
  - `maxMessages` (number) — Maximum number of messages to return (default: 50)
- **Result:** varies (one of several shapes)

### `start_compilation_monitoring`
Start monitoring Unity compilation events and error detection

- **Params:**
  - _none_
- **Result:** `success`, `isMonitoring`, `message`

### `stop_compilation_monitoring`
Stop monitoring Unity compilation events

- **Params:**
  - _none_
- **Result:** `success`, `isMonitoring`, `message`

## Component (6)

### `add_component`
Add a component to a GameObject in Unity

- **Params:**
  - `gameObjectPath` (string, required) — Path to the GameObject (e.g., "/Player" or "/Canvas/Button")
  - `componentType` (string, required) — Type of component to add (e.g., "Rigidbody", "BoxCollider", "Light")
  - `properties` (object) — Initial property values for the component
- **Result:** `success`, `componentType`, `gameObjectPath`, `message`, `appliedProperties`

### `get_component_types`
Get available component types in Unity

- **Params:**
  - `category` (string) — Filter by category (e.g., "Physics", "Rendering", "UI")
  - `search` (string) — Search for component types by name
  - `onlyAddable` (boolean) — Return only components that can be added to GameObjects (default: false)
- **Result:** `componentTypes`, `totalCount`, `categories`, `searchTerm`, `onlyAddable`

### `list_components`
List all components on a GameObject in Unity

- **Params:**
  - `gameObjectPath` (string, required) — Path to the GameObject (e.g., "/Player" or "/Canvas/Button")
  - `includeInherited` (boolean) — Include inherited base component types (default: false)
- **Result:** `success`, `gameObjectPath`, `components`, `componentCount`, `message`

### `modify_component`
Modify component properties via reflection on public fields/properties — the FALLBACK path (refuses scene mutation in play mode). For serialized fields — private [SerializeField], Inspector-accurate writes with Undo, nested structs/arrays, [SerializeReference] — prefer set_serialized_properties (the SerializedObject core); reflection cannot reach those and silently no-ops nested value-type writes.

- **Params:**
  - `gameObjectPath` (string, required) — Path to the GameObject (e.g., "/Player" or "/Canvas/Button")
  - `componentType` (string, required) — Type of component to modify (e.g., "Rigidbody", "Light")
  - `componentIndex` (number) — Index of component if multiple of same type exist (default: 0)
  - `properties` (object, required) — Properties to modify with their new values
- **Result:** `success`, `componentType`, `componentIndex`, `modifiedProperties`, `message`

### `remove_component`
Remove a component from a GameObject in Unity

- **Params:**
  - `gameObjectPath` (string, required) — Path to the GameObject (e.g., "/Player" or "/Canvas/Button")
  - `componentType` (string, required) — Type of component to remove (e.g., "Rigidbody", "BoxCollider")
  - `componentIndex` (number) — Index of component if multiple of same type exist (default: 0)
- **Result:** `success`, `removed`, `componentType`, `componentIndex`, `message`

### `reorder_component`
Reorder a component among its siblings on a GameObject (F4) — component order affects execution/serialization. Moves the component (componentType + componentIndex, default 0) up or down by count steps (clamped at the ends; `moved` reports the steps taken). Registers Undo; refuses in play mode.

- **Params:**
  - `gameObjectPath` (string, required)
  - `componentType` (string, required)
  - `componentIndex` (number) — Which instance of the type (default 0).
  - `direction` (string) — Move up or down (default up). — one of: up, down
  - `count` (number) — Number of steps (default 1).
- **Result:** `componentType`, `direction`, `moved`

## Console (2)

### `clear_console`
Clear Unity Editor console logs

- **Params:**
  - `clearOnPlay` (boolean) — Clear console when entering play mode
  - `clearOnRecompile` (boolean) — Clear console on script recompilation
  - `clearOnBuild` (boolean) — Clear console when building
  - `preserveWarnings` (boolean) — Preserve warning messages when clearing
  - `preserveErrors` (boolean) — Preserve error messages when clearing
- **Result:** `success`, `message`, `clearedCount`, `remainingCount`, `settingsUpdated`, `clearOnPlay`, `clearOnRecompile`, `clearOnBuild`, `timestamp`, `preservedWarnings`, `preservedErrors`

### `enhanced_read_logs`
Read Unity console logs with advanced filtering

- **Params:**
  - `count` (number) — Number of logs to retrieve (1-1000, default: 100)
  - `logTypes` (array) — Filter by log types (default: ["All"])
  - `filterText` (string) — Filter logs containing this text (case-insensitive)
  - `includeStackTrace` (boolean) — Include stack traces in results
  - `format` (string) — Output format for logs — one of: detailed, compact, json, plain
  - `sinceTimestamp` (string) — Only return logs after this timestamp (ISO 8601)
  - `untilTimestamp` (string) — Only return logs before this timestamp (ISO 8601)
  - `sortOrder` (string) — Sort order for logs — one of: newest, oldest
  - `groupBy` (string) — Group logs by criteria — one of: none, type, file, time
- **Result:** varies (one of several shapes)

## Editor (9)

### `get_project_settings`
Get key Unity project settings: product/company name, bundle version, color space, default screen size, scripting backend, API compatibility level, scripting define symbols, and the active build target.

- **Params:**
  - _none_
- **Result:** `productName`, `companyName`, `bundleVersion`, `colorSpace`, `defaultScreenWidth`, `defaultScreenHeight`, `runInBackground`, `activeBuildTarget`, `selectedBuildTargetGroup`, `scriptingBackend`, `apiCompatibilityLevel`, `scriptingDefineSymbols`

### `list_packages`
List the Unity project UPM packages: directly-requested dependencies (from manifest.json) plus the full resolved set with each package source (from packages-lock.json).

- **Params:**
  - _none_
- **Result:** `dependencies`, `resolved`, `count`

### `manage_layers`
Manage Unity project layers (add, remove, list, convert)

- **Params:**
  - `action` (string, required) — Action to perform — one of: add, remove, get, get_by_name, get_by_index
  - `layerName` (string) — Name of the layer
  - `layerIndex` (integer) — Layer index (0-31)
- **Result:** varies (one of several shapes)

### `manage_packages` — _⚠️ destructive (confirm-gated)_
Add or remove a UPM package. Resolution is asynchronous (the editor recompiles/reloads); the call returns once the request is queued — verify with list_packages afterwards. Destructive; requires confirm:true (H3).

- **Params:**
  - `action` (string, required) — Whether to add or remove the package. — one of: add, remove
  - `packageId` (string, required) — Package identifier, e.g. "com.unity.textmeshpro" or "com.unity.textmeshpro@3.0.6" or a git URL.
- **Result:** `message`, `action`, `packageId`

### `manage_selection`
Manage Unity Editor selection (get, set, clear)

- **Params:**
  - `action` (string, required) — Action to perform on selection — one of: get, set, clear, get_details
  - `objectPaths` (array) — Array of GameObject paths for set action
  - `includeDetails` (boolean) — Include detailed information for get action
- **Result:** varies (one of several shapes)

### `manage_tags`
Manage Unity project tags (add, remove, list)

- **Params:**
  - `action` (string, required) — Action to perform: add, remove, or get tags — one of: add, remove, get
  - `tagName` (string) — Name of the tag (required for add and remove actions)
- **Result:** varies (one of several shapes)

### `manage_tools`
Manage Unity Editor tools and plugins (list, activate, deactivate, refresh)

- **Params:**
  - `action` (string, required) — The action to perform — one of: get, activate, deactivate, refresh
  - `toolName` (string) — Name of the tool (required for activate/deactivate)
  - `category` (string) — Filter tools by category (optional for get action)
- **Result:** varies (one of several shapes)

### `manage_windows`
Manage Unity Editor windows (list, focus, get state)

- **Params:**
  - `action` (string, required) — Action to perform on windows — one of: get, focus, get_state
  - `windowType` (string) — Type of window (e.g., SceneView, GameView, InspectorWindow)
  - `includeHidden` (boolean) — Include hidden/minimized windows in get action
- **Result:** varies (one of several shapes)

### `set_project_setting` — _⚠️ destructive (confirm-gated)_
Set one project setting by key (PlayerSettings). Supported keys: productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, runInBackground, colorSpace, scriptingDefineSymbols. Destructive; requires confirm:true (H3).

- **Params:**
  - `key` (string, required) — Which setting to set. — one of: productName, companyName, bundleVersion, defaultScreenWidth, defaultScreenHeight, runInBackground, colorSpace, scriptingDefineSymbols
  - `value` (object, required) — New value; type depends on the key (string, number, or boolean).
- **Result:** `message`, `key`

## Gameobject (6)

### `create_gameobject`
Create a GameObject in Unity scene

- **Params:**
  - `name` (string) — Name of the GameObject (default: "GameObject")
  - `primitiveType` (string) — Type of primitive to create — one of: cube, sphere, cylinder, capsule, plane, quad
  - `position` (object) — World position
  - `rotation` (object) — Rotation in Euler angles
  - `scale` (object) — Local scale
  - `parentPath` (string) — Path to parent GameObject (e.g., "/Parent/Child")
  - `tag` (string) — Tag to assign to the GameObject
  - `layer` (number) — Layer index (0-31)
- **Result:** `id`, `name`, `path`, `position`, `rotation`, `scale`, `tag`, `layer`, `isActive`, `error`

### `delete_gameobject` — _⚠️ destructive (confirm-gated)_
Delete GameObject(s) from Unity scene. Requires confirm:true (H3).

- **Params:**
  - `path` (string) — Path to a single GameObject to delete
  - `paths` (array) — Array of paths to multiple GameObjects to delete
  - `includeChildren` (boolean) — Whether to delete children (default: true)
- **Result:** `deletedCount`, `deleted`, `notFound`, `notFoundCount`, `error`

### `find_gameobject`
Find GameObjects in Unity scene by name, tag, or layer

- **Params:**
  - `name` (string) — Name to search for
  - `tag` (string) — Tag to search for
  - `layer` (number) — Layer index to search for (0-31)
  - `exactMatch` (boolean) — Whether to match name exactly (default: true)
  - `limit` (number) — Max results returned (default 200); the response signals truncated/total. Caps the response so a big scene can't blow the 1MB frame budget.
- **Result:** `count`, `objects`, `error`

### `get_hierarchy`
Get the Unity scene hierarchy

- **Params:**
  - `includeInactive` (boolean) — Include inactive GameObjects (default: true)
  - `maxDepth` (number) — Maximum depth to traverse (-1 for unlimited, default: -1)
  - `includeComponents` (boolean) — Include component information (default: false)
  - `maxNodes` (number) — Max total nodes returned (default 1000); the response signals truncated. maxDepth bounds depth, this bounds total breadth so a big scene can't blow the 1MB frame budget.
- **Result:** `sceneName`, `objectCount`, `hierarchy`, `error`

### `modify_gameobject`
Modify properties of an existing GameObject

- **Params:**
  - `path` (string, required) — Path to the GameObject to modify (required)
  - `space` (string) — F3: space for position+rotation — world (default) sets transform.position/rotation; local sets localPosition/localEulerAngles. scale is always local. — one of: world, local
  - `name` (string) — New name for the GameObject
  - `position` (object) — New position (world by default; local if space:local)
  - `rotation` (object) — New rotation in Euler angles
  - `scale` (object) — New local scale
  - `active` (boolean) — Set active state
  - `parentPath` (string|null) — Path to new parent GameObject (null to unparent)
  - `tag` (string) — New tag
  - `layer` (number) — New layer index (0-31)
- **Result:** `id`, `name`, `path`, `position`, `rotation`, `scale`, `tag`, `layer`, `isActive`, `modified`, `error`

### `remove_missing_scripts` — _⚠️ destructive (confirm-gated)_
Remove missing-script MonoBehaviours from the active scene (F5) — all, or the specified gameObjectPaths. Registers Undo, marks the scene dirty, refuses in play mode (PLAY_MODE). Destructive; requires confirm:true (H3). Returns {removed, objectsAffected, notFound}.

- **Params:**
  - `gameObjectPaths` (array) — Specific GameObject hierarchy paths to clean; omit to clean the whole active scene.
- **Result:** `removed`, `objectsAffected`, `notFound`, `notFoundCount`

## Instances (3)

### `call_unity_tool`
Invoke any tool a connected Unity editor supports, by name (discover names + schemas with list_unity_tools). Params are validated against the editor-advertised schema before the call, so a validation error tells you exactly what to fix. The "instance" (a project path or port) is required — there is no default editor, so every call names its target. To trim the response, pass params.fields — an array of dot-paths (GraphQL-style); omit for the full result.

- **Params:**
  - `instance` (string, required) — REQUIRED — the target editor (a project path or port). There is no default instance: every call must name its editor. Use list_unity_instances to see what is running.
  - `tool` (string, required) — The tool name to invoke (see list_unity_tools).
  - `params` (object) — Parameters for the tool, matching its advertised schema. Also accepts an optional reserved "fields": a string[] of dot-paths that projects the result to just those fields (e.g. ["count","objects.name","state.isPlaying"]); arrays are transparent (the path applies to each element). Omit for all fields. Discover the shape by calling once without "fields".
- **Result:** object

### `list_unity_instances`
List the Unity editor instances currently running and discoverable (project path, Unity version, port). There is no default target — pass an instance from this list as the required "instance" to list_unity_tools / call_unity_tool. Works even when no editor is connected.

- **Params:**
  - `includeStale` (boolean) — Also include descriptors whose process is gone / heartbeat is stale (for diagnosing a missing editor). Default: false.
- **Result:** `instances`, `count`, `registryDir`

### `list_unity_tools`
List the tools a connected Unity editor actually supports, with their schemas (learned from the editor at runtime). Discover what call_unity_tool can invoke on a given instance. Returns names + descriptions by default; pass "name" for one tool's full parameter schema AND result-field hints (its response shape — read these to drive call_unity_tool's `fields` projection), or "category" to filter.

- **Params:**
  - `instance` (string, required) — REQUIRED — the target editor (a project path or port). There is no default instance: every call must name its editor. Use list_unity_instances to see what is running.
  - `category` (string) — Only return tools in this category (e.g. "gameobject", "scene").
  - `name` (string) — Return the full {name, category, description, params, result} schema for just this one tool — including its result-field hints (the response shape).
- **Result:** `instance`, `count`, `tools`, `tool`, `schemasAvailable`

## Menu (2)

### `execute_menu_item`
Execute Unity Editor menu items

- **Params:**
  - `menuPath` (string, required) — Unity menu path (e.g., "Assets/Refresh", "Window/General/Console")
  - `action` (string) — Action to perform: execute menu item or get available menus — one of: execute, get_available_menus
  - `alias` (string) — Menu alias for common operations (e.g., "refresh", "console")
  - `parameters` (object) — Additional parameters for menu execution (if supported)
  - `safetyCheck` (boolean) — Enable safety checks to prevent execution of dangerous menu items
- **Result:** —

### `invoke_static_method`
Invoke a static method by type + name with JSON args (G6). ARBITRARY CODE EXECUTION — gated by a DEFAULT-DENY allow-list (H2): INVOKE_DENIED unless 'FullType.Method' matches a pattern in the UNITY_MCP_INVOKE_ALLOW env var (comma-separated) or ProjectSettings/UnityEditorMcpInvokePolicy.json ({allowInvoke:[...]}); patterns: exact 'Ns.Type.Method', prefix 'Ns.Type.*', or '*'. Resolves the type across loaded assemblies, picks the static overload matching the arg count, marshals args via JSON, returns {type, method, returnType, isVoid, result}.

- **Params:**
  - `typeName` (string, required) — Full type name (e.g. 'MyTools.BuildUtils' or 'UnityEditor.EditorApplication').
  - `methodName` (string, required) — Static method name.
  - `args` (array) — JSON args, marshaled to the method's parameter types in order (overload chosen by arg count).
  - `assemblyName` (string) — Optional assembly name to disambiguate the type.
- **Result:** `success`, `type`, `method`, `returnType`, `isVoid`

## Playmode (4)

### `get_editor_state`
Get current Unity editor state including play mode status

- **Params:**
  - _none_
- **Result:** `status`, `state`

### `pause_game`
Pause or resume Unity play mode

- **Params:**
  - _none_
- **Result:** `status`, `message`, `state`

### `play_game`
Start Unity play mode to test the game

- **Params:**
  - _none_
- **Result:** `status`, `message`, `state`

### `stop_game`
Stop Unity play mode and return to edit mode

- **Params:**
  - _none_
- **Result:** `status`, `message`, `state`

## Scene (6)

### `create_scene`
Create a new scene in Unity

- **Params:**
  - `sceneName` (string, required) — Name of the scene to create
  - `path` (string) — Path where the scene should be saved (e.g., "Assets/Scenes/"). If not specified, defaults to "Assets/Scenes/"
  - `loadScene` (boolean) — Whether to load the scene after creation (default: true)
  - `addToBuildSettings` (boolean) — Whether to add the scene to build settings (default: false)
- **Result:** `sceneName`, `path`, `sceneIndex`, `isLoaded`, `summary`

### `get_scene_info`
Get detailed information about a scene

- **Params:**
  - `scenePath` (string) — Full path to the scene file. If not provided, gets info about current scene.
  - `sceneName` (string) — Name of the scene. Use either scenePath or sceneName, not both.
  - `includeGameObjects` (boolean) — Include list of root GameObjects in the scene (only for loaded scenes). Default: false
- **Result:** `sceneName`, `scenePath`, `isLoaded`, `isActive`, `isDirty`, `buildIndex`, `fileSize`, `lastModified`, `rootGameObjects`, `rootObjectCount`, `totalObjectCount`, `summary`

### `list_scenes`
List all scenes in the Unity project

- **Params:**
  - `includeLoadedOnly` (boolean) — Only include currently loaded scenes (default: false)
  - `includeBuildScenesOnly` (boolean) — Only include scenes in build settings (default: false)
  - `includePath` (string) — Filter scenes by path pattern (e.g., "Levels" to find scenes in Levels folder)
- **Result:** `scenes`, `totalCount`, `loadedCount`, `inBuildCount`, `summary`

### `load_scene`
Load a scene in Unity

- **Params:**
  - `scenePath` (string) — Full path to the scene file (e.g., "Assets/Scenes/MainMenu.unity")
  - `sceneName` (string) — Name of the scene to load (must be in build settings). Use either scenePath or sceneName, not both.
  - `loadMode` (string) — How to load the scene. Single replaces current scene(s), Additive adds to current scene(s) (default: Single) — one of: Single, Additive
- **Result:** `sceneName`, `scenePath`, `loadMode`, `isLoaded`, `previousScene`, `activeSceneCount`, `summary`

### `manage_build_settings`
Manage the build scene list (EditorBuildSettings.scenes) — list/add/remove/move/set_enabled/clear. A common legacy need with no prior command. action=list returns {scenes:[{index,path,enabled,exists}], count, enabledCount} (exists flags dangling paths); add requires an existing .unity scenePath within the project (optional enabled/index); remove/move/set_enabled take scenePath OR index.

- **Params:**
  - `action` (string) — Operation (default list). — one of: list, add, remove, move, set_enabled, clear
  - `scenePath` (string) — Scene .unity path (add; or remove/move/set_enabled by path).
  - `index` (number) — Build-list index (add insert position; or remove/move/set_enabled by index).
  - `enabled` (boolean) — add/set_enabled: whether the scene is enabled in the build (default true on add).
  - `toIndex` (number) — move: destination index (clamped).
- **Result:** `scenes`, `count`, `enabledCount`, `message`

### `save_scene`
Save the current scene in Unity

- **Params:**
  - `scenePath` (string) — Path where to save the scene. If not provided, saves to current scene path. Required if saveAs is true.
  - `saveAs` (boolean) — Whether to save as a new scene (creates a copy). Default: false
- **Result:** `sceneName`, `scenePath`, `originalPath`, `saved`, `isDirty`, `summary`

## Screenshot (2)

### `analyze_screenshot`
Analyze Unity screenshots for content, UI elements, colors, and more

- **Params:**
  - `imagePath` (string) — Path to the screenshot file to analyze (must be within Assets folder)
  - `base64Data` (string) — Base64 encoded image data (alternative to imagePath)
  - `analysisType` (string) — Type of analysis: basic (colors, dimensions), ui (UI element detection), content (scene content), full (all) — one of: basic, ui, content, full
  - `prompt` (string) — Optional prompt for AI-based analysis (e.g., "Find all buttons in the UI")
- **Result:** —

### `capture_screenshot`
Capture screenshots from Unity Editor (Game View, Scene View, or specific windows)

- **Params:**
  - `outputPath` (string) — Path to save the screenshot (e.g., "Assets/Screenshots/capture.png"). If not provided, auto-generates with timestamp
  - `captureMode` (string) — What to capture: game (Game View), scene (Scene View), window (a specific editor window), or camera (render an arbitrary world camera — G5) — one of: game, scene, window, camera
  - `width` (number) — Custom width for the screenshot (0 = use current view size)
  - `height` (number) — Custom height for the screenshot (0 = use current view size)
  - `includeUI` (boolean) — Include UI elements in the screenshot (Game View only)
  - `windowName` (string) — Name of the window to capture (required when captureMode is "window")
  - `encodeAsBase64` (boolean) — Return the capture as viewable MCP image content (default true — the agent SEES the render). false returns only the saved path.
  - `cameraName` (string) — captureMode camera: render the camera on the GameObject with this name.
  - `cameraPath` (string) — captureMode camera: render the camera at this hierarchy path.
  - `cameraInstanceId` (number) — captureMode camera: render the camera with this instanceId. If none of cameraName/Path/InstanceId is given, Camera.main is used.
- **Result:** varies (one of several shapes)

## Scripting (6)

### `create_script`
Create a new C# script in Unity project

- **Params:**
  - `scriptName` (string, required) — Name of the script (without .cs extension)
  - `scriptType` (string) — Type of script to create — one of: MonoBehaviour, ScriptableObject, Editor, StaticClass, Interface
  - `path` (string) — Directory path where script will be created
  - `namespace` (string) — Namespace for the script
- **Result:** —

### `delete_script` — _⚠️ destructive (confirm-gated)_
Delete a C# script from Unity project. Requires confirm:true (H3).

- **Params:**
  - `scriptPath` (string) — Full path to the script file (e.g., Assets/Scripts/PlayerController.cs)
  - `scriptName` (string) — Name of the script to search for (alternative to scriptPath)
  - `searchPath` (string) — Directory to search in when using scriptName
  - `createBackup` (boolean) — Whether to create a backup before deleting
  - `force` (boolean) — When multiple scripts share the name: delete all matches (true) vs refuse (false). NOT related to the H3 confirm gate — confirm:true is always required to proceed.
- **Result:** varies (one of several shapes)

### `list_scripts`
List C# scripts in Unity project

- **Params:**
  - `searchPath` (string) — Directory to search for scripts
  - `pattern` (string) — Pattern to match script names (supports wildcards like *Controller*)
  - `scriptType` (string) — Filter by script type — one of: MonoBehaviour, ScriptableObject, Editor, StaticClass, Interface
  - `sortBy` (string) — Sort results by field — one of: name, path, size, lastModified, type
  - `sortOrder` (string) — Sort order (ascending or descending) — one of: asc, desc
  - `includeMetadata` (boolean) — Include file metadata (size, modification date, etc.)
  - `maxResults` (number) — Maximum number of results to return
- **Result:** `scripts`, `totalCount`, `message`

### `read_script`
Read the contents of a C# script file

- **Params:**
  - `scriptPath` (string) — Full path to the script file (e.g., Assets/Scripts/PlayerController.cs)
  - `scriptName` (string) — Name of the script to search for (alternative to scriptPath)
  - `searchPath` (string) — Directory to search in when using scriptName
  - `includeMetadata` (boolean) — Whether to include file metadata (line count, size, etc.)
- **Result:** `scriptContent`, `scriptPath`, `lastModified`, `lineCount`, `fileSize`, `encoding`

### `update_script` — _⚠️ destructive (confirm-gated)_
Update an existing C# script in Unity project. Destructive: replace mode overwrites the whole file (no Undo; createBackup defaults false). Requires confirm:true (H3).

- **Params:**
  - `scriptPath` (string) — Full path to the script file (e.g., Assets/Scripts/PlayerController.cs)
  - `scriptName` (string) — Name of the script to search for (alternative to scriptPath)
  - `searchPath` (string) — Directory to search in when using scriptName
  - `scriptContent` (string, required) — New content for the script
  - `updateMode` (string) — How to update the script content — one of: replace, append, prepend
  - `createBackup` (boolean) — Whether to create a backup before updating
- **Result:** `scriptPath`, `message`, `backupPath`

### `validate_script`
Validate a C# script for syntax and Unity compatibility

- **Params:**
  - `scriptPath` (string) — Full path to the script file (e.g., Assets/Scripts/PlayerController.cs)
  - `scriptContent` (string) — Script content to validate directly
  - `scriptName` (string) — Name of the script to search for (alternative to scriptPath)
  - `searchPath` (string) — Directory to search in when using scriptName
  - `checkSyntax` (boolean) — Check for syntax errors
  - `checkUnityCompatibility` (boolean) — Check for Unity API compatibility
  - `suggestImprovements` (boolean) — Provide code improvement suggestions
- **Result:** `isValid`, `errors`, `warnings`, `suggestions`, `scriptPath`, `message`

## Serialization (4)

### `inspect_serialized_object`
Discover a target's serialized property tree (propertyPath, SerializedPropertyType, current values, array sizes, [SerializeReference] typenames) via SerializedObject — never reflection, so private [SerializeField] fields appear. Provide `target` (single) or `match` (selector); optional component/componentIndex, pathPrefix (subtree scope), depth, includeValues, maxObjects. Read the result's values to drive set_serialized_properties' mandatory `expected`.

- **Params:**
  - `target` (object) — Single target: one of {instanceId} | {assetPath} | {guid} | {scenePath}.
  - `match` (object) — Selector: one of {prefab} | {componentType} | {tag} | {selection:true} | {scenePaths:[...]}.
  - `component` (string) — For a GameObject target/match: which component's SerializedObject (omitted = all components).
  - `componentIndex` (number) — Disambiguate multiple components of the same type (default 0).
  - `pathPrefix` (string) — Scope to a subtree (a propertyPath).
  - `depth` (number) — Max nesting depth (default 3).
  - `includeValues` (boolean) — Include current values (default true; false = paths+types only).
  - `maxObjects` (number) — Cap a selector's matched objects (default 50, ceiling 500).
- **Result:** `target`, `object`, `count`, `truncated`, `objects`

### `modify_serialized_array` — _⚠️ destructive (confirm-gated)_
Structurally mutate array/list SerializedProperties (resize/insert/remove/move/clear) with a size compare-and-swap. ops:[{target, component?, arrayPath, op, expectedSize, index?, count?, toIndex?, value?}]. Each op applies iff the live arraySize == expectedSize (STALE_SIZE else); expectedSize is mandatory unless force. Out-of-range index -> INDEX_OUT_OF_RANGE; non-array -> NOT_AN_ARRAY; up-front validated so allOrNothing is atomic. insert optionally sets the new leaf element's value. One Undo group; dirty-only (call save_assets). dryRun/allOrNothing/withoutUndo as set_serialized_properties.

- **Params:**
  - `ops` (array, required) — [{target, component?, componentIndex?, arrayPath, op: resize|insert|remove|move|clear, expectedSize, index?, count?, toIndex?, value?}]
  - `force` (boolean) — Skip the expectedSize guard (blind structural mutation).
  - `dryRun` (boolean) — Report planned ops without mutating.
  - `allOrNothing` (boolean) — Any failed op aborts the whole batch.
  - `withoutUndo` (boolean)
  - `undoLabel` (string)
- **Result:** `applied`, `changed`, `skipped`

### `save_assets`
Persist all dirty assets to disk (AssetDatabase.SaveAssets). set_serialized_properties writes are dirty-only — call this to save. Never auto-called per write.

- **Params:**
  - _none_
- **Result:** `saved`

### `set_serialized_properties` — _⚠️ destructive (confirm-gated)_
Safely write serialized properties via SerializedObject (private [SerializeField] included), Inspector-correct (one Undo group, ApplyModifiedProperties, prefab-instance overrides, dirty). READ-BEFORE-WRITE is mandatory. Mode 1 — edits:[{target,component?,set:{path:{value,expected}}}]: each writes iff live==expected (STALE on mismatch); `expected` is required unless `force`. Mode 2 — match+set:{path:value}: a no-token call PREVIEWS (returns the matched set + current values + a token, no mutation); pass the `token` back to commit (STALE_MATCH if state drifted); `force` skips the token. dryRun reports without writing; allOrNothing makes the batch atomic; withoutUndo/undoLabel control undo. Writes are dirty-only — call save_assets to persist.

- **Params:**
  - `edits` (array) — Mode 1: explicit-target compare-and-swap edits [{target, component?, componentIndex?, set:{propertyPath:{value, expected}}}].
  - `match` (object) — Mode 2 selector (see inspect_serialized_object).
  - `set` (object) — Mode 2: propertyPath -> value, applied to every match.
  - `component` (string)
  - `componentIndex` (number)
  - `token` (string) — Mode 2 commit token returned by the preview call.
  - `force` (boolean) — Opt into a blind write — skips `expected` (Mode 1) / `token` (Mode 2). The one reckless door; default false.
  - `dryRun` (boolean) — Report planned changes + precondition results without mutating.
  - `allOrNothing` (boolean) — Any precondition failure aborts the whole batch.
  - `withoutUndo` (boolean) — ApplyModifiedPropertiesWithoutUndo (no undo entry).
  - `undoLabel` (string) — The Undo group name.
- **Result:** `applied`, `changed`, `skipped`, `objects`, `token`, `forced`

## System (7)

### `clear_audit_log` — _⚠️ destructive (confirm-gated)_
Clear the local mutation audit log (H5). Destructive (erases the trail); requires confirm:true (H3).

- **Params:**
  - _none_
- **Result:** `cleared`

### `get_audit_log`
Read the local mutation audit log (H5) — recent dispatched commands as {t, type, target, ok}. A complete command trail; filter `type` (case-insensitive substring, e.g. 'delete'/'set'/'create') for mutations, `since` (ISO-8601 UTC) for a time window; `max` caps the count (default 100, ceiling 1000). Entries are chronological.

- **Params:**
  - `max` (number) — Max entries to return (default 100, ceiling 1000).
  - `type` (string) — Case-insensitive substring filter on the command type.
  - `since` (string) — ISO-8601 UTC; only entries at or after this time.
- **Result:** `count`, `entries`

### `get_editor_info`
Get Unity editor environment info: Unity version, platform, project path, active build target, product/company name, and quick play/compile state.

- **Params:**
  - _none_
- **Result:** `unityVersion`, `platform`, `systemLanguage`, `projectPath`, `dataPath`, `productName`, `companyName`, `version`, `activeBuildTarget`, `isPlaying`, `isCompiling`, `applicationPath`

### `ping`
Test connection to Unity Editor

- **Params:**
  - `message` (string) — Optional message to echo back
- **Result:** `message`, `echo`, `timestamp`

### `quit_editor` — _⚠️ destructive (confirm-gated)_
Quit the Unity editor (intended for CI/automation). The editor exits after the response is sent, so the connection will drop. Unsaved changes are NOT saved. Requires confirm:true (H3).

- **Params:**
  - _none_
- **Result:** `message`

### `read_logs`
Read Unity console logs

- **Params:**
  - `count` (number) — Number of logs to retrieve (1-1000, default: 100)
  - `logType` (string) — Filter by log type (Log, Warning, Error, Assert, Exception) — one of: Log, Warning, Error, Assert, Exception
- **Result:** `logs`, `count`, `totalCaptured`

### `refresh_assets`
Trigger Unity to refresh assets and check for compilation

- **Params:**
  - _none_
- **Result:** `message`, `isCompiling`, `timestamp`

## Test (4)

### `cancel_tests`
Cancel currently running tests in Unity

- **Params:**
  - _none_
- **Result:** `message`, `wasCancelled`, `timestamp`

### `get_test_results`
Get results from the last test run in Unity

- **Params:**
  - `includeDetails` (boolean) — Include detailed test output, stack traces, and assertion results
  - `filterStatus` (string) — Filter results by status (Passed, Failed, Skipped, Inconclusive) — one of: Passed, Failed, Skipped, Inconclusive
- **Result:** `results`, `summary`, `isRunning`, `totalTests`, `message`, `hasResults`

### `list_tests`
List all available tests in the Unity project

- **Params:**
  - `testMode` (string) — Test mode to list (EditMode, PlayMode, or EditAndPlayMode) — one of: EditMode, PlayMode, EditAndPlayMode
  - `filter` (string) — Filter pattern to match test names
  - `includeCategories` (array) — Include tests with these categories
  - `excludeCategories` (array) — Exclude tests with these categories
- **Result:** `tests`, `totalCount`, `testMode`, `message`

### `run_tests`
Run tests in Unity Test Runner

- **Params:**
  - `testMode` (string) — Test mode to run (EditMode, PlayMode, or EditAndPlayMode) — one of: EditMode, PlayMode, EditAndPlayMode
  - `testNames` (array) — Specific test names to run (runs all if not specified)
  - `runAll` (boolean) — Run all tests in the specified mode
  - `includeCategories` (array) — Include tests with these categories
  - `excludeCategories` (array) — Exclude tests with these categories
- **Result:** `message`, `testMode`, `testCount`, `runAll`, `timestamp`

## Ui (5)

### `click_ui_element`
Simulate clicking on UI elements

- **Params:**
  - `elementPath` (string, required) — Full hierarchy path to the UI element
  - `clickType` (string) — Type of click (left, right, middle) — one of: left, right, middle
  - `holdDuration` (number) — Duration to hold click in milliseconds
  - `position` (object) — Specific position within element to click
- **Result:** `success`, `elementPath`, `clickType`, `message`

### `find_ui_elements`
Find UI elements in Unity scene by type, tag, or name

- **Params:**
  - `elementType` (string) — Filter by UI component type (Button, Toggle, Slider, etc.)
  - `tagFilter` (string) — Filter by GameObject tag
  - `namePattern` (string) — Search by name pattern/regex
  - `includeInactive` (boolean) — Include inactive UI elements
  - `canvasFilter` (string) — Filter by parent Canvas name
- **Result:** `elements`, `count`

### `get_ui_element_state`
Get detailed state information about UI elements

- **Params:**
  - `elementPath` (string, required) — Full hierarchy path to the UI element
  - `includeChildren` (boolean) — Include child element states
  - `includeInteractableInfo` (boolean) — Include interaction capabilities
- **Result:** `path`, `name`, `isActive`, `tag`, `layer`, `components`, `color`, `raycastTarget`, `isInteractable`, `value`, `placeholder`, `isOn`, `minValue`, `maxValue`, `options`, `text`, `fontSize`, `font`, `children`

### `set_ui_element_value`
Set values for UI input elements

- **Params:**
  - `elementPath` (string, required) — Full hierarchy path to the UI element
  - `value` (object, required) — New value to set (type depends on element type)
  - `triggerEvents` (boolean) — Whether to trigger associated events
- **Result:** `success`, `elementPath`, `newValue`, `message`

### `simulate_ui_input`
Simulate complex UI interactions and input sequences

- **Params:**
  - `elementPath` (string) — Target UI element path (for simple input)
  - `inputType` (string) — Type of input to simulate (for simple input) — one of: click, doubleclick, rightclick, hover, focus, type
  - `inputData` (string) — Data for input (e.g., text to type)
  - `inputSequence` (array) — Array of input actions to perform (for complex input)
  - `waitBetween` (number) — Delay between actions in milliseconds
  - `validateState` (boolean) — Validate UI state between actions
- **Result:** `success`, `results`, `totalActions`

