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
| **Correlation ID** | The JSON-RPC `id` field; rewritten at the proxy boundary to avoid collisions between child servers | — |
| **Content-Length framing** | The LSP wire format: `Content-Length: N\r\n\r\n{json}` | Not HTTP — no headers beyond Content-Length |
| **Child server** | A single `roslyn-language-server` process owned by the pool, bound to one solution | — |
| **Repo root** | The git working tree root — upper bound for ancestor walks | — |
