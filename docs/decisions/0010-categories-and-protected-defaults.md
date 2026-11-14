# ADR 0010: Categories and protected desktop defaults

- Date: 2026-11-14
- Status: Accepted; Windows UI/DPAPI acceptance pending

Add a printable 1–64 character category to queue jobs, trimming input and retaining case for display. Old payloads default to General without a queue schema migration. Filtering is case-insensitive. Reclassification is serialized by the coordinator, persisted before updating the cache, and forbidden for Running jobs so terminal writes cannot overwrite concurrent edits. It changes metadata only, never destination paths or existing files. Automatic destination rules and category-managed folders remain future work.

Persist explicit form defaults for workers, speed cap, priority and category only. Do not retain URL, checksum, destination, selected interface, system-route opt-in, start time or queue autorun as defaults. Each queued job remains a snapshot of its original settings; saving defaults does not modify active or queued work.

Store a versioned, typed encrypted envelope in preferences.bin beside the protected queue. Reuse the Windows profile protector abstraction, with no plaintext fallback. Validate before writes, write to a unique same-directory temporary file, flush data then atomically replace the target. Clean up only the temporary file created by that save. Reject oversized, malformed, unsupported-version or undecryptable content. If loading preferences fails, the desktop uses built-in defaults and disables overwriting through Save defaults; preserve the file for recovery. The user can back it up/rename it while the app is closed and restart to create new defaults.

The existing queue-session lease excludes another desktop instance; shutdown awaits save and reclassification commands before releasing that lease. The category filter operates over existing queue snapshots; it does not rescan files or inspect downloaded content. Source URLs remain absent from the history table's visible columns.

Tests verify legacy category defaults, normalization/bounds, validation, protected category round-trip, coordinator reclassification/state preservation and active-job refusal. Preference tests verify missing-file defaults, encrypted round-trip, preservation on encryption failure, absence of leftover temporary files and tamper rejection without deleting content. Portable tests use the existing test-only AES-GCM protector; native Windows DPAPI and desktop bindings are not certified by these tests.
