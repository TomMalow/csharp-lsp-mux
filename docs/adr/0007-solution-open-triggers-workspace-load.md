# ADR-0007: `solution/open` triggers Roslyn workspace load

## Status

Accepted

## Context

The installed `roslyn-language-server` (`Microsoft.CodeAnalysis.LanguageServer`, v5.9.0) is the
raw `dotnet/roslyn` LSP CLI, not a wrapper. The raw server opens **zero** projects after the
`initialize`/`initialized` handshake unless the client explicitly tells it what to load.

The mux was setting `initializationOptions.solutionPath` in its `initialize` request — but that
key is a *wrapper convention* (e.g. SofusA/csharp-language-server), and the raw server silently
ignores unknown init options. So Roslyn loaded nothing, emitted no `Loading…` `$/progress`
tokens, and never sent `workspace/projectInitializationComplete` — the very signal the readiness
handshake (#53, #54, #47) waits on. Requests sat gated in `Initialized` until the unconditional
30s hard timeout fired `TransitionToReady()` and drained the queue. Every first request paid the
full 30s, then executed against Roslyn's miscellaneous-files fallback (single-file semantics only,
no cross-project resolution) — matching claude-code#38683.

Full analysis: `docs/investigation/readiness-stalls-until-hard-timeout.md`.

## Decision

After the mux sends `initialized` to the child and drains queued notifications — but before the
hard-timeout task is scheduled — it writes a `solution/open` notification to the child's stdin:

```json
{"jsonrpc":"2.0","method":"solution/open","params":{"solution":"file:///abs/path/App.slnx"}}
```

- `params.solution` is the `file://` URI of the routed solution (`new Uri(path).AbsoluteUri`),
  built the same way as `rootUri`.
- Sent only when a solution path is configured. `SolutionRouter` always resolves to a solution
  file, so `solution/open` (not `project/open`) covers today's routing.
- The dead `initializationOptions.solutionPath` is removed from the `initialize` request.

Rejected alternatives:

- **`--autoLoadProjects` launch flag** — documented by the tool and claude-code#38683 as "less
  reliable and slower," and it defeats the point of routing to one specific solution. Explicit
  `solution/open` is preferred.
- **Keep `initializationOptions.solutionPath`** — a no-op against the raw server; kept nothing
  working while looking like it did.

## Consequences

- **Ready when actually ready**: Roslyn loads the solution → emits progress →
  `workspace/projectInitializationComplete` → existing #53 interceptor calls `TransitionToReady()`
  and drains queued requests, typically well under 30s. The 30s timer reverts to a true safety net.
- **Better result quality, not just latency**: requests hit the fully-loaded project graph instead
  of the miscellaneous-files fallback.
- **`projectInitializationComplete` is now the authoritative readiness trigger**; the `$/progress`
  title substring match (`"Loading"`) is secondary and remains fragile — verify Roslyn's actual
  progress titles before relying on it.
- **Coupled to the raw Roslyn server contract**: if the mux ever targets a wrapper server (see
  ADR-0002), the load mechanism must be revisited. A bare `.csproj` target would need `project/open`
  with `{"projects":[uri]}`.
