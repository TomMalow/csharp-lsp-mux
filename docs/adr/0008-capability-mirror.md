# ADR-0008: Scoped LSP surface + capability mirror

## Status

Accepted

## Context

The mux is simultaneously a *server* to Claude Code and a *client* to each
`roslyn-language-server` child. "Capabilities" therefore has two directions: the
`initialize` response the mux synthesizes for Claude Code, and the `initialize` request
the mux sends each child. Before this change these were two independently
hand-authored `JsonObject` literals — one in `MuxDispatcher`, one in
`RoslynServerProcess` — with no structural link between them. The correspondence
between the two sides is not a uniform boolean: client-facing `workspaceSymbolProvider`
maps to Roslyn-facing `workspace.symbol`; client-facing `textDocumentSync` maps to
Roslyn-facing `textDocument.synchronization`. Nothing enforced that a capability added
on one side also landed on the other, and #67's investigation found exactly that kind of
drift (`implementationProvider`/`callHierarchyProvider` routed on the Roslyn-facing side
in spirit but were never advertised on either side).

Separately, that same investigation surfaced a gap in ADR-0003's "future-proof: new LSP
methods work without router changes" consequence. `callHierarchy/incomingCalls` and
`callHierarchy/outgoingCalls` are not `textDocument/*` methods and carry their target at
`params.item.uri`, not `params.textDocument.uri`. The existing `textDocument/*` routing
rule doesn't recognize them, so they silently fall through to the no-op catch-all and
the client hangs until timeout. ADR-0003's payload transparency held — nothing mangled
the forwarded JSON — but its routing claim did not: a genuinely new method-*family* still
needs a router that knows where that family's target URI lives.

## Decision

**Single-source `Capabilities` module.** `src/CsharpLspMux/Capabilities.cs` is now the
one place that defines the LSP capabilities the mux advertises. It distinguishes:

- **Feature providers** — user-facing LSP features (hover, definition, references,
  documentSymbol, workspaceSymbol, completion, signatureHelp, rename, codeAction,
  diagnostic, and — once #70/#71 land — implementation and callHierarchy). Each is
  declared once with both its client-facing path/shape and its Roslyn-facing
  path/shape, so the two sides project from a shared definition rather than being
  kept in sync by convention.
- **Operational capabilities** — `textDocumentSync`/`textDocument.synchronization` and
  `window.workDoneProgress` — which exist to make the tool function rather than expose
  a feature, and are exempt from the mirror (`window.workDoneProgress` has no
  client-facing counterpart at all: Claude Code has no need to know the mux's own
  progress-reporting capability to Roslyn).

`MuxDispatcher.HandleInitialize` and `RoslynServerProcess.SendInitialize` both call into
this module (`BuildClientFacingCapabilities()` / `BuildRoslynFacingCapabilities()`)
instead of authoring their own capability JSON. This slice is behavior-preserving: the
capability set advertised on both sides is unchanged from before the extraction, proven
by the pre-existing capability assertions in `MuxDispatcherTests` and
`RoslynServerProcessTests` passing unmodified, plus a new capability-lock test per side
in `CapabilitiesTests` that pins the exact advertised key set.

**Mirror invariant, enforced structurally.** Because a `FeatureProvider` record carries
both sides' paths together, a feature literally cannot be declared for one side without
the other — `CapabilitiesTests.FeatureProviders_MirrorAcrossBothSides` asserts both
paths resolve to a value in the respective built capability object, catching a
regression in the builder itself rather than in the data.

**Scoped LSP surface — refinement to ADR-0003.** Transport transparency still holds:
forwarded *payloads* are never parsed or mutated beyond correlation-ID rewriting. What
this ADR refines is the routing claim: "new LSP methods work without router changes" is
true only for a method that fits an already-known routing shape (today,
`textDocument/*` reading `params.textDocument.uri`). A method family with a genuinely
different shape — `callHierarchy/*` reading `params.item.uri` — requires an explicit,
purpose-built dispatch handler that knows where that family's target URI lives. The mux
therefore supports a deliberately **scoped** LSP surface: the operations Claude Code
actually issues, each backed by a routing rule the mux author has looked at, not an
implicit "anything textDocument-shaped just works" assumption extended to families that
were never checked.

## Point-in-time snapshot

The nine LSP operations Claude Code drives the mux with, and their status as of this
ADR (see `CONTEXT.md` for the live, maintained list — this table is a snapshot, not the
source of truth):

| Operation | Routes today | Advertised today | Tracking |
|---|---|---|---|
| `textDocument/hover` | Yes (`textDocument/*`) | Yes | — |
| `textDocument/definition` | Yes | Yes | — |
| `textDocument/references` | Yes | Yes | — |
| `textDocument/documentSymbol` | Yes | Yes | — |
| `workspace/symbol` | Yes (broadcast) | Yes | — |
| `textDocument/implementation` | Yes | **No** | #70 |
| `textDocument/prepareCallHierarchy` | Yes | **No** | #71 |
| `callHierarchy/incomingCalls` | **No** — falls through to no-op | **No** | #71 |
| `callHierarchy/outgoingCalls` | **No** — falls through to no-op | **No** | #71 |

## Consequences

- **Structural mirror, not conventional**: adding a feature provider to `Capabilities`
  and forgetting one side's projection is a compile-time record-construction error, not
  a docs/code drift that surfaces later as a silent client-capability gap.
- **Capability-lock tests are now the deliberate friction point**: any future change to
  the advertised set fails a pinned-constant test until the change is acknowledged.
- **Routing still needs a per-family decision**: this ADR does not make call hierarchy
  work — #71 adds the dedicated `params.item.uri` handlers. It records why that handler
  is necessary rather than a `textDocument/*` extension, so the next new method family
  is evaluated the same way instead of being force-fit into the existing rule.
- **ADR-0003 stands, refined**: "forward raw JSON unchanged" is unaffected; "new LSP
  methods work without router changes" now reads as scoped to methods whose target URI
  lives where an existing routing rule already looks.
