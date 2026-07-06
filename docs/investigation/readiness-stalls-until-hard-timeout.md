# Investigation: requests stall until the 30s hard timeout instead of finalizing on readiness

**Status:** Resolved and confirmed live — fix shipped in `8091ef8` (#55), captured as
[ADR-0007](../adr/0007-solution-open-triggers-workspace-load.md). The last inferred link (that this
binary emits `projectInitializationComplete` in response to `solution/open`) has now been confirmed
by a live capture: the child Roslyn server was launched with `--logLevel Debug --extensionLogDirectory`
and observed loading the solution and reaching readiness on the real signal, with the hard-timeout
fallback no longer firing.
**Date:** 2026-07-06
**Area:** `RoslynServerProcess` readiness handshake

## Symptom

The intended behavior is: the first request to a freshly-spawned `roslyn-language-server`
stalls only while the server indexes the solution, then completes as soon as the server is
ready. The observed behavior is that the request stalls for the full **30-second hard timeout**
every time, and only then executes — often with degraded results.

## Root cause

**The mux never tells Roslyn which solution to load, so Roslyn never loads anything — which
means it never emits the readiness signal the mux is waiting for. The 30s hard timeout is doing
all the work.**

The chain:

1. **The binary is the raw `Microsoft.CodeAnalysis.LanguageServer`.** The `--help` output of the
   installed tool (v`5.9.0-1.26303.1`) shows the unmodified Roslyn LSP CLI: `--pipe`,
   `--brokeredServicePipeName`, `--devKitDependencyPath`, `--csharpDesignTimePath`,
   `--sourceGeneratorExecutionPreference`, `--autoLoadProjects`, `--stdio`. It is repackaged
   straight from `dotnet/roslyn`. It is **not** a wrapper (e.g. SofusA/csharp-language-server).

2. **The raw server has no notion of `initializationOptions.solutionPath`.**
   `RoslynServerProcess.SendInitialize` (`src/CsharpLspMux/RoslynServerProcess.cs:110-150`) sets
   `initializationOptions.solutionPath = <abs path>`. That key is a *wrapper convention*; the raw
   Roslyn server silently ignores unknown init options. The spawn (`Start`,
   `src/CsharpLspMux/RoslynServerProcess.cs:76-108`) also passes only `--stdio` — no
   `--autoLoadProjects`.

3. **So Roslyn opens zero projects.** The raw server loads a workspace only when the client sends
   a custom notification:
   - `solution/open` — `{"solution": "file:///abs/path/App.slnx"}`
   - `project/open` — `{"projects": ["file:///abs/path/App.csproj"]}`

   …or, as a weaker fallback, when launched with `--autoLoadProjects`. The mux sends **none** of
   these.

4. **No load ⇒ no readiness signal.** With nothing loading, Roslyn emits no `Loading…`
   `$/progress` tokens and never sends `workspace/projectInitializationComplete`. The mux sits in
   `Initialized` (requests gated on `Ready`, `src/CsharpLspMux/RoslynServerProcess.cs:152-186`)
   until the unconditional 30s timer (`src/CsharpLspMux/RoslynServerProcess.cs:228-247`) fires
   `TransitionToReady()` and drains the queue.

5. **Why the request then "succeeds" (partially):** the queued `textDocument/didOpen` lands and
   Roslyn serves it from its *miscellaneous-files* fallback — single-file semantics only (syntax,
   missing-semicolon diagnostics), no cross-project resolution. This matches the degraded behavior
   described in claude-code issue #38683.

The recent commits — #53 (`projectInitializationComplete`), #54 (`window.workDoneProgress`), #47
(progress tokens), `3e2abe7` (removing the grace timer) — all correctly built the **receiving**
half of the readiness handshake. The **sending** half — telling Roslyn to open the solution — was
never wired. The receiver is waiting for a signal that can never arrive.

## The fix

> **Shipped** in `8091ef8` (#55) and recorded as
> [ADR-0007: `solution/open` triggers Roslyn workspace load](../adr/0007-solution-open-triggers-workspace-load.md).

After the mux sends `initialized` to the child
(`src/CsharpLspMux/RoslynServerProcess.cs:208-227`, immediately before the hard timeout is
scheduled), send:

```json
{"jsonrpc":"2.0","method":"solution/open","params":{"solution":"file:///abs/path/App.slnx"}}
```

The mux already knows the exact solution path (`_solutionPath`) — that is `SolutionRouter`'s whole
job. Then:

- Roslyn loads the solution → emits `Loading…` `$/progress` → finally
  `workspace/projectInitializationComplete`.
- The existing #53 interceptor catches it → `TransitionToReady()` → queued requests drain **when
  the server is actually ready**, typically well under 30s.
- The 30s timer reverts to being a true safety net.

This also fixes result *quality*, not just latency: requests hit the fully-loaded project graph
instead of the miscellaneous-files fallback.

## Points worth deciding / watching

- **`solution/open` vs `project/open`:** use `solution/open` for `.sln`/`.slnx`. A bare `.csproj`
  target would need `project/open` with `{"projects":[uri]}`. Since `SolutionRouter` always
  resolves to a solution file, `solution/open` covers today's routing.
- **`--autoLoadProjects` is the wrong lever** — issue #38683 and the tool docs both call it "less
  reliable and slower," and it defeats the point of routing to one specific solution. Prefer
  explicit `solution/open`.
- **The `$/progress` title heuristic is fragile** (`src/CsharpLspMux/RoslynServerProcess.cs:283-308`
  matches titles containing `"Loading"`). Once `solution/open` works,
  `projectInitializationComplete` is the authoritative trigger and the title match is secondary; if
  relied on, verify Roslyn's actual progress titles rather than trusting the substring.
- **URI construction:** the `solution` value must be a `file://` URI (`new Uri(path).AbsoluteUri`),
  not a raw path — matching how `rootUri` is already built.

## Recommended empirical confirmation

Before/after the change, launch with `--logLevel Trace --extensionLogDirectory <dir>` and confirm:

- the log shows the solution actually loading and a `projectInitializationComplete` emission, and
- the mux log line `workspace load timeout … fired as fallback` **disappears**, replaced by
  `workspace ready in <n>ms` well under 30s.

This closes the one link inferred rather than observed live: that this binary emits
`projectInitializationComplete` in response to `solution/open`.

## References

- [claude-code#38683 — Improve LSP client compatibility with Roslyn language server](https://github.com/anthropics/claude-code/issues/38683)
- [roslyn.nvim — LSP protocol integration](https://deepwiki.com/seblyng/roslyn.nvim/5-lsp-protocol-integration)
- [roslyn-language-server on NuGet](https://www.nuget.org/packages/roslyn-language-server)
- [dotnet/roslyn](https://github.com/dotnet/roslyn)
