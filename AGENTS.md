# Sockseek engineering guidance

Sockseek is a self-hosted homeserver. Remote peers are untrusted; the operator,
configured storage, and ordinary local administration are trusted.

- Handle valid work best effort. Do not reject it because of estimated memory,
  aggregate size, queue depth, or another internal implementation limit.
- Bound concurrency and representations through streaming, paging, backpressure,
  compact scheduling, or disk-backed storage.
- Add hard limits only when derived from a real protocol, representation,
  security boundary, or explicit operator policy.
- Isolate failures to the smallest independent entry, request, root, or transfer.
  Never silently replace authoritative data with an empty or partial result.
- Preserve exact Soulseek identities and wire spelling unless verified protocol
  semantics require otherwise.
- Prefer one clear owner, model, and contract. Avoid parallel abstractions,
  speculative configuration, and unnecessary compatibility layers.
- Test observable behavior and failure isolation, not documentation wording.
- The solution-wide `dotnet test --no-restore` must run in under 15 seconds; 
  Remove waits, find tests that aren't useful, and optimize without reducing coverage.
  Refactor the production code for testability when needed.

Archived designs in docs/design/archive provide rationale and context but may contain superseded requirements.
