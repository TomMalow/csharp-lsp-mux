# ADR-0002: Roslyn language server only

## Status

Accepted

## Context

The .NET LSP ecosystem has multiple servers: `roslyn-language-server` (ships with the C# Dev Kit / VS Code extension), OmniSharp, and `csharp-ls`. Each has different initialization options, capabilities, and quirks.

## Decision

Target only `roslyn-language-server`. Do not support OmniSharp or csharp-ls.

## Consequences

- **Simpler protocol handling**: one set of initialization options, one capability surface.
- **Tighter coupling**: if Roslyn changes its CLI or protocol, we must adapt.
- **No fallback**: users without `roslyn-language-server` installed get no LSP at all.
