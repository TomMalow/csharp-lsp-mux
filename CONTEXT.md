# Context: csharp-lsp-mux

## What this is

An LSP multiplexer that sits between Claude Code and multiple Roslyn language server instances, routing requests to the correct server based on which .NET solution owns the file.

## Glossary

| Term | Definition | Not this |
|---|---|---|
| **Router** / **Mux** | The `csharp-lsp-mux` process itself — reads JSON-RPC from stdin, dispatches to child servers, writes responses to stdout | Not an HTTP/network router |
| **Solution** | A `.sln` or `.slnx` file that groups .NET projects | Not "solution" in the generic problem-solving sense |
| **Ancestor walk** | Routing strategy: walk directories upward from the file until a `.sln`/`.slnx` is found — the common case where the solution is a direct ancestor | — |
| **Sibling scan** | Routing fallback: when ancestor walk fails, find the nearest `src/` ancestor and scan its subtree for solutions — handles files in sibling-project layouts | — |
| **Server pool** | Bounded set of `roslyn-language-server` child processes keyed by solution path, with LRU eviction | — |
| **Server session** | The server pool's per-entry unit: one child server plus the session state the mux keeps for it (opened URIs, in-flight request ids); lives and dies with the entry, so eviction cleans it up structurally | Not a **mux session** (one `csharp-lsp-mux` process — see ADR-0006) |
| **Correlation ID** | The JSON-RPC `id` field; rewritten at the proxy boundary to avoid collisions between child servers | — |
| **Content-Length framing** | The LSP wire format: `Content-Length: N\r\n\r\n{json}` | Not HTTP — no headers beyond Content-Length |
| **Child server** | A single `roslyn-language-server` process owned by the pool, bound to one solution | — |
| **Repo root** | The git working tree root — upper bound for ancestor walks | — |

## LSP message dispatch

Claude code is currently the main target of the LSP mux. This section documents the LSP methods it sends and how the mux handles each. Three handling modes:

- **Absorb** — mux responds directly; Roslyn never sees it
- **Route** — forward to the single server that owns the file's solution
- **Broadcast** — fan out to all active servers; merge responses

### Lifecycle

| Method | Mode | What Claude Code uses it for | Mux behaviour |
|---|---|---|---|
| `initialize` | Absorb | Opens the LSP session; negotiates capabilities | Mux synthesises a capability response; no child servers started yet (lazy) |
| `initialized` | Absorb | Confirms `initialize` was received | Dropped — each child server gets its own `initialized` when it starts |
| `shutdown` | Absorb | Asks the server to stop | Drains all child servers (`ShutdownAsync` + `DisposeAsync`) then responds |
| `exit` | Absorb | Signals the process to exit | Exits the read loop; process terminates |

### Text document

All `textDocument/*` methods share one routing rule: extract `params.textDocument.uri` → `SolutionRouter.Route()` → forward to the owning server (starting it if not yet active).

| Method | What Claude Code uses it for | Notes |
|---|---|---|
| `textDocument/didOpen` | Sent before the first request on a file | Tracked on the file's server session; replayed automatically before forwarding requests to files that were opened in a prior session |
| `textDocument/didChange` | After each edit Claude makes | Forwarded verbatim (incremental sync) |
| `textDocument/didClose` | File closed | Removed from the server session's opened-URI set |
| `textDocument/hover` | Type info / docs at cursor | Forwarded; response relayed back |
| `textDocument/definition` | Go to definition | Forwarded; response relayed back |
| `textDocument/references` | Find all references | Forwarded; response relayed back |
| `textDocument/documentSymbol` | Symbols in the open file | Forwarded; response relayed back |
| `textDocument/implementation` | Find implementations of interface/abstract | Forwarded; response relayed back |
| `textDocument/prepareCallHierarchy` | Entry point for call hierarchy | Forwarded; response relayed back |

### Workspace

| Method | Mode | What Claude Code uses it for | Mux behaviour |
|---|---|---|---|
| `workspace/symbol` | Broadcast | Global symbol search by name across the repo | Fan out to all active servers concurrently; merge result arrays; partial failure returns remaining results |
| `workspace/didChangeWatchedFiles` | Absorb | File system changes to `.sln`/`.slnx`/`.csproj` | Calls `SolutionRouter.NotifyFileChanged` to invalidate routing cache; not forwarded |
| `workspace/didChangeConfiguration` | Broadcast (not yet) | Settings changes | Currently dropped — should be forwarded to all active servers ([#42](https://github.com/TomMalow/csharp-lsp-mux/issues/42)) |

### Cancellation

| Method | Mode | What Claude Code uses it for | Mux behaviour |
|---|---|---|---|
| `$/cancelRequest` | Route | Cancel an in-flight request by id | Forwarded to the active server session that registered that correlation id |

### Known gaps

| Method | Direction | Why it matters | Issue |
|---|---|---|---|
| `workspace/configuration` | Server → client | Roslyn requests project config during startup; mux relays it to Claude Code unanswered, which may stall workspace indexing | [#41](https://github.com/TomMalow/csharp-lsp-mux/issues/41) |
| Child `initialize` capabilities | Client → Roslyn | Mux sends `"capabilities":{}` to child servers; Roslyn may skip building symbol indexes | [#40](https://github.com/TomMalow/csharp-lsp-mux/issues/40) |
