# ADR-0003: Transport-transparent forwarding

## Status

Accepted

## Context

The router sits between client and server on the JSON-RPC stream. It could parse and transform LSP messages deeply, or forward them as opaque JSON blobs.

## Decision

Forward raw JSON unchanged. The only mutation is correlation ID rewriting — the router maps client-facing IDs to server-facing IDs to avoid collisions when multiple child servers are active.

## Consequences

- **Future-proof**: new LSP methods work without router changes.
- **Low overhead**: no deserialization of message bodies.
- **Limited introspection**: the router cannot reject or transform individual fields within a request/response without adding per-method handlers.
