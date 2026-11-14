# Remaining implementation and acceptance register

The user requested consolidated execution. This register makes scope explicit; it does not certify that unfinished work has been implemented. Browser integration/extensions and Site Grabber are excluded.

| Area | Current delivery | Still required |
| --- | --- | --- |
| HTTP correctness | Validated ranges, redirects, checksums, retries, sequential fallback | Auth/cookies/proxy request context and safe expired-link refresh |
| Scheduler | Lazy disjoint allocation and per-worker range sizing | Generation-fenced active-tail splitting and adaptive concurrency |
| Jobs | Protected queue/history, aging priority, one active file | Resource-bounded simultaneous files and cross-job fairness |
| Scheduling | One-time starts and daily fixed-offset transfer windows | Weekday/time-zone/DST rules and optional OS wake/start integration |
| Connection policy | Explicit selected-path sessions, shared per-download and per-path caps | Usage accounting, persistent quotas and metered policy |
| Desktop | Modular WPF, categories, defaults, live progress/details | Tray, notifications, visual refinement and complete accessibility acceptance |
| Destinations | Explicit no-overwrite destination, category metadata and safe category-folder suggestions | Extension/content-type rules, batch import/export and richer collision choices |
| Recovery | Flushed hashed checkpoints, queue save retry, cleanup notices | Process-kill/power-failure and disk-removal/full tests; large checkpoint optimization |
| Distribution | Portable Windows x64 cross-build and CI workflow | Installer/uninstaller, signing, clean-machine verification and update integrity |
| Protocol/media | HTTP(S) | FTP/FTPS, scoped non-DRM HLS/DASH handling and packaging |
| Advanced desktop | Not implemented | Synchronization, explicit antivirus integration and optional clipboard detection |
| Hardware/runtime acceptance | Linux tests and WPF cross-compilation | Real Windows 10/11 UI, routing, overlapping subnets, reconnect and resource benchmarks |

Implementation continues independently of the external acceptance register. Daily windows and per-path speed caps were delivered in this increment; the other rows remain open. No claim is made that the whole roadmap can be replaced by passing the present unit suite.
