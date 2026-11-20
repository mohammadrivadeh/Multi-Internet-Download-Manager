# Development status

## Windows balancer pivot

Updated 2026-11-20. [ADR 0018](decisions/0018-windows-network-balancer.md) records the migration from segmented downloads to system-wide per-flow balancing.

- Added a standalone flow scheduler with IPv4 and IPv6 reservations, weighted assignment, health filtering, and concurrent reservation tests.
- Added a pinned Windows x64 source build for the upstream Go engine and a read-only adapter diagnostics executable. WPF opens on Network Balancer and uses the helper's JSON inventory when packaged. Existing downloader and queue workflows are accessible from the shell.
- The .NET solution builds without warnings or errors and all 54 core tests passed. The pinned Go suite passed on Windows, the upstream CLI reported the pinned revision, and the .NET bridge successfully read six adapter records from the packaged helper. The Wintun archive checksum and DLL signature were verified without installing the driver. Service integration, rollback hardening, and multi-uplink capture acceptance remain open. No live routes were changed.

### Desktop capture preview

- Added opt-in WPF Start and Stop and a separately packaged elevated Go host. The host uses the pinned upstream TUN and relay, verifies its desktop peer over a restricted named pipe, and requests cleanup on Stop or pipe loss. The desktop reports readiness, confirmed stop, and errors. Normal app builds still ship read-only diagnostics.
- The preview uses a dedicated Wintun adapter and avoids physical adapter setting changes. It refuses incompatible strong-host settings and a surviving capture adapter.
- Verified the normal solution build and the packaged capture preview, both with zero warnings or errors. All 55 core tests, 3 presentation tests, the upstream Go suite on rerun, and two capture-host tests passed. The initial upstream run encountered a loopback SOCKS5 connection reset in `TestUDPAssociateRefusedCleanly`. The preview was built without starting the elevated host or changing live routes.

### Capture route recovery

- Replaced the desktop host's split-default route commands with Windows IP Helper calls. Each intent is flushed to a protected machine registry journal before route creation. Start and teardown reconcile journaled entries through the dedicated adapter's stable GUID and verify route ownership before removal. Conflicts and uncertain cleanup preserve the journal and block new capture.
- The Windows Go tests passed, including 32 simulated interruptions across the four routes and ownership, ACL, and native route layout checks. An earlier upstream run hit the known loopback SOCKS5 reset and passed on rerun. Both .NET builds passed without warnings or errors, all 55 core and 3 presentation tests passed, and the packaged preview host matched the rebuilt binary.
- Recovery is triggered by normal teardown or the next authorized Start. Actual route creation, registry persistence through forced host termination or reboot, and network restoration still require isolated Windows VM acceptance. Capture remains opt-in. No live routes were changed.

### Downloads and adapter selection redesign

- Network Balancer owns adapter selection. The capture preview passes the chosen adapters to the elevated host, which checks them against current eligible adapters before starting and monitors them during capture. New downloads use Windows system routing with no application speed cap.
- Downloads has modal New download and queue-name editors, a toolbar, and a theme-aware row context menu and tooltips. The centered grid shows file, progress, status, category, and size. Destination, origin, checksum, and scheduling details are in Properties. Row pause and cancellation use a per-job token so sibling downloads can continue. Redownload from zero requires a fresh destination and preserves the existing file and partial download.
- Each category and queue has an independent settings dialog in the sidebar. Category defaults set the request count, priority, and base folder for new downloads. Queue dialogs manage concurrency and schedule. Older saved defaults apply to their original category, while individual queue settings keep their stored values.
- The New Download dialog proposes a destination from the URL and category, preserves manual edits, and defaults to Main without changing which queue is selected in the sidebar.
- The Downloads toolbar filters by download status. Select multiple reveals per-row checkboxes and a checkbox for all visible downloads. Separate dialogs let users choose a destination queue or category for the selected rows.
- Adapter inventory uses .NET directly and refreshes after Windows address changes. The capture host checks egress flags through IP Helper and refreshes the scheduler with eligible adapters from the original selection. Choose additional adapters when starting another capture session. Live throughput and Task Manager behavior remain to be verified.
- Automatic download names prefer the server's Content-Disposition metadata and preserve known extensions from redirects or the original URL. Manual filenames take precedence. SQLite checkpoint files remain internal to incomplete segmented downloads.
- An adapter with a usable address and gateway can be selected manually when the HTTPS test cannot verify it. The bounded desktop log in `Logs/diagnostics.log` beside the application records adapter tests, DNS and TCP results, download probes and stages, and capture counters by interface. The log excludes download URLs and filesystem destinations. Capture counters include all traffic seen by the host.

