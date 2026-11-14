# ADR 0004: Download coordinator and durable range recovery

- Date: 2026-11-14
- Status: Accepted for initial engine; hardware/fault-injection acceptance pending

## Context

The user authorized continued implementation while they own real Windows tests. Independent engine work proceeds; experimental Windows routing is not certified by Linux tests.

## Decision

Probe with a real one-byte GET. Use strong ETag + known length + validated 206 for segmentation; probe every selected transport and require the same representation before combining payload. Fall back to sequential download for 200, missing strong validators or empty resources. Redirects, authenticated request contexts and expired-link refresh remain explicit future work; never silently combine unvalidated versions.

Use one locked lazy range allocator, a bounded 1–128 worker count and one unique extent per worker. Each completed worker takes more pending work. Estimate its next range from observed useful throughput for a two-second target, clamped to configured sizes. Active tails are not split/reassigned yet. Cancel all sibling workers on a terminal error and await all writers before returning. This ownership discipline avoids stale writes without promising the later generation-fenced live-tail scheduler.

Use a temporary `.part` on the destination volume and SQLite schema version 1 at `.part.sqlite`. Lock the data file exclusively. Store only a source URI SHA-256 identity, resource length, ETag and per-range hashes; do not persist plaintext URLs, request headers or credentials. Reject changed identity, unknown schemas, overlapping/out-of-range metadata and partial files without metadata. Never overwrite an existing destination.

After a range completes, hash its bytes, serialize checkpoint work, flush file data to disk, then commit its hash/extent in a SQLite transaction. SQLite uses WAL + synchronous FULL and no connection pooling. On resume, hash every checkpointed range before accepting it; discard mismatches and redownload. Metadata memory grows with checkpoint count; streaming/coalesced recovery for very large files remains a performance task. No total-file buffers or unbounded worker tasks are used.

Reprobe identity before finalization; verify expected SHA-256 when supplied. Flush, close all handles and rename on the same volume without replacement. Cleanup the database after rename. A crash after rename and before sidecar cleanup may leave an orphan database, but the complete destination is not overwritten on restart. Completion does not require a source-supplied checksum when none exists; in that case only protocol identity/size and local checkpoint consistency are verified.

Sequential mode uses a new full GET and a 64 KiB buffer. It does not persist resume state; interrupted sequential data is deleted only if this operation created it. Unknown totals remain indeterminate.

## Timeouts and retries

Bound connection setup to eight seconds in supplied transports, response headers and each inactive read to thirty seconds. There is no arbitrary whole-download timeout. Retry transient transport/short-body/inactivity failures up to two times by default with bounded exponential jitter. Respect 429/503 Retry-After; if it exceeds the thirty-second retry budget, stop instead of retrying early. Do not retry disk failures, changed identity or malformed range responses. Retry the whole currently owned extent without double-counting progress.

## Dependencies

Microsoft.Data.Sqlite 10.0.15 (MIT) supplies Microsoft’s maintained provider and native SQLite bundle. SQLite is public domain; SQLitePCLRaw uses Apache-2.0. NuGet auditing rejected the initial 10.0.0 dependency graph due to GHSA-2m69-gcr7-jv3q; use the serviced version instead, with no audit suppression or trust bypass. SQLite native binaries must be included in RID-specific packaging and checked on Windows.
