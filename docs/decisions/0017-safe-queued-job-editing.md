# ADR 0017: Safe settings editing for queued work

- Date: 2026-11-14
- Status: Accepted; Windows edit-flow acceptance pending

Add QueueJobSettings as a policy-only update contract. It contains concurrency/cap, category/priority, schedules and explicit path choices, but no job ID, source URL, destination, checksum, creation timestamp, state or progress setters. Applying it preserves those identity/integrity/recovery fields and copies path collections. Only Queued, Paused and Failed jobs are editable; Running and terminal records are rejected.

Coordinator updates run under the existing gate, flush pending terminal writes first, validate the changed record and save it before publishing it to the cache. Save failure leaves the prior record visible and allows a later retry. Saving never requeues paused/failed work or touches data/checkpoint files. A race with a runner starting is resolved by the backend state check, not by trusting UI button state.

The WPF Edit selected action loads the captured settings into an explicit edit mode. Source/destination/checksum are read-only and picker/suggestion commands are disabled. Add is disabled in edit mode; Save settings and Cancel edit are separate actions. Starting the queue and due-job scheduling are held while the edit form is open. Saving disables competing edit actions until persistence ends, and shutdown awaits edit commands.

Retain original saved paths unless the user explicitly selects replacement. Rediscovery may omit an unavailable old address; that omission alone must not erase it or silently fall back to another route. The replacement checkbox captures the current explicit selection; choosing system route remains an explicit policy change. A changed path can resume compatible data because engine representation validation still controls byte reuse. Link refresh/change of resource identity is not provided by this feature.

Tests verify protected identity/checksum/progress/state preservation, copied path collections, rejection of running/completed/cancelled edits, invalid-setting rejection, store-first failed-save behavior and restored settings. Actual edit bindings, command enablement, missing adapters and keyboard flow remain Windows acceptance items.
