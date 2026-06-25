# Contributing to Unity Editor MCP

Thank you for your interest in contributing to Unity Editor MCP! This document provides guidelines and instructions for contributing to the project.

## Compatibility policy — read this first

This fork's identity is being **floor-true for older Unity** (2019 → latest; tested floor 2019.4 LTS). Two rules
are non-negotiable:

- **Guards, not floors.** Every version-divergent Unity API goes behind `#if UNITY_X_Y_OR_NEWER` with **both
  branches maintained** — never raise the floor to dodge a divergence. Catalog every guard in
  [`COMPATIBILITY.md`](COMPATIBILITY.md).
- **Stay within the floor's language/runtime.** Unity-side C# is constrained to **C# 7.3 / netstandard 2.0**
  (the 2019.4 floor Mono — older than 2020.3 C# 8; the whole codebase compiles on 2019.4). No UI Toolkit editor
  APIs in core paths (IMGUI-safe). The Node server stays **pure JS, no native modules** (Node ≥ 18).

`node scripts/compat-lint.mjs` flags floor-divergent Unity APIs used outside a guard, and the
[floor-matrix CI](.github/workflows/floor-matrix.yml) compiles + runs the EditMode suite on 2019.4 / 2020.3 /
2021.3 / 2022.3 on every release. See [`CLAUDE.md`](CLAUDE.md) for the architecture and the rest of the compatibility notes.

## Getting Started

1. Fork the repository
2. Clone your fork: `git clone https://github.com/YOUR_USERNAME/unity-editor-mcp.git`
3. Create a new branch: `git checkout -b feature/your-feature-name`

## Development Setup

### Prerequisites

- Unity 2019.4 LTS or newer
- Node.js 18.0.0 or newer
- Git

### Setup Instructions

1. Install dependencies in the mcp-server directory:
   ```bash
   cd mcp-server
   npm install
   ```

2. Install the Unity package in your Unity project (see README.md)

3. Run tests:
   ```bash
   npm test
   ```

## Code Guidelines

### JavaScript (Node, ESM)

- Pure ES modules, no TypeScript and no native modules
- Follow the existing code style
- Add JSDoc comments for public functions
- Keep functions focused and single-purpose
- All logging goes to **stderr** (`console.error` / the logger) — stdout is reserved for the MCP JSON-RPC stream

### Unity C#

- Follow Unity's coding conventions
- Use meaningful variable and method names
- Add XML documentation for public methods
- Handle exceptions appropriately

### Commit Messages

- Use clear, descriptive commit messages
- Start with a verb in present tense (e.g., "Add", "Fix", "Update")
- Keep the first line under 50 characters
- Add detailed description if needed

Example:
```
Add GameObject search by component type

- Implement find_by_component tool
- Add support for exact type matching
- Include inactive object filtering
```

## Testing — the gate suite

Before submitting, the same checks CI runs should pass locally:

- **Node server:** `cd mcp-server && npm run test:ci`
- **Protocol contract (no drift):** `node protocol/scripts/check-drift.mjs`
- **Compatibility lint:** `node scripts/compat-lint.mjs`
- **Unity-independent Core:** `dotnet test dotnet/UnityEditorMCP.Core.Tests/UnityEditorMCP.Core.Tests.csproj`
- **Editor (NUnit EditMode):** run via the Unity Test Runner in an editor — the floor-matrix CI runs these on every floor.

Write tests for new features (Node unit/integration; an EditMode test per editor handler). Any command/tool
change must keep `check-drift.mjs` green and regenerate `docs/tools-reference.md`
(`node protocol/scripts/generate-tools-reference.mjs`).

## Pull Request Process

1. Update documentation if needed
2. Ensure all tests pass
3. **Adding or changing a command/tool?** Edit `protocol/catalog/commands.json` first (the contract is the source of truth), implement both halves, and make sure `node protocol/scripts/check-drift.mjs` passes — see [`protocol/README.md`](protocol/README.md) and [`CLAUDE.md`](CLAUDE.md). Update the README tool list too.
4. Submit PR with clear description
5. Address review feedback promptly

## Reporting Issues

- Use GitHub Issues for bug reports and feature requests
- Include Unity version and OS information
- Provide steps to reproduce for bugs
- Include relevant error messages and logs

## Code of Conduct

- Be respectful and inclusive
- Welcome newcomers and help them get started
- Focus on constructive feedback
- Collaborate openly and transparently

## Questions?

Feel free to open an issue for any questions about contributing!