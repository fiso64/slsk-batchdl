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
- Do not turn optional functionality into an availability dependency: reject or
  disable the affected feature with a clear warning, and fail startup only when
  safe core operation requires it.
- Preserve exact Soulseek identities and wire spelling unless verified protocol
  semantics require otherwise.
- Prefer one clear owner, model, and contract. Avoid parallel abstractions,
  speculative configuration, and unnecessary compatibility layers.
- Make daemon-owned work observable: log coarse lifecycle and health transitions
  at the normal log level with a stable correlation ID, outcome, duration, and
  safe bounded counts. Log terminal failures with the full exception once at the
  operation owner; never silently discard diagnostic evidence or rely only on
  metrics or API state.
- Keep logs useful without spam: use `Information` for operator-relevant
  lifecycle, `Debug` for decisions, reuse, and routine rejections, rate-limited
  `Warning` for recoverable degradation, and `Error` for failed operations.
  Never log per-item untrusted or private content; use IDs or hashes when needed.
- Test observable behavior and failure isolation, not documentation wording.
- Keep warm `dotnet test --no-restore` under 15 seconds (it does not matter if the issue is pre-existing). Do not increase the
  current test-worker counts or remove CI's sequential-host guard: oversubscription
  caused thread-pool starvation and flaky timeouts. Remove polling and fixed waits,
  optimize setup, and mark stress tests as `Load`. Delete obsolete, redundant,
  overly specific, implementation-coupled, or otherwise low-value tests; preserve
  meaningful behavioral coverage rather than test count.
- Tests are quiet by default. Capture or disable application logging unless the
  test asserts it; incidental console logs are test noise, not diagnostics.

Archived designs in docs/design/archive provide rationale and context but may contain superseded requirements.
