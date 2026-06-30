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

## Installation

```bash
# Build and install as a global tool
dotnet tool install --global --add-source ./src/CsharpLspMux

# Verify
csharp-lsp-mux --version
```

## Claude Code Plugin Setup

In your consumer repo (e.g. your mono-repo), create a plugin:

```
.claude/plugins/csharp-lsp-mux/
├── .claude-plugin/
│   └── plugin.json
└── .lsp.json
```

`plugin.json`:
```json
{
  "name": "csharp-lsp-mux"
}
```

`.lsp.json`:
```json
{
  "csharp": {
    "command": "csharp-lsp-mux",
    "extensionToLanguage": {
      ".cs": "csharp"
    },
    "env": {
      "LSP_ROUTER_MAX_SERVERS": "10"
    }
  }
}
```

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
dotnet build
dotnet test
dotnet pack -c Release
```

## License

MIT
