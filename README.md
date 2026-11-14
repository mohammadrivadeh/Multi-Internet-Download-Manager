# Multi-Internet Download Manager

Independent Windows 10/11 x64 download manager, under active development. The [approved plan](AGENTIC_DEVELOPMENT_PLAN.md) defines the full roadmap; this revision implements an initial functional engine and WPF application, not every planned feature.

## Available now

- Explicit adapter/address selection with isolated Windows HTTP/1.1 pools; system-route mode is an explicit alternative.
- Real range probing, representation checks across selected paths and a bounded lazy range allocator.
- 1–128 workers with measured per-worker range sizing. Faster workers claim new work; active-tail splitting is not yet implemented.
- Validated range responses and offset writes, bounded retry/backoff, header/read deadlines and 429/503 handling.
- Bounded HTTP(S) redirects with loop/downgrade rejection and final-URI identity checks before writing or resuming.
- Pause/cancel, SQLite checkpoints, hash-verified resume and final size/optional authoritative SHA-256 checks.
- Safe sequential fallback when ranges/strong validators are unavailable (restart rather than resume).
- WPF URL/destination form, connection selection, concurrency setting and live progress.
- Persistent queue/history protected with Windows DPAPI; per-job priority, one-time scheduled start, filename search, categories and state/category filters.
- Sequential job processing, explicit requeue of paused/failed jobs and cancellation without deleting data files.
- A per-download KiB/s speed cap shared across all its workers, with zero meaning unlimited.

Windows routing, WPF execution and real-device performance still require acceptance. Development continues without waiting for those results; see [the acceptance register](docs/testing/PENDING-ACCEPTANCE.md). Tray, weekday/DST schedules, media and advanced protocols remain future work in [status](docs/STATUS.md).

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

Enter a direct HTTP(S) URL and a new destination filename. Select desired addresses under Connections, set workers to at least the number of selected addresses, then Add to queue. Repeat for additional files and click Start queue. System-route mode uses OS routing instead of the selected interfaces. The engine follows up to eight validated HTTP(S) redirects; HTTPS downgrades are rejected. No proxy, auth/cookie capture or TLS verification bypass is enabled. Existing destinations are never overwritten. Select a Paused or Failed entry and click Requeue paused / failed, then Start queue, to resume. Each entry retains its own source, destination and connection settings. If the representation changes, preserve the partial file and choose another destination. A stopped sequential job restarts from zero.

Queue records are saved under `%LOCALAPPDATA%/MultiInternetDownloadManager/queue.sqlite`, protected for the current Windows user. A second app instance cannot open the same queue. Source URLs and signed query strings are not stored in plaintext. Keep the original Windows user profile to retain decryption access. Failed decryption preserves the store and disables queue actions.

Optional priority ranges from -10 to 10. A scheduled start must include a date and offset, for example `2026-11-15T09:00:00+03:30`. Start queue enables ready jobs and checks scheduled work every five seconds **while the app is open**. Stop queue disables scheduled starts and pauses the active transfer. Launching the app does not automatically resume work. Optional daily fixed-offset windows can restrict unfinished transfers; weekday/DST rules, OS wake/start tasks and simultaneous file jobs are not yet implemented.

The Download speed limit field is in **KiB/s** (1024 bytes/s); enter zero for unlimited. The limit is captured per queue entry and applies to combined accepted payload across its workers, including sequential fallback. It does not shape OS/socket traffic or count ISP billing usage. Changes to the form affect newly added entries, not an already queued/running job. At low caps, durable progress can update slowly because checkpoints occur after whole ranges complete.

Search and state filters apply to the queue/history table. Cancel pending entry does not delete partial files; clearing Completed/Cancelled history does not delete downloads. A Running job must first stop and finish cancellation before its entry can be cancelled. To correct an unavailable saved network address, stop the runner, edit that job and explicitly replace its saved connections; do not delete valid checkpoints. Queue capacity is 1000 entries including history.

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

## Redirected sources

The engine preserves range headers through a bounded redirect chain and validates the final resource URI, strong ETag and length. Every selected path and range must resolve to the same final resource. Changing that final URI prevents reuse of old redirected checkpoints even if ETag and size match; preserve the partial state and choose another destination. The NetworkProbe remains a single-response diagnostic and intentionally does not follow redirects. Custom engine clients must disable handler redirects/cookies/decompression and have no default headers; per-hop requests use a protocol-header allowlist.

## Scope and source layout

Browser extensions/integration and Site Grabber are **excluded by user request**. They are not future deliverables. The remaining scope and incomplete acceptance are listed in docs/STATUS.md.

