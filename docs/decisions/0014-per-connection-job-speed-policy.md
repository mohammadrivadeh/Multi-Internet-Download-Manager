# ADR 0014: Per-connection speed policy captured in queued jobs

- Date: 2026-11-14
- Status: Accepted; hardware pacing acceptance pending

Extend each selected QueuedPath with an optional accepted-payload rate cap. The desktop accepts KiB/s per address, zero meaning unlimited, and captures it in the protected queue payload. Old records remain unlimited. Refreshing discovery preserves unsaved cap values for the same adapter/address. System-route mode ignores selected-path settings deliberately.

The engine creates one limiter per transport, shared across all workers assigned to that transport. Apply both the job and path limiter to actual read-byte counts before accepting/writing them. Await both schedules concurrently so two identical caps do not add their delays and halve delivery artificially. Use the smaller read size to bound buffering. Sequential fallback applies the first selected transport's cap as well. Invalidation and cancellation still await all writers before releasing sessions.

Caps are per job/path session, not permanent system-interface shapers. This does not implement usage quotas, metered billing or a cross-job cumulative data ledger. Probes/headers/TLS/OS buffering remain outside accepted-payload accounting. Tests verify pacing shared by workers in ranged and sequential modes, saved per-path settings and invalid policy rejection. Real multi-interface throughput and OS traffic accounting remain pending.
