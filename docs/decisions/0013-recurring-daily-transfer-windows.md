# ADR 0013: Recurring daily transfer windows

- Date: 2026-11-14
- Status: Accepted; Windows timer/close acceptance pending

Add an optional per-job daily transfer window with HH:mm start/end and an explicit fixed UTC offset. Start is inclusive, end exclusive, equal endpoints invalid, and overnight windows supported. Null means unrestricted. Preserve this policy inside the existing encrypted job payload; older records remain unrestricted. Do not save a window as a default for new work.

Ready selection requires both NotBefore and an open daily window. The queued executor links a deadline for the window's remaining duration into the existing engine cancellation lifetime. It waits for the engine/all writers to end before reporting window closure. The coordinator requeues this outcome, persists it and ends the runner invocation, avoiding a tight retry loop even if its clock has not advanced. The started desktop queue can resume it on a later five-second scheduling tick when the window opens. Explicit user cancellation still pauses and disables auto-run. Completed files are never repeated or overwritten each day.

This is a recurring permission window for unfinished work, not a recurring duplicate-file downloader. Sequential fallback restarts from zero after a boundary cancellation. There is no OS wake task, closed-app execution, weekday selector or automatic DST adjustment. Explicit offsets avoid host-locale ambiguity; time-zone-ID/DST scheduling remains open. Other selected-path invalidations remain ordinary pauses, not automatic window requeues.

Core tests cover explicit offsets, open/close boundaries, overnight duration and invalid/empty input. A coordinator test covers queued window closure, no busy loop, no out-of-window execution and later completion. Actual window-deadline cancellation on Windows, sleep/wake and clock adjustment remain acceptance cases.
