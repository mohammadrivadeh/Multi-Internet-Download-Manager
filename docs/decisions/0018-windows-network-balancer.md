# Pivot to a Windows network balancer

The product is moving from scheduling HTTP byte ranges to distributing new system-wide TCP and UDP flows across Windows uplinks. Each flow stays on its chosen uplink for its lifetime. Aggregate throughput comes from concurrent flows. A single TCP flow uses one link.

## Source and architecture

Use [net-merge at revision 2b333a3](https://github.com/net-merge/net-merge/tree/2b333a3ad820996ba337588adfa8f7259a45baec) as the Windows data-plane source. Keep Wintun, the Go userspace stack, and the flow relay together in a Windows service adapted from that source. Keep the WPF process unelevated and give it a versioned local control channel for status, adapter selection, and start and stop. During migration, keep the existing downloader and its queue available until the replacement passes the Windows acceptance tests.

The existing `WindowsPathConnector` confirms the Windows socket option and byte-order conventions. It resolves an HTTP hostname and binds a TCP socket. The service needs destination IPs from captured packets, both TCP and UDP, and independent IPv4 and IPv6 eligibility. Its outbound sockets and probes must bind the selected source address and interface so traffic does not re-enter Wintun.

Review [the upstream MIT license](https://github.com/net-merge/net-merge/blob/2b333a3ad820996ba337588adfa8f7259a45baec/LICENSE) and retain its copyright and permission notice with vendored or adapted source and binaries. Review distribution terms and third-party notices for Wintun, tun2socks, and gVisor independently. Pin the upstream revision, Go toolchain, module checksums, and architecture-specific signed Wintun artifacts in the service build.

## Milestones

1. **Flow assignment (implemented).** The standalone .NET scheduler exercises the policy derived from [upstream `sched.go`](https://github.com/net-merge/net-merge/blob/2b333a3ad820996ba337588adfa8f7259a45baec/internal/engine/sched.go). It reserves a path using (open flows + 1) divided by weight and tracks load per adapter GUID and address family. This milestone uses positive integer weights, stable tie order, and excludes known-unreachable paths even when all probes fail. Unknown paths are eligible when no reachable path is available. The .NET scheduler is a testable policy reference. The Go service should use one scheduler in the data plane, with shared test vectors checking policy parity before integration.
2. **Source integration and discovery (implemented).** The Windows build produces the upstream CLI, a standalone diagnostics helper, and an optional desktop capture host. The WPF app opens on Network Balancer, with Downloads and Queue accessible in the same shell. The inventory displays discovery eligibility. It does not imply tested Internet reachability.
3. **Desktop capture preview (implemented, awaiting VM acceptance).** Start opens a single elevated host with UAC. The desktop stays unelevated and hosts a local named pipe restricted to the signed-in SID. Both processes verify the peer PID before the desktop sends authorization. The host reports readiness after TUN setup. Stop, window exit, and pipe loss request teardown without terminating the host. A clean stop is acknowledged after the capture adapter disappears. UAC with another Windows account cannot connect through the current-user pipe. The host uses its own adapter name, Wintun GUID, and singleton. It preflights weak-host send and forwarding and makes no changes to physical adapter settings. It stops if eligible adapters, addresses, or required interface settings change. Normal builds package only diagnostics. Preview builds are for an isolated Windows VM.
4. **Owned-route recovery (implemented for the preview, awaiting VM acceptance).** Before adding each route, the host records its intent and flushes the machine registry journal. The key is owned by the Administrators group with a protected DACL that grants access to Administrators and SYSTEM. The journal accepts only the four split-default prefixes for the preview's Wintun GUID. The host resolves that GUID to the current Windows interface LUID and checks the prefix, on-link next hop, management protocol, and session-specific route metric before deleting a route. It verifies removal before clearing the intent. Unreadable journals, conflicting routes, and cleanup failures prevent a new session. A surviving adapter still blocks startup after route reconciliation. Windows uses LUIDs, rather than mutable interface indices, for the route lookup. [Windows IP route lookup](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipforwardentry2)
5. **Production deployment (pending).** Recovery runs on the next authorized Start and during normal teardown. No service or boot-time recovery task is installed. Continue to require the opt-in preview build until VM acceptance verifies live routing, behavior after forced host termination and reboot, and clean stop. Flow counts, reachability, and throughput display are later work.

## Build and inspect

From the repository root on Windows x64, with Git and Go 1.27.1 on PATH:

```powershell
& ./application/NetworkBalancer/Upstream/build-windows.ps1 -Test
dotnet build DownloadManager.sln
```

The build script exports the pinned Git tree, tests with the checked-in module checksums, and writes the CLI, diagnostics helper, and notices under `artifacts/net-merge/windows-amd64`. Pass `-SourceRepository` to reuse a local clone and `-Go` to select a Go executable. The helper can be run directly to inspect the version-1 JSON inventory.

To build the isolated VM preview:

```powershell
& ./application/NetworkBalancer/Upstream/build-windows.ps1 -Test -IncludeWintun
dotnet build application/App.Wpf/App.Wpf.csproj -p:EnableCapturePreview=true -o artifacts/capture-preview
```

`-IncludeWintun` stages the adapted host, signed Wintun DLL, and Wintun license under `artifacts/net-merge/desktop-capture`. An optional `-WintunArchive` uses a local archive with the same checksum and signature verification. Building the package does not install the driver or activate capture. Run the preview in an isolated VM from a deployment directory writable only by administrators.

The standalone upstream CLI remains a development artifact. The desktop invokes the adapted host, which uses Windows IP Helper APIs for its four capture routes. The upstream virtual adapter address and MTU setup still runs against the preview interface after its GUID is verified. Route setup failure triggers reconciliation and normal teardown attempts reconciliation before closing the adapter. An externally replaced route with a different fingerprint is preserved. Windows does not provide an atomic delete conditioned on that fingerprint, so the preview assumes no independent administrator concurrently modifies its dedicated adapter. Forced host death and reboot behavior remain unverified on real routes.

## Windows acceptance

The upstream [Windows TUN setup](https://github.com/net-merge/net-merge/blob/2b333a3ad820996ba337588adfa8f7259a45baec/internal/tun/tun_windows.go) installs split default routes and adjusts weak-host send and forwarding on eligible interfaces. Its recovery file is written after those changes and records interface indices. The desktop adaptation disables these physical-interface mutations and the upstream startup cleanup hook. It refuses to start if an eligible adapter requires changes or the capture adapter is already present. Before supporting Internet Connection Sharing or Mobile Hotspot on such adapters, journal original values by stable identity and test restoration across process death and adapter reconnect.

On a VM with two separate uplinks, confirm that simultaneous TCP and UDP flows use both links and individual flows keep their egress. Test IPv4, IPv6, DNS, a failed uplink, cable removal and reinsertion, resume, and coexistence with local routes and VPNs. Force failure after each startup mutation, kill the host with capture active, and reboot. Check connectivity immediately, then check recovery on the next authorized Start. Verify owned routes are removed and pre-existing routes and adapter settings survive. These live tests are pending.

For the WPF preview, also verify that denying UAC leaves routes intact, a second host cannot start, Stop restores direct connectivity, and closing or terminating the desktop stops the host. Check that WPF reports an unexpected host exit as unconfirmed cleanup. Capture route and adapter snapshots before and after each case.
