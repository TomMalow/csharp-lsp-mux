# ADR-0001: Lazy solution discovery

## Status

Accepted

## Context

A mono-repo can contain dozens of `.sln` files. Scanning the entire tree at startup would add latency before the first LSP response and discover solutions that may never be needed in the current session.

## Decision

Solutions are discovered lazily — only when a `textDocument/*` request arrives for a file not yet mapped to a solution. The SolutionRouter resolves and memoizes the result at that point.

## Consequences

- **Cold first request**: the first request for a given file pays the cost of a directory walk.
- **No wasted work**: solutions never opened by the user are never started.
- **Simpler startup**: `initialize` response is immediate; no filesystem enumeration.
