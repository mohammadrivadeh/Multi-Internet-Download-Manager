# Network Balancer and download reliability plan

The goal is reliable use of distinct Windows Internet connections, faster completion when the origin supports safe byte ranges, and recoverable start, pause, resume and shutdown. The [net-merge design record](decisions/0018-windows-network-balancer.md) covers the optional system-wide host. The [selected path recovery decision](decisions/0019-selected-path-recovery.md) covers the download engine.

| Priority | Work | Acceptance |
| --- | --- | --- |
| P0 | Restore a clean WPF Debug build on the repository SDK. | Build the whole solution and run the existing suites with the new regression tests. |
| P0 | Keep range owners independent when a selected adapter stops responding. | Two controlled paths transfer disjoint ranges concurrently. An interrupted range is replayed by a surviving path, final checksum matches, and representation mismatches still fail safely. |
| P0 | Capture each IP family only when its selected egress and TUN configuration support it. | Cover IPv4-only, dual-stack, egress removal and route cleanup. Check Windows route snapshots before and after in an isolated environment. |
| P0 | Verify the queue and modal flows. | Start and resume the requested job within its queue rules, pause without affecting siblings, reopen the queue, and verify persisted range recovery. Check keyboard, focus and field layout at different DPI settings. |
| P1 | Correlate retry, reconnect, path contribution and completion in local logs. | Match a job identifier across network and queue events. Verify logs omit URLs, credentials, and local download paths, and distinguish retry payload from accepted progress. |
| P1 | Measure actual improvement on physical Windows uplinks. | Use the same range-capable origin and expected SHA-256 for each single-path run and the combined run. Record median time, useful bytes/sec, bytes per interface, adapter loss and reconnect behavior. Explain shared upstream or server bottlenecks against the measurements. |
| P1 | Validate capture lifecycle in a Windows VM with two networks. | Test IPv4/IPv6, DNS, TCP/UDP, sleep and reconnect, UAC denial, Stop, forced host exit and reboot. Verify unrelated routes remain intact and recovery is observable. |
| P2 | Expose per-download path contribution and clearer recovery errors. | Display usable egress, activity, selected path loss and recovery in the UI using the verified runtime state. Test compact layout and accessibility. |
| P2 | Review queue-wide capacity and speed controls. | Measure contention with simultaneous large and small jobs. Keep per-path caps and queue schedules effective during failover. |

Build, integration tests and loopback fixtures establish implementation behavior. Record physical routing and performance in the [Windows results template](testing/windows-results-template.md).

For a pause and resume investigation, filter the local log by the job identifier. Compare `download-start`, `download-path-sample` and `download-state-saved` across attempts. A `download-state-save-failed` event identifies a failed write. The queue retries that write before subsequent operations and records a successful save when it becomes durable. The scheduled loopback download test checks the persisted pause and completion events and verifies that the job log omits the signed URL token and destination.
