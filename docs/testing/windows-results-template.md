# Windows networking test results

- Date:
- Commit / source revision:
- Windows edition and exact build:
- SDK/runtime version and x64 confirmation:
- Topology and adapter aliases (redact identifiers as needed):
- Controlled server address/family and certificate setup:

| Case | Passed / failed / unrun | Evidence and reproduction |
| --- | --- | --- |
| Build and tests | Unrun | |
| Discovery / link-up is not reachability | Unrun | |
| IPv4 each selected physical path | Unrun | |
| IPv6 each selected physical path | Unrun | |
| No disallowed TCP egress (packet capture) | Unrun | |
| Fixture status 206 and sample hash | Unrun | |
| Redirect not followed | Unrun | |
| Stalled response timeout / Ctrl+C | Unrun | |
| Disconnect and reconnect / changed address | Unrun | |
| Overlapping private tethering subnets | Unrun | |
| Trusted HTTPS and rejected invalid certificate | Unrun | |
| Unreachable destination / no family match | Unrun | |
| One baseline per adapter and combined transfer | Unrun | Equal worker count, fixture SHA-256, median goodput and checkpointed bytes per interface |
| Simultaneous traffic on selected interfaces | Unrun | Overlapping time windows in packet captures or controlled-server logs |
| Combined transfer with one adapter disconnected | Unrun | First payload on both interfaces, path retirement, final SHA-256 and surviving-path contribution |

Attach sanitized probe JSON, fixture-info hashes, server observations and packet capture summary. Do not include cookies, auth headers, signed URLs or credentials. Identify limitations independently of failures. This record cannot certify WPF, installation or aggregate throughput.
