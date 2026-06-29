# CLAUDE.md — csharp-lsp-mux

## What this is

A dotnet global tool (`csharp-lsp-mux`) that multiplexes LSP JSON-RPC between Claude Code and multiple `roslyn-language-server` instances — one per solution in a mono-repo.

## Repo layout

```
LspRouter.sln
NuGet.Config                        ← nuget.org only
Directory.Build.props               ← RestorePackagesWithLockFile=true, NuGetAudit=true
src/LspRouter/
    LspRouter.csproj                ← net10.0, PackAsTool=true, ToolCommandName=csharp-lsp-mux
    Program.cs                      ← entry point: stdin/stdout JSON-RPC loop
tests/LspRouter.Tests/
    LspRouter.Tests.csproj          ← net10.0, xUnit v3
```

## Modules

### Program / Entry Point
Reads LSP JSON-RPC frames from stdin, dispatches to handlers, writes responses to stdout. Manages full lifecycle: accept `initialize`, spin up child servers on demand, forward traffic, respond to `shutdown`/`exit`.

### JsonRpc Transport
Content-Length framed reading/writing over streams. Parses JSON-RPC message envelopes (request, response, notification). Thin — no LSP semantics. Responsible for framing, correlation IDs, raw JSON forwarding.

### SolutionRouter
Pure function: given absolute file path + repo root → absolute path of owning `.sln`/`.slnx`.

**Algorithm:**
1. **Ancestor walk**: walk directories upward from file — if `.sln`/`.slnx` found → return it
2. **Sibling scan** (fallback): if ancestor walk exits repo root without finding one → find nearest `src/` ancestor, scan subtree for all `.sln`/`.slnx`, return the one with most `.csproj` references
3. Memoize results keyed by file path
4. Invalidate on `workspace/didChangeWatchedFiles` for `.sln`/`.slnx`/`.csproj` changes

### ServerPool
Bounded set of `roslyn-language-server` child processes. Keyed by solution path. Cap: `LSP_ROUTER_MAX_SERVERS` (default 10). LRU eviction via `shutdown` → `exit` → kill.

Each server initialized with:
- `rootUri` = solution's directory
- `initializationOptions.solutionPath` = absolute `.sln`/`.slnx` path

## Request dispatch

| Category | Handling |
|---|---|
| `initialize` / `initialized` | Router handles; responds with synthesized capabilities; does not forward |
| `textDocument/*` | Extract URI → route to owning solution's server; start server if not active |
| `workspace/symbol` | Broadcast to all active servers; merge result arrays |
| `$/cancelRequest` | Forward to server owning the original request ID |
| `shutdown` / `exit` | Drain all active servers then exit |

## Build & test

```bash
dotnet build
dotnet test
dotnet pack -c Release

# Install locally
dotnet tool install --global --add-source ./src/LspRouter
```

## Test conventions

- Only SolutionRouter has unit tests (pure function, highest value)
- Test observable contract (inputs/outputs), not internal structures
- xUnit v3, no mocking frameworks
- Tests should break only when routing decisions change

## Key design decisions

- **No startup scan** — solutions discovered lazily on first file access
- **roslyn-language-server only** — no OmniSharp/csharp-ls
- **Transport-transparent** — forwards raw JSON unchanged except correlation ID rewriting
- **Cross-platform** — uses `Path.Combine`/`Path.GetFullPath` and platform-appropriate case sensitivity
- **Single workspace folder** — no multi-root workspace negotiation

## Agent skills

### Issue tracker

Issues live in GitHub Issues (`TomMalow/csharp-lsp-mux`); external PRs are not a triage surface. See `docs/agents/issue-tracker.md`.

### Triage labels

Default label vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context repo — `CONTEXT.md` + `docs/adr/` at root. See `docs/agents/domain.md`.
