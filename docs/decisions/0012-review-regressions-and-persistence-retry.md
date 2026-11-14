# ADR 0012: Terminal queue persistence, cleanup outcomes and ETA regressions

- Date: 2026-11-14
- Status: Accepted; real OS failure acceptance pending

Resolve four issues identified in the fd1c177 review.

After the executor has fully ended, publish its true terminal outcome in the coordinator cache and retain an immutable pending write until persistence succeeds. A save failure still stops the runner and surfaces a queue-storage error, but no ghost Running entry remains. All subsequent mutations/runner starts first retry pending writes; failure blocks destructive history clearing and new execution. Retry queue save exposes an explicit recovery action that never repeats the executor. Shutdown attempts one final save after all commands/writers end. Pending outcomes are in memory until committed: process death before a successful retry can still recover the old persisted Running row as Paused. The separate finalized-file/queue-state transaction limitation remains; do not overwrite existing files to reconcile it.

After successful same-volume rename, sidecar deletion IO/access failures are cleanup warnings, not failed downloads. Emit and persist CheckpointCleanupPending, display an Issue notice with Completed state and retain the sidecar. Never automatically delete unknown files or restart an already finalized download. The normal no-overwrite destination policy remains in force. Fault tests inject the cleanup operation through an internal constructor; production still calls File.Delete.

Classify HttpIOException and EndOfStreamException as Network before the broad IOException Storage branch. Do not persist exception messages. Keep the prior payload-rate estimate during sub-250ms samples and recompute ETA from current remaining bytes; unknown totals/zero speed remain unknown. This addresses UI flicker, not the separately deferred stall heartbeat or smoothing work.

Regression tests inject terminal persistence failure for Completed, Failed and Paused outcomes, verify explicit retry and no repeated execution, and prevent premature history removal. Additional tests cover network error categories, successful finalization despite cleanup failure with exact fixture bytes, destination protection, and stable ETA between rate samples. Linux injection tests do not replace Windows file-sharing, disk-full or power-failure acceptance.