## Downloader

Updated 2026-11-14. The user authorized merging to main and continuing without waiting for their Windows test results. All outstanding acceptance is recorded in [PENDING-ACCEPTANCE.md](testing/PENDING-ACCEPTANCE.md); it does not block independent implementation.

## Implemented

- WPF visual refresh: Moonlight Glow shell and shared dynamic palette resources, with six palettes included. Runtime palette selection and live Windows visual acceptance are not implemented.
- WPF shell: custom borderless title bar with Segoe Fluent glyphs and clickable Downloads, Queue, Connections, and Preferences navigation. Connections use wrapping adapter cards. Preferences use paired saved-default fields. Downloads centers the queue, provides a collapsed New download editor, and shows a compact progress/speed/remaining strip. Queue row/cell colors use global palette resources. Release WPF build succeeds with zero warnings/errors. Live resize, title-bar hit testing, DPI, keyboard accessibility, and rendered contrast remain pending Windows acceptance.
- New download workflow: adapter selection and per-connection caps moved to Connections. Destination defaults to the Windows Downloads directory; if that directory remains selected, the URL path supplies a suggested filename. The primary OK action enqueues and starts processing. Release WPF build succeeds with zero warnings/errors.
- IDM-inspired list update: Downloads now shows category choices and queues in the left rail, and a labeled icon toolbar for New download, Start queue, and Pause. The central list includes size and an in-row progress bar with decimal KB/MB/GB values; the cap column is labeled KB/s. The progress bar uses an explicit one-way binding for its read-only row percentage. Per-category icons and Windows visual/accessibility acceptance remain open.
- Custom queues: Queue names and settings are stored in the protected preferences file; queue names are also stored with each encrypted download. Older queue records without a queue name default to Main. Users can create, rename, delete queues (deleting moves their downloads to Main), choose a queue for new downloads, and move selected jobs between queues. Each queue has an independent start/stop runner and runs 1–32 downloads in parallel, or All pending downloads capped at 32. Daily local-time start/stop windows support overnight ranges; a scheduled stop pauses active jobs and requeues only those interrupted by that schedule for the next start. Queue schedule automation requires the app to stay open and that queue to be started. Current tests cover parallel runners, independent queues, moves, renames, protected persistence and overnight schedules.

- Three production projects: Core, Infrastructure and App.Wpf; pinned .NET 10 SDK, nullable checks, warnings as errors and central NuGet versions.
- Immutable 64-bit intervals, a lazy serialized range allocator, bounded 1–128 workers and per-worker adaptive range sizing.
- Interface discovery, explicit Windows source/interface binding, isolated pools, stale snapshot checks and network-change cancellation. System route requires explicit opt-in.
- HTTP/1.1 real one-byte probing, cross-path representation validation, exact range and strong ETag validation, offset writes and sequential fallback.
- Header/inactivity timeouts, bounded transient retries and Retry-After handling (long server delays stop rather than retry early).
- SQLite schema version 1, file-flush-before-metadata transactions, checkpoint hashes, conservative resume, optional authoritative SHA-256 and no-overwrite finalization.
- WPF form and persistent queue/history: explicit per-job URL/destination/path/worker/checksum settings, priorities, scheduled starts, filename search and state filters.
- Windows DPAPI-protected queue payloads, a single-instance database lease, explicit requeue/cancel and record-only history clearing. Interrupted Running entries recover Paused.
- One active file at a time, aging-based ready selection, five-second due-job checks after Start queue, and awaited download/save/initialization shutdown.
- Optional per-job payload speed cap shared across all ranged workers/retries and sequential fallback; saved in the protected job configuration and displayed in KiB/s.
- Console probe/download harnesses, synthetic fixture server and Linux/Windows GitHub Actions build/test workflow.
- ADRs 0001–0017, architecture notes, acceptance procedures and backlog.

