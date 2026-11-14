# ADR 0016: Incremental WPF updates and portable presentation tests

- Date: 2026-11-14
- Status: Accepted; actual desktop acceptance pending

Replace immutable/recreated display rows with observable stable row objects keyed by job ID. Reconcile snapshots by updating surviving rows, adding/removing only changed identities and moving rows only when needed. Reject duplicate snapshots before mutations. Avoid ObservableCollection.Clear and unconditional collection-view Refresh during periodic polling. Enable live filtering on the observable Job property; explicit search/filter changes may still refresh the view. Preserve selected row references, details bindings and user-owned sort/filter state. No claim is made that scrolling never changes when the user changes filters/sort or a row leaves the filter.

Reconcile category choices only when the category list changes. Apply live transfer progress to the existing row rather than replacing its collection item. Keep the queue bounded and use row/column virtualization with recycling. Add resizable editor/queue panels, job/completion counts and a no-results overlay. Resume/cancel command availability follows the actual selected state; backend validation remains authoritative.

Check telemetry staleness on the existing five-second UI timer: after at least five seconds without a sample, clear speed/ETA and reset the estimator. Detection can take up to the next timer tick. Clear metrics on runner exit as well. This adds no background polling task or network traffic and does not alter durable progress.

Add tests/Presentation.Tests targeting net10.0. It links the exact production StableCollection implementation, which has no WPF dependencies, so its identity/notification contracts run on Linux and Windows. This suite does not instantiate WPF or certify binding, live filtering, accessibility or rendering. Tests verify update without replacement/Reset, move/add/remove while retaining surviving objects and duplicate rejection before mutation.

The existing Windows UI acceptance register still owns actual selection/scroll, keyboard, DPI, high-contrast and binding checks. App remains composed through injected desktop/session services; no new production dependency was added.
