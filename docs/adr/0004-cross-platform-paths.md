# ADR-0004: Cross-platform path handling

## Status

Accepted

## Context

Path handling differs between macOS/Linux (forward slashes, case-sensitive by default) and Windows (backslashes, case-insensitive). The tool needs to work on both.

## Decision

Support both macOS and Windows. Use `Path.Combine` / `Path.GetFullPath` for all path construction. Normalize URIs via `Uri` class. Compare paths case-insensitively on Windows, case-sensitively elsewhere.

## Consequences

- **Broader audience**: works on Windows, macOS, and Linux.
- **More careful path code**: must use framework APIs rather than string concatenation with `/`.
- **Testing surface**: path-sensitive tests need to account for platform differences (or use abstractions).
