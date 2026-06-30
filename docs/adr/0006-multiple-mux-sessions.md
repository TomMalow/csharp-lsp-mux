# ADR-0006: Multiple csharp-lsp-mux sessions and duplicate roslyn-language-server processes

## Status

Accepted

## Context

`csharp-lsp-mux` is a single-process proxy — one stdin/stdout pipe per session. If a user runs multiple Claude Code sessions against the same repo simultaneously (or multiple parallel agents each launching their own mux), each mux process independently starts its own `roslyn-language-server` for each solution it accesses. This raises the question of whether duplicated Roslyn instances cause correctness or stability problems.

## Investigation

**Does roslyn-language-server tolerate multiple instances against the same solution?**

Yes. `roslyn-language-server` holds no exclusive file locks on `.cs` source files or `.sln`/`.slnx` files; it builds an in-memory Roslyn workspace from the solution graph at startup and keeps it warm. This is exactly the model used when opening the same solution in two separate VS Code windows — two independent Roslyn servers run side-by-side without corrupting each other. The `.vs/` server-data directory can see concurrent writes, but Roslyn's server data is per-process and tolerates concurrent readers/writers at the file level (it is advisory, not exclusive-lock-based).

**Is the cost acceptable?**

The main cost is memory: each Roslyn instance for a large solution can use 500 MB–2 GB. Two concurrent sessions against the same solution double that. Performance is symmetric — each session gets its own warm index with no cross-session interference, which is often preferable to contending for a shared process.

**Is a singleton-per-solution strategy feasible?**

Technically yes (named pipe or Unix socket handshake, lock-file leader election), but the complexity is disproportionate to the benefit:

- Requires cross-process IPC with LSP framing, multiplexing responses by originating session
- Must handle coordinator crash, stale lock files, and OS pipe lifecycle across three process tiers
- Adds a new failure mode without a clear user-observable win (the status-quo "two windows" model is universally understood)

## Decision

Accept the duplicate-process behavior as-is. No coordination mechanism will be added. Document it as a known cost in the README.

## Consequences

- **Memory**: two concurrent sessions against the same solution consume roughly double the RAM a single session would.
- **No correctness risk**: each Roslyn instance is independent; no shared mutable state between processes.
- **No action required**: the behavior is already the natural outcome of the current architecture and consistent with how VS Code handles multiple windows on the same solution.
- **Documentation only**: update the README unsupported-cases section with a note about concurrent sessions.
