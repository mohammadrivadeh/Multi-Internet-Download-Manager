# Deferred acceptance and external test results

The user explicitly requested on 2026-11-14 that implementation continue without waiting for hardware results. This register is a record, not a development stop gate. Do not mark unexecuted scenarios passed. Add returned observations and regression tests when available.

| Scenario | State | Evidence needed |
| --- | --- | --- |
| Windows 10 22H2 x64 and supported Windows 11 exact builds | Pending user execution | Launch, download, pause/resume and runtime compatibility |
| Bound HTTPS range download over one physical IPv4 adapter | Passed on Windows 10 22H2 x64 | [Single-adapter smoke result](#single-adapter-smoke-result) |
| Independent Wi-Fi + USB phones + Ethernet | Pending user execution | Selected and disallowed adapter packet captures plus controlled-server logs |
| IPv4/IPv6 and overlapping tether subnets | Pending user execution | Correct selected TCP interface, no unsafe fallback |
| Network change and reconnect | Pending user execution | Cancellation, new discovery/session and successful resume |
| Protected queue/profile and tamper regression | Pending Windows execution | Native DPAPI test in Windows CI, cross-profile refusal and preserved unreadable store |
| Categories and saved desktop defaults | Pending Windows desktop execution | Category selection/filter/reclassification, DPAPI defaults reload and unreadable-file preservation |
| Queue UI, close/save races and second instance | Pending Windows desktop execution | Add/requeue/cancel/history filtering, exclusive lease and awaited shutdown |
| Scheduled start, stop and app restart | Pending desktop execution | Explicit offsets, no start while stopped/closed and Paused crash recovery |
| Queue edit mode | Pending Windows desktop execution | Identity field locking, explicit connection replacement, unavailable saved addresses, save/cancel and shutdown races |
| New Download WPF layout and command wiring | Automated smoke passed on Windows 10 | Run `dotnet run --project tests/Wpf.Smoke/Wpf.Smoke.csproj -c Debug`. Real WPF views render offscreen. The fixture checks validation, Start, selection, Stop and Resume through controls with an isolated queue and executor. Pointer, modal behavior and display scaling still require desktop acceptance. |
| Incremental table and resizable UI | Pending Windows desktop execution | Stable selection/scroll, live filtering after state/category changes, no-results overlay and resize limits |
| WPF keyboard, screen reader, DPI and high contrast | Pending user execution | Actual desktop accessibility and responsiveness |
| Close window during transfer | Pending user execution | All writers finish before process exit; successful next-run resume |
| Process kill / power failure during flush or SQLite commit | Pending fault injection | Conservative recovery; no false completion |
| Disk full, removed destination drive, access denied | Pending fault injection | Safe stop, no finalized corrupt output |
| Redirects on real HTTPS/CDN endpoints | Pending Windows/TLS execution | Trusted HTTPS chains, downgrade rejection, final-URI consistency and cancellation on selected interfaces |
| Trusted TLS, invalid TLS and captive portal | Pending Windows acceptance | Correct success/failure with certificate validation intact |
| Multi-path throughput and real 16/32/64 request benchmarks | Pending hardware benchmark | Measured traffic and resources; no synthetic aggregation claims |
| Speed caps on real selected interfaces | Pending Windows benchmark | Aggregate payload pacing, low-cap pause responsiveness and distinction from wire/billing traffic |
| Daily window boundaries and per-path caps | Pending Windows execution | End-window cancellation, next-window resume and aggregate/per-path pacing on real interfaces |
| Installer, signing and clean-machine distribution | Not implemented | Distribution decision and Windows install/uninstall evidence |

## Single-adapter smoke result

On 2026-11-20, the Debug download harness completed a bound HTTPS download on one active physical IPv4 adapter under Windows 10 Enterprise x64 build 19045 and SDK 10.0.404. The harness used the production queue executor and the raw GitHub copy of `AGENTIC_DEVELOPMENT_PLAN.md` at revision `0782d2c`. Both resource probes returned HTTP 206 with a range and validator. The path counters showed 43,885 payload bytes, 43,885 checkpointed bytes and zero retries. The finalized file matched the pinned Git blob. Its SHA-256 was `BC9C201DF515EB84E5B5DF71B39EC496986A08B526730755C67D8E228E6FFDC8`.

This machine exposed one active physical adapter. Packet capture, multiple independent uplinks, IPv6, elevated capture and route recovery remain separate acceptance cases.

Automated Linux tests cover byte intervals, allocation, HTTP behavior, cancellation, SQLite-based resume, changed/corrupt state, checksums and simulated concurrency. Linux cross-compilation verifies C#/XAML compilation, not desktop execution or Windows routing. `windows-results-template.md` is the initial routing results form.
