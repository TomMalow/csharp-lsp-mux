# ADR-0005: Single workspace folder

## Status

Accepted

## Context

LSP supports multi-root workspaces (`workspace/workspaceFolders`). Claude Code typically opens one folder at a time, but multi-root is part of the spec.

## Decision

Assume a single workspace folder. Do not negotiate `workspace/workspaceFolders` capabilities or handle `workspace/didChangeWorkspaceFolders` notifications.

## Consequences

- **Simpler routing**: one repo root as the upper bound for ancestor walks.
- **No multi-root**: users who open multiple folders in one session won't get correct routing across roots.
- **Easy to relax later**: multi-root support can be added by tracking multiple root URIs without changing the core routing algorithm.