The desktop uses explicit application composition, focused shell command modules, separate section views and injected desktop/queue-session adapters. See [desktop architecture](docs/architecture/desktop-structure.md) for the folder map and ownership rules.

## Categories and desktop defaults

Enter a category in the form when adding a job. Use the category filter to narrow history, or Apply category to selected to reclassify a non-running entry. Names contain 1–64 printable characters; existing jobs without a category appear under General. Reclassification never moves or deletes files. Category-based destination suggestions are available through the explicit folder-rule controls.

Save form defaults persists workers, speed cap, priority, category and an optional base folder in DPAPI-protected `preferences.bin` beside `queue.sqlite`. It does not save the source URL, destination, checksum, connections, schedule or queue autorun. Saved queue entries keep their own captured settings. Unreadable settings are preserved and built-in values are used; to reset, close the app and back up/rename the unreadable preferences file before restarting. Defaults cannot be overwritten through the UI after a load failure.

## Live transfer status

The progress bar includes uniquely accepted bytes in active ranges; the existing byte/status/history counts for segmented transfers remain checkpointed bytes. A retry never adds the same range prefix twice. Accepted bytes can reach the total while verification is still running; completion is reported only after finalization. Speed/remaining time are event-based estimates and can be stale during stalls. The selected-job expander shows destination, sanitized origin and expected checksum without exposing signed URL queries. These UI changes are cross-compiled; actual Windows visual acceptance remains pending.

## Recovering queue storage errors

If a terminal queue save fails, the actual outcome stays visible and is retained for retry. Fix disk/access issues and click **Retry queue save** before closing. New queue mutations/run starts also retry pending outcomes first; history cannot silently discard them. Shutdown makes a final save attempt. A crash while storage remains unavailable can still restore the prior persisted row as Paused; do not delete or overwrite an existing finalized destination to force a retry.

A Completed job may show a checkpoint-cleanup notice if its leftover SQLite sidecar could not be deleted. The finalized file is still successful and protected from overwrite. Close the app before inspecting/removing only the identified sidecar; no downloaded data is deleted by this warning.

## Daily transfer windows

Expand Daily transfer window and enter both HH:mm boundaries plus an explicit offset such as +03:30. Blank boundaries mean unrestricted. Overnight windows are supported; equal boundaries are invalid. The started queue waits outside the window, cancels/awaits active work at its end, and requeues unfinished work for a later opening. A completed file is not downloaded again daily. Explicit Stop pauses the queue instead. The app must remain open; no OS wake task or DST adjustment is provided. The full remaining scope is tracked in docs/REMAINING-WORK.md.

## Per-connection speed limits

Each discovered connection has a KiB/s input beside its selection; zero is unlimited. Add to queue captures this cap with that address. Workers using the same selected path share the path cap, while the download cap still limits their aggregate. System-route mode ignores the selected-path inputs. Existing queued jobs are unchanged by later form edits. These limits govern accepted payload, not OS wire traffic, ISP consumption or persistent data quotas.

## Category destination suggestions

Under Destination folder rules, choose an existing base folder and click Suggest category destination. The proposed path is `base/category/URL-filename`; review or edit it before Add to queue. Query strings are excluded. A trailing-slash URL uses download.bin. Invalid Windows names/traversal/encoded separators are rejected rather than silently renamed. Matching generated category folders are created only when adding the validated job; manual destination edits do not trigger automatic folder creation. Linked/reparse folders are unsupported by these automatic rules. The optional base folder can be saved with form defaults; the complete destination filename is not saved as a default.

## Desktop usability and refresh behavior

Drag the divider between the download form and queue to resize panels. The queue shows total/completed job counts and a no-results message for empty filters. Paused/failed rows enable requeue; active/completed rows cannot be cancelled through the pending-entry action. Existing rows update in place so refreshes avoid rebuilding the table. User filtering/sorting can still move or hide rows. The selected details view stays attached to its row.

Speed and ETA become unknown after an idle threshold checked by the five-second timer, and clear when the runner ends. The portable Presentation.Tests suite verifies collection update contracts; real WPF binding, scrolling and accessibility tests still require Windows.

## Editing queued jobs

Select a Queued, Paused or Failed job while the runner is idle, then Edit selected job. Adjust workers, cap, category, priority or scheduling; source, destination and checksum stay fixed. Save job settings preserves state/progress and does not automatically retry a paused/failed job. Cancel edit discards the draft. Queue starts are held while editing.

Saved connection choices are retained even if rediscovery cannot find an address. Check the explicit replacement option only when you want the current selection to replace them. System route is still opt-in. To change the source or destination, create a separate job; this editor does not bypass checkpoint resource validation or refresh an expired URL.
