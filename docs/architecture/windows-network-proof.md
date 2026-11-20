# Phase 1: independent egress proof procedure

The discovery, probe and download harnesses are implemented. Independent egress and speedup still require measurements on Windows 10/11 x64 with independently connected interfaces.

## Harness requirements

Implement explicit interface enumeration and an opt-in destination probe before the full UI. Identify stable adapter ID, current index, source address and generation. Link-up is not internet readiness. Use isolated HTTP/1.1 transport pools per generation and session; retire pools on reconnect. Apply the correct IPv4/IPv6 outgoing-interface socket option and source binding. Never fall back to another interface on failure.

DNS initially needs an explicit documented decision: OS lookup is not per-interface DNS. Preserve the original hostname for Host, SNI and certificate validation. Verify same-family destination candidates only, bounded timeouts/cancellation, and fail closed if the selected family/path is unavailable. Do not accept signed URLs or auth headers in diagnostic logs.

## Required evidence

1. Record Windows build, SDK/runtime, adapter identities and topology without credentials.
2. Use a controlled server and independently inspect packet captures/server observations for each path; public egress IP alone is insufficient.
3. Validate IPv4 and IPv6 separately and confirm no disallowed-interface traffic on failure.
4. Repeat with two USB phones plus Wi-Fi, overlapping private subnets, address changes and reconnects.
5. Confirm old-generation pools are retired and new requests use only current addresses.
6. Check TLS failures, unreachable destinations, cancellation and captive portal behavior without weakening certificate verification.
7. Store sanitized results with exact commands and passed/failed/unrun cases. Close the phase only when independent path use is proven.

See ADR 0002 for the implemented Windows option representation, OS DNS boundary and cancellation policy. Production per-path DNS and long-lived generation/pool management remain open.

## Run the harness on Windows (PowerShell, repository root)

Install the pinned .NET 10 SDK, then:

```powershell
dotnet build DownloadManager.sln -c Release
dotnet test DownloadManager.sln -c Release --no-build
dotnet run --project tests/NetworkProbe -c Release --no-build -- list
dotnet run --project tests/NetworkProbe -c Release --no-build -- probe "ADAPTER-ID-FROM-LIST" "SOURCE-IP-FROM-LIST" "http://CONTROLLED-SERVER:5099/fixture"
```

Run a fresh probe for each selected address. IPv6 link-local addresses must retain their scope suffix. The destination must be reachable through that interface; do not use loopback to claim physical-interface proof. No automatic public speed test is performed. Use unsigned, credential-free test URLs: command-line arguments can be visible in process listings and shell history. Do not share private addresses or identifiers publicly without redaction.

The separately packaged Windows x64 probe is framework-dependent: install the .NET 10 x64 runtime, unzip, then run `./NetworkProbe.exe list` or `./NetworkProbe.exe probe ...`. Packaging on Linux is not a Windows execution test.

## Controlled server

On a machine reachable through the selected networks, with .NET 10 SDK:

```powershell
dotnet run --project tests/TestServer -c Release -- --urls http://0.0.0.0:5099
```

Choose firewall exposure yourself for the test topology and stop with Ctrl+C afterward. The server exposes synthetic data only and provides no authentication. `/fixture-info` returns the expected sample and full SHA-256 hashes. `/fixture` supports HTTP ranges; compare the probe sample hash and expect status 206 and 65536 sample bytes. `/redirect` must return 302 and exit 1 without following. `/stall` must cancel within the probe budget. HTTP tests do not prove TLS; also test a controlled HTTPS endpoint with a normally trusted certificate. Never disable certificate validation.

Disconnect/reconnect an adapter during `/stall`: expect cancellation; rediscover afterward and probe using the new address/index. Each invocation discards its pool; unchanged configuration can produce the same fingerprint after reconnect. Capture traffic on both selected and disallowed adapters, and compare with server observations to verify real routing. DNS may use the OS default route, as recorded in ADR 0002.

## Results to return

Copy `docs/testing/windows-results-template.md` to a new local results file, fill exact OS builds/topology, commands, sanitized output and packet/server observations, and retain failed/unrun cases. Two separate probes establish individual path selection. Use a combined download to measure simultaneous use.

## Compare single-path and combined downloads

Use a controlled origin reachable over both selected Internet connections. Start it with a fixture large enough for multiple ranges. The example serves 64 MiB and retains the same bytes and strong ETag for every trial using that size.

```powershell
dotnet run --project tests/TestServer -c Debug -- --urls http://0.0.0.0:5099 --fixture-bytes 67108864
```

From the Windows client, list the adapter IDs and source addresses with `NetworkProbe list`. Keep the same origin and total worker count for each trial. Use fresh destination names and repeat the three trials to compare median times.

```powershell
$origin = 'http://CONTROLLED-SERVER:5099'
$source = "$origin/fixture"
$adapterA = 'FIRST-ADAPTER-ID'
$addressA = 'FIRST-SOURCE-ADDRESS'
$adapterB = 'SECOND-ADAPTER-ID'
$addressB = 'SECOND-SOURCE-ADDRESS'
(Invoke-RestMethod "$origin/fixture-info").fullSha256
dotnet run --project tests/DownloadHarness -c Debug -- paths $source ./only-a.bin 8 $adapterA $addressA
dotnet run --project tests/DownloadHarness -c Debug -- paths $source ./only-b.bin 8 $adapterB $addressB
dotnet run --project tests/DownloadHarness -c Debug -- paths $source ./both.bin 8 $adapterA $addressA $adapterB $addressB
```

Compare each reported SHA-256 to the fixture hash. Match the printed job ID with per-interface byte counts and failover events in the diagnostics log. Confirm concurrent traffic on the two selected interfaces using packet capture or server timing, and record whether the combined download beat the faster single path. Repeat the combined trial while disconnecting one test adapter after both have carried payload. Verify the surviving path finishes the download and that the final hash still matches. A loopback transfer verifies HTTP and file handling. Actual adapter routing and speed measurements require the physical interfaces.
