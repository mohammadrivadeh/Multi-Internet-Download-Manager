# ADR 0011: Live accepted bytes, durable progress and transfer estimates

- Date: 2026-11-14
- Status: Accepted; Windows display acceptance pending

Extend DownloadProgress with AcceptedBytes while preserving CompletedBytes as the durable checkpoint count for ranged jobs. Track a maximum accepted prefix per exclusively owned worker range; replaying a range cannot count its prefix twice. Committing a checkpoint moves that range into durable coverage without changing total unique accepted bytes. Reject incomplete commits and excessive ownership. Abort still discards uncertain extents on recovery; live progress never authorizes a checkpoint.

RangeTransfer reports accepted prefixes only after a successful offset write. The engine serializes/throttles live reports and rejects stale snapshots before forwarding them. Durable commit events remain explicit. The UI uses accepted bytes for its bar, but the existing byte/status/history records retain durable counts for segmented jobs. Sequential progress has no resumable durability guarantee, as before.

Compute event-based accepted payload speed and ETA from monotonic elapsed time and byte deltas. Start with an unknown estimate, reset at job/stage/restart boundaries and do not count resumed bytes as new speed. Unknown totals and nonpositive speed yield no ETA. Measurements are not wire-traffic counters or predictions guaranteed under path changes; during no new events estimates can be stale until the next sample. Smoothing, stall heartbeat and richer per-path histories remain future work.

Expose selected filename, destination, sanitized origin (no URL path/query) and expected checksum in a collapsible details area. No signed URL or credential is added to display/history columns. Keep resource labels and the bounded queue viewport. A displayed 100% accepted payload is not completion until verification and finalization finish.

Core tests cover retries, exact unique coverage, durable commits and deterministic rate/ETA baselines. An engine test verifies live progress before checkpoint on a rate-limited single range, then exact final bytes and durable completion. Existing recovery/redirect/queue tests remain required. Windows UI/DPAPI/routing acceptance remains separately tracked.
