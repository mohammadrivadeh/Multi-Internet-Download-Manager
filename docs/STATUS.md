# Development status

Updated 2026-11-14. The user authorized merging to main and continuing without waiting for their Windows test results. All outstanding acceptance is recorded in [PENDING-ACCEPTANCE.md](testing/PENDING-ACCEPTANCE.md); it does not block independent implementation.

## Implemented

- Three production projects: Core, Infrastructure and App.Wpf; pinned .NET 10 SDK, nullable checks, warnings as errors and central NuGet versions.
- Immutable 64-bit intervals, a lazy serialized range allocator, bounded 1–128 workers and per-worker adaptive range sizing.
- Interface discovery, explicit Windows source/interface binding, isolated pools, stale snapshot checks and network-change cancellation. System route requires explicit opt-in.
- HTTP/1.1 real one-byte probing, cross-path representation validation, exact range and strong ETag validation, offset writes and sequential fallback.
- Header/inactivity timeouts, bounded transient retries and Retry-After handling (long server delays stop rather than retry early).
- SQLite schema version 1, file-flush-before-metadata transactions, checkpoint hashes, conservative resume, optional authoritative SHA-256 and no-overwrite finalization.
- Initial WPF form with URL/destination, interface selection, worker count, SHA-256, progress, start/resume/pause and awaited cancellation on close.
- Console probe/download harnesses, synthetic fixture server and Linux/Windows GitHub Actions build/test workflow.
- ADRs 0001–0005, architecture notes, acceptance procedures and backlog.

## Validation completed on the current machine

- Release solution build, all eight projects: **0 warnings, 0 errors**.
- Automated tests: **60 passed, 0 failed, 0 skipped** (14 Core, 46 integration).
- Tested: interval boundaries, randomized exact coverage, stale snapshots, HTTP request/response behavior, invalid identity/header rejection before writes, short/oversized bodies, cancellation, sparse offset above 4 GiB, complete downloads, SQLite resume, damaged checkpoint data repair, changed-resource rejection, differing representations across paths, sequential fallback, checksum failure, destination collision including a race at finalization, and long Retry-After refusal without retrying early.
- Tests configured at 16, 32 and 64 workers verified more than eight overlapping simulated requests and correct full output. These are correctness tests, not speed benchmarks or real interface aggregation.
- Real local end-to-end smoke: ASP.NET fixture server → HTTP probe → range transfer → SQLite checkpoints → final file; compared with authoritative fixture SHA-256 and verified sidecar cleanup. A 16-worker configuration was used; the small fixture needs only one default-sized range, so this smoke does not establish 16 concurrent network requests.
- Windows x64 WPF portable build cross-published successfully. Requires .NET 10 Desktop Runtime x64. Not executed on Windows.
- NuGet auditing remained enabled. Initial SQLite 10.0.0 graph was rejected due to a vulnerable native transitive dependency; serviced Microsoft.Data.Sqlite 10.0.15 restored and built successfully.
- CI configuration is saved; no remote workflow result is claimed without checking GitHub.

## Phase and feature matrix

| Plan area | Implementation status | Remaining work / evidence |
| --- | --- | --- |
| Phase 0: design baseline | Initial architecture/contracts/ADRs and original wireframe represented | Continue decisions as components evolve |
| Phase 1: Windows networking proof | Harness and application transport implemented | Real independent-path proof pending user tests |
| Phase 2: single-path engine | Initial full-file engine, validated ranges and sequential fallback implemented | Redirect policy, richer failure diagnostics, auth/context, more failure-server cases |
| Phase 3: adaptive multi-path | Shared pending pool, adaptive range sizing and selected transports implemented | Active-tail splitting/fenced leases, adaptive concurrency, fairness across jobs, real benchmarks |
| Phase 4: durable recovery | SQLite checkpoint ordering, hashed resume and finalization implemented | Process-kill/power-loss and disk-failure injection, migrations beyond schema 1, large checkpoint optimization |
| Phase 5: WPF base product | First functional desktop flow implemented | Queue/history/categories, speed/quota policy, scheduler, tray, notifications, settings, localization infrastructure expansion, accessibility/DPI acceptance, installer |
| Phase 6: browser integration | Not implemented | Extensions, native messaging, minimal protected request context and expired-link refresh |
| Phase 7: advanced modules | Not implemented | Supported-site non-DRM media, HLS/DASH, FTP/FTPS, Site Grabber, sync and antivirus integration |

No claim is made that all phases or IDM feature parity are complete. No routes, firewall settings, public release or external deployment were changed.

## Operational limitations

- Resume requires the original exact URL and a stable strong ETag/length; changed or expired links are not refreshed automatically. Metadata stores only a URL hash, not the URL or credentials.
- Sequential fallback restarts from zero after cancellation and deletes only its own partial file. Unknown size stays indeterminate.
- Redirects, cookies/authentication, proxies and encoded representations are rejected/unsupported in this first version. Certificate validation stays enabled.
- Each worker retains exclusive ownership until it finishes; failed extents are retried whole. Active-tail reassignment and speculative duplicate requests are not implemented.
- Metadata grows with checkpoint count. The current implementation loads checkpoint rows to verify them; stress tuning and coalesced/streaming recovery remain open.
- A crash after rename but before database cleanup can leave an orphan sidecar. Existing finalized destinations are never replaced automatically.
- Closing the window cancels and awaits the current transfer; tray/background continuation is not implemented.

## Delivery history

- `c475123`: .NET foundation and Windows probe, initially pushed to work.
- `5cb2694`: validated range transfer, initially pushed to work.
- Both commits fast-forward merged and pushed to main at the user's request.
- The functional engine/recovery/desktop increment is delivered with the next main commit.