## Validation completed on the current machine

- Release solution build, all nine projects: **0 warnings, 0 errors**.
- Automated tests: **146 passed, 0 failed, 1 skipped** (54 Core, 89 integration, 3 presentation passed; 1 Windows-only DPAPI skipped).
- Tested: interval boundaries, randomized exact coverage, stale snapshots, HTTP request/response behavior, invalid identity/header rejection before writes, short/oversized bodies, cancellation, sparse offset above 4 GiB, complete downloads, SQLite resume, damaged checkpoint data repair, changed-resource rejection, differing representations across paths, sequential fallback, checksum failure, destination collision including a race at finalization, and long Retry-After refusal without retrying early. Queue tests additionally cover priority aging, explicit-offset schedules, state transitions, encrypted persistence/tamper rejection, exclusive ownership, duplicate destinations/runners, cancellation ordering, failure progression, restart recovery and protected history after a real scheduled HTTP transfer. Rate-policy tests cover shared concurrent reservations, no idle credit, aggregate engine delivery pacing in segmented/sequential modes, cancellation and saved cap settings.
- Tests configured at 16, 32 and 64 workers verified more than eight overlapping simulated requests and correct full output. These are correctness tests, not speed benchmarks or real interface aggregation.
- Real local end-to-end smoke: ASP.NET fixture server → HTTP probe → range transfer → SQLite checkpoints → final file; compared with authoritative fixture SHA-256 and verified sidecar cleanup. A 16-worker configuration was used; the small fixture needs only one default-sized range, so this smoke does not establish 16 concurrent network requests.
- Windows x64 WPF portable build cross-published successfully. Requires .NET 10 Desktop Runtime x64. Not executed on Windows.
- NuGet auditing remained enabled. Initial SQLite 10.0.0 graph was rejected due to a vulnerable native transitive dependency; serviced Microsoft.Data.Sqlite 10.0.15 restored and built successfully.
- CI configuration is saved; no remote workflow result is claimed without checking GitHub.
- Queue feature validation: Release WPF build succeeded with zero warnings/errors. The focused queue/persistence checks passed (5/5); Core tests passed (50/50), Integration tests passed (91/91 when excluding the Windows DPAPI profile test), and Presentation tests passed (3/3). Running the complete Integration suite on this machine failed only at the existing DPAPI profile-protection test because this Windows session could not complete its user-profile protection call. Windows desktop visual acceptance remains pending.

## Phase and feature matrix

| Plan area | Implementation status | Remaining work / evidence |
| --- | --- | --- |
| Phase 0: design baseline | Initial architecture/contracts/ADRs and original wireframe represented | Continue decisions as components evolve |
| Phase 1: Windows networking proof | Harness and application transport implemented | Real independent-path proof pending user tests |
| Phase 2: single-path engine | Initial full-file engine, validated ranges and sequential fallback implemented | Richer failure diagnostics, auth/context, more failure-server cases |
| Phase 3: adaptive multi-path | Shared pending pool, adaptive range sizing and selected transports implemented | Active-tail splitting/fenced leases, adaptive concurrency, fairness across jobs, real benchmarks |
| Phase 4: durable recovery | SQLite checkpoint ordering, hashed resume and finalization implemented | Process-kill/power-loss and disk-failure injection, migrations beyond schema 1, large checkpoint optimization |
| Phase 5: WPF base product | Functional desktop, protected queue/history, priority, one-time start scheduling, search/filter and per-download aggregate speed cap implemented | Extension-based destination rules, persistent consumption quotas/metered accounting, weekday/DST schedule rules, concurrent-job fairness, tray, notifications, settings UI, accessibility/DPI acceptance, installer |
| Phase 6: browser integration | Excluded by user | No browser extensions, native messaging or capture work |
| Phase 7: advanced modules | Not implemented | Supported-site non-DRM media, HLS/DASH, FTP/FTPS, sync and antivirus integration |

