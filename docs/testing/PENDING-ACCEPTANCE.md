# Deferred acceptance and external test results

The user explicitly requested on 2026-11-14 that implementation continue without waiting for hardware results. This register is a record, not a development stop gate. Do not mark unexecuted scenarios passed. Add returned observations and regression tests when available.

| Scenario | State | Evidence needed |
| --- | --- | --- |
| Windows 10 22H2 x64 and supported Windows 11 exact builds | Pending user execution | Launch, download, pause/resume and runtime compatibility |
| Independent Wi-Fi + USB phones + Ethernet | Pending user execution | Selected and disallowed adapter packet captures plus controlled-server logs |
| IPv4/IPv6 and overlapping tether subnets | Pending user execution | Correct selected TCP interface, no unsafe fallback |
| Network change and reconnect | Pending user execution | Cancellation, new discovery/session and successful resume |
| Protected queue/profile and tamper regression | Pending Windows execution | Native DPAPI test in Windows CI, cross-profile refusal and preserved unreadable store |
| Categories and saved desktop defaults | Pending Windows desktop execution | Category selection/filter/reclassification, DPAPI defaults reload and unreadable-file preservation |
| Queue UI, close/save races and second instance | Pending Windows desktop execution | Add/requeue/cancel/history filtering, exclusive lease and awaited shutdown |
| Scheduled start, stop and app restart | Pending desktop execution | Explicit offsets, no start while stopped/closed and Paused crash recovery |
| WPF keyboard, screen reader, DPI and high contrast | Pending user execution | Actual desktop accessibility and responsiveness |
| Close window during transfer | Pending user execution | All writers finish before process exit; successful next-run resume |
| Process kill / power failure during flush or SQLite commit | Pending fault injection | Conservative recovery; no false completion |
| Disk full, removed destination drive, access denied | Pending fault injection | Safe stop, no finalized corrupt output |
| Redirects on real HTTPS/CDN endpoints | Pending Windows/TLS execution | Trusted HTTPS chains, downgrade rejection, final-URI consistency and cancellation on selected interfaces |
| Trusted TLS, invalid TLS and captive portal | Pending Windows acceptance | Correct success/failure with certificate validation intact |
| Multi-path throughput and real 16/32/64 request benchmarks | Pending hardware benchmark | Measured traffic and resources; no synthetic aggregation claims |
| Speed caps on real selected interfaces | Pending Windows benchmark | Aggregate payload pacing, low-cap pause responsiveness and distinction from wire/billing traffic |
| Installer, signing and clean-machine distribution | Not implemented | Distribution decision and Windows install/uninstall evidence |

Automated Linux tests cover byte intervals, allocation, HTTP behavior, cancellation, SQLite-based resume, changed/corrupt state, checksums and simulated concurrency. Linux cross-compilation verifies C#/XAML compilation, not desktop execution or Windows routing. `windows-results-template.md` is the initial routing results form.
