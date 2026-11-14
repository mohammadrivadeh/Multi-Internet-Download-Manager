# ADR 0005: First functional WPF slice and deferred acceptance

- Date: 2026-11-14
- Status: Accepted; Windows desktop execution pending

Use net10.0-windows WPF with CommunityToolkit.Mvvm 8.4.0 (MIT), three production projects and a test-only console harness. This is the first functional desktop slice, not final IDM feature parity or visual acceptance. No extra UI library or embedded browser is needed for this slice.

Expose URL, explicit destination, per-address interface selection, explicit system-route opt-in, bounded worker count, optional SHA-256, start/resume and pause. Run engine disk/hash/SQLite work off the dispatcher. Throttle progress before dispatch to at most ten updates per second plus stage changes. Close cancels and awaits the current job; repeated close events cannot terminate a writer early. No tray/background-service behavior is implemented.

Keep user-visible strings in a resource dictionary. Use system foreground/background resources for basic high-contrast compatibility and labels/automation names for form fields. No animation is required for correctness. DPI, keyboard and assistive-technology testing remain pending on Windows.

Create one isolated NetworkPathSession per selected adapter/address, validate snapshots before each request/connect, and link invalidation tokens into the complete engine lifetime. Address or availability changes conservatively cancel the operation. Dispose every session only after writers end; resuming constructs fresh pools. No fallback to a disallowed interface is introduced. System-route mode is an explicit user choice and does not claim independent-interface use. Worker count must be at least the selected transport count.

The user explicitly authorized continued development without waiting for their final tests. Maintain docs/testing/PENDING-ACCEPTANCE.md for external evidence, and continue independent implementation. Do not present cross-compilation, mocks or localhost traffic as real Windows acceptance.

Queues, history, settings persistence, resource refresh/authentication, clipboard, browser integration, media, FTP and site-grabbing are not provided by this first shell. They remain separately tracked development work, not implied by a successful app build.
