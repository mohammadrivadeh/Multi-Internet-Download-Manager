# Development status

Date: 2026-11-14. Baseline fetched from origin/main: `30d5fb81f8159ebf63ad74b07abbab62eeedddac` (already current).

## Scope and progress

- Phase 0: started; architecture boundary document, ADR 0001, compatibility matrix and executable byte-range contract added. Existing specification supplies the initial UI wireframe and feature roadmap. Remaining domain contracts will be defined with their consumers.
- Phase 1: initial Windows networking harness implemented (discovery, explicit family/source/interface binding, fresh isolated HTTP/1.1 pool, network-change cancellation, bounded hash sample), CLI, controlled test server, ADR 0002 and Windows results template added. Real Windows evidence remains pending; user will execute final hardware tests. Production long-lived pool/generation management is not implemented.
- Phase 2: independent single-range HTTP/file-write primitive started under the user’s continuation instruction; not integrated with Windows transport. The full downloader, probing, fallback and resume remain pending.
- Phases 3–7: not implemented. Persistence, scheduler, WPF, browser integration and advanced modules remain pending.

## Validation

Executed `dotnet test DownloadManager.sln --configuration Release` using SDK 10.0.404 on Linux x64: **12 passed, 0 failed, 0 skipped**. The Release compilation completed with warnings treated as errors. The runner emitted an informational VSTEST_ARTIFACTS_DIRECTORY fallback message; test discovery and execution completed successfully. Tests cover invalid intervals, single-byte HTTP conversion, offsets above 4 GiB, long.MaxValue endpoint arithmetic, invalid split boundaries, overlap/adjacency, and 2,000 deterministic random splits preserving coverage without overlap.

No Windows interface-routing, reconnect, WPF or installer tests have run. No multi-internet throughput claim is made.

## Networking slice validation (2026-11-14)

- Full solution Release build: 0 warnings, 0 errors (all six projects).
- Core suite: 12 passed, 0 failed, 0 skipped.
- Networking integration suite: 12 passed, 0 failed, 0 skipped. Tests include destination restrictions, interface option encoding, stale/missing snapshots, mutable address isolation, real loopback 200/206/302 exchanges, bounded hashing, pending-connect cancellation and truncated-body rejection. These tests inject a loopback connector and cannot validate Windows binding.
- Live TestServer smoke: readiness, 206, Content-Range, 65536-byte fixture sample/hash and redirect response verified. The temporary server was stopped afterward.
- CLI smoke: discovery JSON parsed (4 candidate addresses), bad command exit 2, Linux binding rejected with exit 1, and error output omitted the URL query.
- Framework-dependent Windows x64 packages produced for NetworkProbe and TestServer. Not executed on Windows. Probe needs .NET 10 x64 runtime; server needs ASP.NET Core 10 x64 runtime (or .NET 10 SDK for both).
- Packaged archive is a local test artifact, not a published release. No system routing, firewall or credentials were modified.

## Next gate

Run the Windows proof procedure and return the results template with selected-interface packet/server evidence. Validate IPv4/IPv6, overlapping tethering subnets, disconnect/reconnect, cancellation and TLS. Resolve any routing defects before accepting Phase 1 and integrating adaptive multi-path behavior. The downloader and WPF UI are still unimplemented.

## Continued development after first push

- First implementation committed and pushed as `c475123` to `origin/work`.
- Added `RangeTransfer.WriteAsync`: known-length, strong-ETag single-range HTTP transfer with response validation before writing, pooled streaming and explicit file offsets.
- Added ADR 0003 describing supplied transport/ownership requirements, partial-write behavior and the absence of durability/completion guarantees.
- Final Release build: 0 warnings, 0 errors. Tests: **45 passed, 0 failed, 0 skipped** (12 Core, 33 integration).
- New tests include valid offset writes, header/identity rejection before writes, short/oversized body rejection, stalled-body cancellation and a sparse-file write above 4 GiB. No Windows or throughput acceptance is implied.
- This code does not yet implement a full-file downloader, retries, sequential fallback, resource probing, crash recovery or multi-path scheduling. Header/connect/read timeout policy belongs to the supplied transport/orchestrator and is not yet integrated here.
