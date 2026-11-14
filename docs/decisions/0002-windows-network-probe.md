# ADR 0002: Explicit Windows interface probe and DNS boundary

- Date: 2026-11-14
- Status: Accepted for experimental harness; Windows routing proof pending
- Scope: Phase 1

## Decision

Use NetworkInterface discovery, stable adapter ID, per-family index and unicast source. Each discovered snapshot has a configuration fingerprint over ID, indices and addresses; it is not a monotonic reconnect counter. Adapter up means only a candidate, never internet readiness. Own copies of mutable IPAddress objects.

Use a fresh SocketsHttpHandler for each bounded probe with HTTP/1.1 exact, no proxy, cookies, redirects or decompression. The ConnectCallback resolves the original hostname through OS DNS, filters to the selected family, and tries at most 16 matching candidates. Each new socket sets Windows IP_UNICAST_IF/IPV6_UNICAST_IF (SDK option 31), binds the selected source and connects. IPv4's option value uses network byte order; IPv6 uses host byte order. The original request hostname remains available for Host and TLS SNI; default certificate checks remain enabled.

Fail closed on unsupported OS, missing/changed snapshots or failed binding. There is no default-interface or cross-family fallback. Any address/availability event cancels the active probe. Each next invocation discovers anew and creates a fresh pool, so no connection survives between probe runs. Checks before the request and before/after connect reject stale snapshots. A long-lived pool manager and monotonic generations for production leases are deferred.

DNS uses the system resolver and can travel on a different interface. The harness proves only the selected TCP/HTTP path, not DNS isolation or VPN/filter-driver behavior. Per-path DNS remains an open production decision that needs a separate experiment.

Set an 8-second connection timeout and 20-second whole-probe deadline; these are probe budgets, not future download timeouts. Request bytes 0–65535 and read at most 65536 response bytes even if Range is ignored. Report status, protocol and sample hash, with no assertion of range correctness, full-file integrity or throughput. A sample-limit flag means the limit was reached, not that the response was truncated. Redirects are reported without following them. Probe failures log only exception type to avoid leaking URLs, credentials or remote text.

## Consequences and evidence

Proxy-dependent networks may fail, deliberately: using the proxy would invalidate the interface experiment. Loopback integration tests exercise HTTP framing, sample bounds, hashing, redirects and cancellation via a test connector; they do not execute Windows socket binding. The production callback rejects Linux.

The controlled fixture server uses only ASP.NET Core shared-framework APIs and deterministic bytes. It serves synthetic data only; it is not production application infrastructure. No third-party production packages are added.

## References

- https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options (IP_UNICAST_IF)
- https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ipv6-socket-options (IPV6_UNICAST_IF)
- https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback
