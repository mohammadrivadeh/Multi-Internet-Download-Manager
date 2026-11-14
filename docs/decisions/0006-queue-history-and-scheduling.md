# ADR 0006: Persistent queue, protected history and explicit scheduled starts

- Date: 2026-11-14
- Status: Accepted; Windows UI/DPAPI acceptance pending

## Queue ownership and state

Add a platform-neutral QueueJob state machine and deterministic ready-job selection to Core. Jobs transition through Queued, Running, Paused, Completed, Failed and Cancelled. Terminal records cannot restart. A paused or failed record must be explicitly requeued. A previous process's Running record recovers as Paused; app startup does not automatically enable the runner.

Execute one file at a time, with that file retaining its configured bounded worker count. This preserves the existing global request bound while supporting multiple queued files. A single runner semaphore rejects simultaneous runners; a separate state gate serializes mutations. Persist Running before execution and terminal state after the executor has ended all writers. Cancellation is recorded as Paused and stops that runner invocation. Failed jobs do not retry forever; the next ready entry may proceed. The engine's existing bounded request retries remain in force.

Priority ranges from -10 to 10. Add one aging point per five minutes of waiting, then order by creation time and stable ID. This avoids starvation by newly added high-priority work. No active file is preempted or range reassigned by this queue. Concurrent jobs and more elaborate cross-job origin/path fairness remain future work.

Reject duplicate unfinished destination paths using Windows case-insensitive full-path semantics. Limit stored entries, including history, to 1000. Limit each encrypted payload to 1 MiB and validate schedule, state, progress and source/path input. History removal deletes queue records only; partial and complete files are never deleted by these actions.

## Protected persistence

Store one complete encrypted JSON payload per ID in a separate SQLite schema version 1 database at `%LOCALAPPDATA%/MultiInternetDownloadManager/queue.sqlite`. The payload includes URLs (possibly signed), destinations, path choices, worker/checksum settings, priority, schedule, state and terminal progress. Use Windows DPAPI CurrentUser with a versioned purpose/entropy value. There is no plaintext fallback, key embedded in the app, or credential request. Clear serialized plaintext byte buffers after use; in-memory .NET URI/string instances are not secure-memory containers.

Use System.Security.Cryptography.ProtectedData 10.0.15 (Microsoft .NET, MIT) through its Windows guard. Linux tests inject an ephemeral AES-GCM protector only in the test assembly; this does not certify DPAPI. A Windows-only test verifies DPAPI round-trip and tamper rejection in Windows CI. Undecryptable, tampered, unknown-schema or malformed stores are preserved and loading fails instead of clearing or replacing history.

An exclusive `.lock` handle prevents two desktop instances from coordinating the same database. Retain the handle until all initialization, download and state-changing UI commands have finished on shutdown. Process death releases the OS lock; the harmless lock file can remain. SQLite writes use transactions and FULL synchronization. Do not copy encrypted history between Windows users or assume it is recoverable after profile/key loss.

A complete-file rename and queue-state commit are not one cross-file transaction. A crash between them can restore a Paused row for an already finalized destination. The engine refuses to overwrite it; the user must reconcile that history entry. This limitation does not permit reporting incomplete data as complete.

## Scheduling and user flow

Accept an optional ISO 8601 date/time with an explicit offset or Z (for example `2026-11-15T09:00:00+03:30`); reject machine-dependent date formats and implicit offsets. Compare DateTimeOffset instants against an injected TimeProvider for deterministic tests. Preserve the input offset for display.

Add to queue snapshots per-job settings and selected adapter ID/source addresses. Start queue enables processing of ready work and a five-second UI schedule check. The app must remain running; there is no OS wake task, service, recurring schedule, sleep prevention or automatic launch. Stop queue disables scheduled starts and cancels/awaits the active job. Closing also waits for saves and initialization. Reopening the app requires Start queue again.

The WPF list shows filename, state, last accepted/terminal progress, priority, scheduled start and sanitized failure category. Search is by filename; filters cover all, active, completed and failed. Never persist raw exception messages, server text or credentials in history. Display strings remain resource-backed. Queued source addresses are resolved anew before execution; unavailable addresses fail closed. To change a saved job's unavailable path, cancel the entry and add the same original source/destination with a new explicit selection; compatible engine checkpoints can resume.

## Evidence

Core tests verify priority/aging, terminal transitions, future readiness, exact-offset parsing and rejection of ambiguous schedules. Coordinator tests cover ordering, serialization, cancellation completion, duplicate runner/destination rejection, explicit retries, failed-job progression, interrupted-state recovery and record-only history removal. SQLite tests verify encrypted round-trip, cross-instance lease rejection and tamper rejection without deleting records. An actual loopback HTTP download exercises scheduling, protected persistence, engine checkpoints, final hash and restored Completed history. Windows UI execution and native DPAPI remain separately tracked.

Subsequent update: ADR 0017 adds policy-only editing and explicit saved-path replacement for inactive unfinished jobs; cancel/re-add is no longer required for changing a connection selection.