No claim is made that all phases or IDM feature parity are complete. No routes, firewall settings, public release or external deployment were changed.

## Operational limitations

- Resume requires the original exact URL and a stable strong ETag/length; changed or expired links are not refreshed automatically. Metadata stores only a URL hash, not the URL or credentials.
- Sequential fallback restarts from zero after cancellation and deletes only its own partial file. Unknown size stays indeterminate.
- HTTP(S) redirects follow a bounded, credential-stripping policy; loops, HTTPS downgrades and changed final resource URIs are rejected. Cookies/authentication, proxies and encoded representations remain unsupported. Certificate validation stays enabled.
- Each worker retains exclusive ownership until it finishes; failed extents are retried whole. Active-tail reassignment and speculative duplicate requests are not implemented.
- Metadata grows with checkpoint count. The current implementation loads checkpoint rows to verify them; stress tuning and coalesced/streaming recovery remain open.
- A crash after rename but before database cleanup can leave an orphan sidecar. Existing finalized destinations are never replaced automatically.
- Closing the window cancels/awaits the queue, pending saves and initialization before releasing the store lease. Tray/background continuation is not implemented.
- Scheduling requires the app running and Start queue enabled. It is checked at five-second intervals; no OS wake/start tasks are created. Optional daily fixed-offset windows now restrict and resume unfinished work. Only one file runs at once, with concurrent requests inside that file.
- DPAPI persistence is tied to the Windows user profile. Cross-account imports, profile-key recovery and queue encryption on Linux are not implemented. The actual Windows DPAPI regression test is skipped on Linux and configured to execute in Windows CI; no remote CI success is claimed.
- A crash between finalized-file rename and queue-state commit may leave a Paused history row for an existing destination. That file remains protected from overwrite; reconcile the row manually.

## Delivery history

- `c475123`: .NET foundation and Windows probe, initially pushed to work.
- `5cb2694`: validated range transfer, initially pushed to work.
- Both commits fast-forward merged and pushed to main at the user's request.
- `2463ff5`: functional engine, SQLite recovery and first desktop app, pushed to main.
- `fc6d923`: protected queue/history and scheduled starts, pushed to main.
- `5019ad1`: shared per-download speed cap, pushed to main.
- The bounded redirect/resource-identity increment follows; nonlocal acceptance remains recorded without blocking development.

## Speed-cap limitations

The current limit measures accepted application payload, not wire traffic or billing usage. Probe/header/TLS overhead and OS buffering are outside the cap. Multiple file jobs, per-path budgets, live cap changes and consumption quotas remain future work. Small configured caps with large minimum ranges can delay durable progress updates; cancellation still interrupts pending waits and uncertain extents are redownloaded safely.

## Redirect increment validation

Supported statuses: 301/302/303/307/308; maximum eight redirects within a shared thirty-second header deadline. Tests cover header forwarding and stripping, invalid targets, loops/hop limits, cancellation, ranged/sequential completion, and refusal to mix final URIs across paths or resume. A real local three-hop HTTP download matched the authoritative fixture SHA-256; a loop was rejected before partial state was created. Existing direct-URL checkpoint identity remains compatible. Diagnostic NetworkProbe behavior is intentionally unchanged.

The GitHub Actions API request returned 403, so remote CI results remain unverified. Linux tests and cross-compilation do not establish real TLS, Windows desktop or independent interface acceptance.

## Scope and desktop architecture update

Browser integration/extensions and Site Grabber were removed by the user, not deferred. The remaining roadmap continues. WPF now separates Views/Shell, Views/Sections, ViewModels, Models, Services, Resources and Themes. App is the explicit composition root; desktop dialogs, dispatch/scheduling and protected queue lifetime have injected adapters. Shell commands are organized into focused partial files. Form, queue and status views share the shell presentation state; independent feature ViewModels can be introduced with further behavior rather than speculative frameworks.

The queue occupies a bounded Grid viewport with row/column virtualization; only the form scrolls. System color resources and default control focus behavior are retained. Release build and 104 automated regressions pass, one Windows-only DPAPI test remains skipped on Linux. Actual WPF layout, accessibility and lifecycle acceptance remain pending Windows execution.

