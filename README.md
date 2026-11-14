# Multi-Internet Download Manager

Windows 10/11 x64 download manager under development. The approved requirements are in [the development plan](AGENTIC_DEVELOPMENT_PLAN.md).

## Current implementation

Phase 0 foundation and an initial Phase 1 Windows network probe: interface discovery, explicit source/interface selection, bounded HTTP sampling and a controlled fixture server. An independent single-range transfer primitive now validates HTTP responses and writes directly at the requested file offset (ADR 0003). There is no complete downloader or WPF application yet. Windows hardware acceptance is pending. See the Windows proof procedure below for commands and a results template.

## Build and test

Install SDK 10.0.404 (see global.json), then from the repository root:

```sh
dotnet restore DownloadManager.sln
dotnet build DownloadManager.sln --configuration Release --no-restore
dotnet test DownloadManager.sln --configuration Release --no-build
```

In the prepared cloud environment, first set `DOTNET_ROOT=/workspace/.dotnet`, prepend it to `PATH`, set `DOTNET_CLI_HOME=/workspace/.dotnet-cli` and `NUGET_PACKAGES=/workspace/.nuget/packages`. If the sandbox blocks the NuGet-Migrations named mutex, run the specific .NET command with approved elevated execution.

Production projects belong under `application/`. Core must remain independent of WPF, Windows interop, SQLite and concrete HTTP transports. Infrastructure contains the networking probe; App.Wpf will be added with its first vertical slice.

See [status and evidence](docs/STATUS.md), [architecture](docs/architecture/baseline.md), [decisions](docs/decisions/0001-foundation.md), and [Windows proof procedure](docs/architecture/windows-network-proof.md).
