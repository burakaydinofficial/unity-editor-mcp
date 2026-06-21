# Visual Capture — MCP Image Content + Specific-Camera Render (0.15.0) Design

> Status: design (autonomous). Requirement **G5** (Capture: game view, scene view, **specific camera**). Makes
> captured renders something the agent can actually **see** (real MCP image content), and adds a world-camera
> render mode. Answers the maintainer's "do we support renders / does MCP support images" thread with action.

## 1. The gap

`capture_screenshot` already renders Game View / Scene View / window, writes a PNG, and can return `base64Data`.
But the Node server's `toMcpResponse` (`mcp-server/src/core/server.js`) text-wraps **every** result
(`JSON.stringify` → a single `type:"text"` block), so the base64 is buried in JSON and the model **cannot see
it**. The MCP protocol *does* support `type:"image"` blocks (base64 + `mimeType`); we just never emit them.

## 2. Image-content convention (Node)

A general, reusable convention so any tool can return a viewable image:
- A handler result whose `result` object carries **`image: { mimeType, data }`** (`data` = base64, no data-URI
  prefix) signals viewable image content.
- `toMcpResponse` emits an MCP **`type:"image"`** block (`{ type:"image", data, mimeType }`) **plus** a
  `type:"text"` block carrying the rest of the result with the bulky base64 **replaced by a size marker**
  (`image: { mimeType, bytes }`) — so the model both sees the picture and gets the metadata, without a giant
  duplicated base64 string. Error results are unchanged (text).
- `call_unity_tool` passes the editor payload through as the result, so an editor capture's `image` surfaces
  here unchanged (the exact nesting is pinned by a Node unit test).

## 3. Editor: surface the image + add a camera mode

- **Image field:** when a capture is requested as image content (default on for the capture tool, or an
  `asImage` flag), the editor result includes `image: { mimeType: "image/png", data: <base64> }` (alongside the
  existing `path`/dimensions). Reuses the existing `EncodeToPNG` + `Convert.ToBase64String`.
- **`camera` capture mode (G5 "specific camera"):** render an arbitrary world camera (resolved by
  `cameraName` / `cameraPath` / `cameraInstanceId`, else `Camera.main`) to a `RenderTexture` →
  `ReadPixels` → PNG, exactly like the existing Scene-View path. Structured errors: `CAMERA_NOT_FOUND`,
  and the existing headless `NO_VIEW`-style failures stay. Reports the camera's transform like Scene mode does.

## 4. Floor-safety

`Camera` enumeration (`Camera.allCameras` / `GameObject.Find`), `RenderTexture`, `Texture2D.ReadPixels`,
`EncodeToPNG`, `Convert.ToBase64String` — all floor-safe (2020.3). No version-divergent API; nothing for
COMPATIBILITY.md. Node: pure JS, only the `@modelcontextprotocol/sdk` content shape (image block is standard MCP).

## 5. Catalog & integration

`capture_screenshot` gains `captureMode:"camera"` + `cameraName`/`cameraPath`/`cameraInstanceId` params (and the
`asImage` toggle, default true). No new command. Drift stays green (params-only). The editor handler change is
dogfoodable (base64 present in the result); the Node `toMcpResponse` change is **not** live-dogfoodable (the live
bridge runs a pre-session Node server) — covered by Node unit tests instead.

## 6. Testing

- **Node unit (`server` / `toMcpResponse`):** a success result with `result.image = { mimeType, data }` →
  content has a `type:"image"` block with the data + mimeType, AND a `type:"text"` block whose JSON has no raw
  base64 (a `bytes` marker instead); a normal result (no image) → unchanged single text block; an error result
  → unchanged. Pin the `call_unity_tool` passthrough nesting so the `image` field lands where the server looks.
- **Editor (NUnit/dogfood):** `camera` mode renders `Camera.main` (the scene's Main Camera) → result has
  `image.data` non-empty + the camera transform; `CAMERA_NOT_FOUND` for a bogus camera name. Dogfood on the
  floor: `capture_screenshot captureMode:"camera"` → base64 present.
- **Gates:** drift, compat-lint, Core dotnet, EditMode, `npm run test:ci`.