## Categories and defaults increment

Per-job category selection, category filtering and non-running entry reclassification are implemented. Old encrypted queue records default to General. Reclassification preserves queue state, source, destination and data files. Validated form defaults (workers, cap, priority, category) persist in a typed/versioned encrypted preferences file with flushed atomic replacement. Sources, destinations, network choices, checksum, schedule and autorun are excluded. An unreadable preferences file is preserved and cannot be overwritten via Save defaults after load failure.

New tests cover legacy category compatibility, bounds, protected round-trip, coordinator state preservation and active-edit refusal, default preference recovery, encryption failure and tampering. Windows execution of the new controls remains pending; no new hardware-readiness claim is made.

## Live progress and details increment

Accepted-byte progress is now separate from checkpointed progress, including maximum-prefix accounting across range retries. WPF shows event-based payload speed, estimated remaining time and a collapsible selected-job summary with destination, sanitized origin and checksum. Unknown estimates are shown as unknown. New policy tests cover coverage/ETA, and a rate-limited engine test verifies intermediate accepted bytes before the durable checkpoint. Stall heartbeat, smoothing and per-path telemetry are still pending.

## Review fixes

Fixed terminal persistence errors leaving ghost Running rows: retain true outcomes and pending writes, retry explicitly or before the next mutation/run, and attempt persistence on shutdown. Fixed failed sidecar deletion after successful rename: Completed now carries a persisted cleanup warning rather than failing the file. Fixed truncated/HTTP body errors misclassified as Storage and sub-250ms ETA samples flickering unknown. Seven added regressions pass, including injected failures for all terminal outcomes and full-file cleanup-failure verification. Pending writes remain volatile until a successful save; crashes in that window still need the documented existing-file reconciliation.

## Consolidated remaining work and daily windows

See docs/REMAINING-WORK.md for the complete remaining register. Daily fixed-offset/overnight transfer windows are now captured per queued job, enforced by the executor and persisted across requeues. Window closure awaits cancellation and requeues unfinished work without a busy loop; explicit stop remains a pause. This does not implement recurring duplicate downloads or closed-app scheduling. Remaining roadmap work is explicitly open rather than marked done.

## Per-connection cap increment

Each selected address now captures its own KiB/s cap, protected with its queue entry. A transport's workers share one limiter and combine it with the job limiter. Sequential fallback respects its selected transport's limit. Discovery refresh preserves unsaved settings for unchanged addresses. These are accepted-payload caps, not wire-level shaping or billing quotas. Per-path quota accounting is still open in the remaining-work register.

## Category destination rules increment

Added explicit category-folder destination suggestions, existing-base folder selection and protected storage of the optional base folder. Windows leaf validation rejects traversal, encoded separators, device/stream names and unsafe suffixes. Add creates only the validated generated category directory; manual paths are not auto-created or overridden. Native dialog, junction/long-path and hostile-local-process TOCTOU behavior remain outside Linux validation. Extension/content-type routing and automatic conflict renaming remain future work.

## UI update/optimization increment

Stable observable queue rows now reconcile in place; periodic snapshots no longer clear the collection or unconditionally reset the collection view. Live filtering tracks row changes, category choices avoid unnecessary churn, and progress updates preserve row identity. Added portable presentation tests for update/move/add/remove and duplicate-snapshot notification contracts. UI adds resizable columns/panels, job counts, empty results and state-aware resume/cancel actions. Stale speed/ETA is cleared by the existing polling timer and at runner exit. Actual WPF selection, scrolling, live filtering and accessibility remain Windows acceptance tasks; no rendering or frame-rate benchmark is claimed.

## Safe job editing increment

Added policy-only updates for inactive unfinished jobs and an explicit WPF edit mode. Identity, source, destination, checksum, state and checkpoint progress remain fixed. Store-first atomic publication preserves previous settings on failed saves. Original saved paths are retained unless explicitly replaced; missing discoveries do not silently remove them. Queue starts/scheduled dispatch are held during editing. Tests cover immutable fields, copied settings, state restrictions, validation and persistence failure/retry. Source refresh and live active-policy edits remain out of scope for this increment.
