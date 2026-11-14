# Multi-Internet Download Manager

Independent Windows 10/11 x64 download manager, under active development. The [approved plan](AGENTIC_DEVELOPMENT_PLAN.md) defines the full roadmap; this revision implements an initial functional engine and WPF application, not every planned feature.

## Available now

- Explicit adapter/address selection with isolated Windows HTTP/1.1 pools; system-route mode is an explicit alternative.
- Real range probing, representation checks across selected paths and a bounded lazy range allocator.
- 1–128 workers with measured per-worker range sizing. Faster workers claim new work; active-tail splitting is not yet implemented.
- Validated range responses and offset writes, bounded retry/backoff, header/read deadlines and 429/503 handling.
- Pause/cancel, SQLite checkpoints, hash-verified resume and final size/optional authoritative SHA-256 checks.
- Safe sequential fallback when ranges/strong validators are unavailable (restart rather than resume).
- WPF URL/destination form, connection selection, concurrency setting, progress, start/resume and pause.

Windows routing, WPF execution and real-device performance still require acceptance. Development continues without waiting for those results; see [the acceptance register](docs/testing/PENDING-ACCEPTANCE.md). Queues/history, tray, schedules, browser capture, media and advanced protocols remain future work in [status](docs/STATUS.md).

## Build and test

Install SDK 10.0.404 (`global.json`), then from the repository root:

```sh
dotnet restore DownloadManager.sln
dotnet build DownloadManager.sln -c Release --no-restore
dotnet test DownloadManager.sln -c Release --no-build
```

Linux can compile the WPF application using Windows targeting packs and run engine tests. It cannot run WPF or certify interface binding. In the prepared cloud machine export `DOTNET_ROOT=/workspace/.dotnet`, prepend it to `PATH`, set `DOTNET_CLI_HOME=/workspace/.dotnet-cli` and `NUGET_PACKAGES=/workspace/.nuget/packages`. Use approved elevated execution for specific .NET commands if the sandbox blocks named mutexes or loopback test listeners.

## Run the Windows desktop app

```powershell
dotnet run --project application/App.Wpf -c Release
```

Enter a direct HTTP(S) URL and a new destination filename. Select desired addresses under Connections, set workers to at least the number of selected addresses, then Start / Resume. System-route mode uses OS routing instead of the selected interfaces. No proxy, automatic redirect, auth/cookie capture or TLS verification bypass is enabled. Existing destinations are never overwritten. Use the original URL and destination to resume a segmented job. If the representation changes, preserve the partial file and choose another destination. A stopped sequential job restarts from zero.

To distribute a framework-dependent test build:

```powershell
dotnet publish application/App.Wpf/App.Wpf.csproj -c Release -r win-x64 --self-contained false -o artifacts/desktop
```

The target machine needs the .NET 10 **Desktop Runtime x64**, not just the base .NET runtime. This is a portable test build; an installer/signing strategy is not implemented.

## Controlled end-to-end test (Linux or Windows)

Terminal 1:

```sh
dotnet run --project tests/TestServer -c Release -- --urls http://127.0.0.1:5099
```

Terminal 2 (choose a new destination path):

```sh
dotnet run --project tests/DownloadHarness -c Release -- system http://127.0.0.1:5099/fixture ./fixture.bin 16
```

Compare the file SHA-256 with `/fixture-info`. This verifies the engine using the system route, not multi-interface routing. For Windows routing tests, use [the dedicated procedure](docs/architecture/windows-network-proof.md) and results template.

## Recovery and boundaries

A segmented job uses `destination.part` plus `destination.part.sqlite` (and SQLite WAL/SHM files while active). Do not edit or delete these during a job. Each completed range is flushed before its metadata transaction; resume hashes saved bytes and redownloads damaged extents. An uncertain failed extent is never marked complete. Authoritative checksum mismatch prevents finalization. No plaintext source URL or credentials are stored in checkpoint metadata.

The Core stays independent of WPF, SQLite, Windows interop and concrete HTTP. Production projects live in `application/`. Decisions, limitations and validation evidence are recorded under `docs/`.
