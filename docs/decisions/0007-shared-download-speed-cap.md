# ADR 0007: Shared per-download payload speed cap

- Date: 2026-11-14
- Status: Accepted; real network/UI acceptance pending

## Decision

Add optional BytesPerSecondLimit to DownloadOptions. Null means unlimited; configured values range from 1 B/s to 1 GiB/s. The WPF form accepts integer KiB/s, with zero translated to unlimited. Each queue entry captures and protects its own cap, and displays it in the queue table. Existing stored payloads without the property remain unlimited.

Create one monotonic delivery schedule per complete download and share it across all ranged workers and retries. Sequential fallback uses the same limiter. Reserve time for the actual byte count returned by each body read, then await its slot before accepting/writing that payload. Do not reserve the requested buffer size, which could unfairly overcharge short network reads. Limit each read to roughly one tenth of the configured rate, clamped to 1–65536 bytes, to bound buffering and improve cancellation response.

Use TimeProvider timestamps, not wall-clock dates, for elapsed time; use its cancellable delay API. Scheduling policy in Core serializes reservations and rounds durations upward to the next tick. There is no accumulated credit after long idle periods. Pausing cancels outstanding waits promptly. A resume constructs a fresh limiter with the saved cap. No new package is required.

## Meaning and limits

This cap applies to application payload delivery/writes aggregated across the download's workers. It is not a network-driver shaper, instantaneous wire-traffic guarantee, metered-data quota or ISP billing counter. HTTP headers, TLS, the one-byte probes and OS/socket buffering are outside this measurement. Already-read in-flight buffers can be ahead of writes, and scheduling/OS timers can make delivery uneven or slower. Retried body bytes consume delivery time as well; they do not inflate unique completed progress.

One file currently runs at a time in the queue, so its cap covers that active job. Cross-application/global traffic control, separate per-path caps, accounting quotas and live editing of an active job's policy remain future work. Speed-limited jobs can checkpoint/update durable progress infrequently when the configured minimum range is large; this does not imply the UI is blocked or that uncheckpointed bytes are safe to resume.

## Validation

Deterministic policy tests verify shared reservations, concurrency, upward charging, invalid caps and no idle burst credit. Engine tests download the fixture through four ranged workers and through sequential fallback, verify the full bytes/hash, and assert that a one-second configured delivery budget is not bypassed. A limiter wait test verifies immediate cancellation; the encrypted queue round-trip test also preserves the saved rate option. These checks are not real multi-interface bandwidth benchmarks.
