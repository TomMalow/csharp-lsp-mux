# csharp-lsp-mux

A transparent LSP multiplexer for C# mono-repos. Routes LSP requests from Claude Code to the correct `roslyn-language-server` instance based on which solution owns the file being edited.

## Problem

In a mono-repo with many `.sln` files, a single language server binds to whichever solution it finds first — go-to-definition, hover, and diagnostics all return results from the wrong context.

## Solution

`csharp-lsp-mux` sits between Claude Code and a pool of Roslyn servers (one per solution). It inspects each request's file URI, maps it to the owning solution, and forwards to the right server. Servers start lazily on first access; the pool is bounded with LRU eviction.

## Features

- **Lazy server startup** — no upfront scan; servers spin up on first file access
- **Ancestor walk** — walks parent directories from file toward repo root for `.sln`/`.slnx`
- **Sibling scan** — fallback for sibling-project layouts (scans `src/` subtree, picks solution with most `.csproj` references)
- **Bounded pool** — configurable cap (`LSP_ROUTER_MAX_SERVERS`, default 10) with LRU eviction
- **Request queuing** — requests arriving before a server finishes initializing are queued, not dropped
- **Cancel forwarding** — `$/cancelRequest` routed to the correct server
- **Workspace symbol merge** — `workspace/symbol` broadcasts to all active servers
- **Cache invalidation** — routing cache refreshes on `.sln`/`.slnx`/`.csproj` changes
- **Clean shutdown** — all child servers drained on proxy exit

## Supported operations (Claude Code)

- Hover
- Go to definition
- Find references
- Go to implementation
- Document symbols
- Call hierarchy (incoming/outgoing calls)
- Workspace symbol search (broadcast + merge across active solutions)

See [CONTEXT.md](./CONTEXT.md) for the authoritative, method-level dispatch table.

## Prerequisites

`csharp-lsp-mux` launches `roslyn-language-server` child processes, so that tool
**must be installed and on your `PATH`**. It is not bundled with this package.

```bash
dotnet tool install --global roslyn-language-server
```

If it is missing, `csharp-lsp-mux` exits with a message telling you to install it.

## Installation

### From nuget.org (once published)

```bash
dotnet tool install --global CsharpLspMux
csharp-lsp-mux --version
```

### From source

```bash
# Pack then install as a global tool
dotnet pack src/CsharpLspMux -c Release
dotnet tool install --global --add-source ./src/CsharpLspMux/bin/Release CsharpLspMux

# To reinstall after rebuilding (same version):
dotnet tool uninstall --global CsharpLspMux
dotnet tool install --global --add-source ./src/CsharpLspMux/bin/Release CsharpLspMux

# Verify
csharp-lsp-mux --version
```

## Claude Code Plugin Setup

### Option A: Local marketplace (recommended)

Create a local marketplace so `csharp-lsp-mux` is available across all your C# projects.

**1. Create the marketplace directory:**

```
~/.claude/plugins/marketplaces/csharp-lsp-mux/
├── .claude-plugin/
│   └── marketplace.json
└── plugins/
    └── csharp-lsp-mux/
        └── .claude-plugin/
            └── plugin.json
```

**2. `marketplace.json`:**

```json
{
  "$schema": "https://anthropic.com/claude-code/marketplace.schema.json",
  "name": "csharp-lsp-mux-local",
  "description": "Local C# LSP multiplexer plugin",
  "owner": { "name": "local" },
  "plugins": [
    {
      "name": "csharp-lsp-mux",
      "description": "C# LSP multiplexer routing to per-solution Roslyn servers",
      "version": "1.0.0",
      "author": { "name": "local" },
      "category": "development",
      "source": "./plugins/csharp-lsp-mux",
      "lspServers": {
        "csharp": {
          "command": "csharp-lsp-mux",
          "extensionToLanguage": { ".cs": "csharp" },
          "env": { "LSP_ROUTER_MAX_SERVERS": "10" }
        }
      }
    }
  ]
}
```

**3. `plugins/csharp-lsp-mux/.claude-plugin/plugin.json`:**

```json
{
  "name": "csharp-lsp-mux",
  "description": "C# LSP multiplexer routing to per-solution Roslyn servers",
  "version": "1.0.0",
  "lspServers": {
    "csharp": {
      "command": "csharp-lsp-mux",
      "extensionToLanguage": { ".cs": "csharp" },
      "env": { "LSP_ROUTER_MAX_SERVERS": "10" }
    }
  }
}
```

**4. Register the marketplace** by adding an entry to `~/.claude/plugins/known_marketplaces.json`:

```json
{
  "csharp-lsp-mux-local": {
    "source": { "source": "github", "repo": "local/csharp-lsp-mux" },
    "installLocation": "/Users/you/.claude/plugins/marketplaces/csharp-lsp-mux",
    "lastUpdated": "2026-01-01T00:00:00.000Z"
  }
}
```

**5. Enable the plugin** in `~/.claude/settings.json`:

```json
{
  "enabledPlugins": {
    "csharp-lsp-mux@csharp-lsp-mux-local": true
  }
}
```

**6. Disable the official `csharp-lsp` plugin** (if enabled) to avoid `.cs` file conflicts:

```json
{
  "enabledPlugins": {
    "csharp-lsp@claude-plugins-official": false
  }
}
```

### Prerequisites

- `csharp-lsp-mux` installed as a dotnet global tool and on PATH
- `roslyn-language-server` on PATH (ships with C# Dev Kit or install via `dotnet tool install --global Microsoft.CodeAnalysis.LanguageServer`)
- The official `csharp-lsp` plugin disabled to avoid conflicts on `.cs` extension

## Configuration

| Environment Variable | Default | Description |
|---|---|---|
| `LSP_ROUTER_MAX_SERVERS` | `10` | Max concurrent Roslyn server instances |

Set environment variables in the `env` block of `.lsp.json`, or export them in your shell before launching Claude Code.

## Unsupported cases

- **Shared source files** — if a `.cs` file is included by multiple solutions via `<Compile Include=...>` or linked projects, `workspace/symbol` results from all active servers are merged by concatenation without deduplication. Duplicate symbols may appear in results.
- **Cross-solution go-to-definition** — a symbol referenced across solution boundaries routes to whichever solution owns the *requesting* file; the definition may live in a different server's index and will not be found.
- **Multi-root workspaces** — only a single `rootUri` per server is supported; VS Code multi-root workspace folders are not negotiated.
- **Concurrent sessions against the same solution** — each `csharp-lsp-mux` process starts its own `roslyn-language-server` independently. Multiple concurrent sessions (e.g. two Claude Code windows on the same repo) each pay the full memory cost; RAM roughly doubles per additional session. There is no correctness risk — Roslyn holds no exclusive file locks — but memory pressure increases linearly with session count. See [ADR-0006](docs/adr/0006-multiple-mux-sessions.md).

## Architecture

See [CLAUDE.md](./CLAUDE.md) for module map, routing algorithm, and development conventions.

## Development

```bash
dotnet tool restore   # installs roslyn-language-server (pinned in .config/dotnet-tools.json)
dotnet build
dotnet test
dotnet pack -c Release
```

## License

MIT
