# Architecture baseline

The approved specification remains the requirements source. This document maps it to implementation boundaries.

| Boundary | Responsibility | Current state |
| --- | --- | --- |
| Core | Pure domain contracts, range ownership, scheduler policy | ByteRange, range allocator, download options/progress and queue state/scheduling implemented; fenced active leases planned |
| Infrastructure | HTTP, interface discovery/binding, disk, SQLite | Discovery, selected-path pools, HTTP engine, file writes, SQLite checkpoints, protected queue store and coordinator implemented; Windows acceptance pending |
| App.Wpf | Composition, MVVM, resource-backed English UI | Functional form, queue/history, search/filter and one-time start scheduling implemented; Windows desktop acceptance pending |
| Tests | Core invariants, controlled HTTP behavior, Windows acceptance | Core, HTTP and SQLite recovery tests, fixture server and CLI harnesses implemented; Windows acceptance pending |

## Contract rules

`ByteRange` owns `[Start, EndExclusive)` with `0 <= Start < EndExclusive <= long.MaxValue`. Splits preserve exact coverage and cannot introduce an empty interval. HTTP receives `(Start, EndExclusive - 1)`.

Next contracts are NetworkPath (stable identity distinct from address/index/generation), RangeLease (exclusive writable extent and fencing generation), and RemoteResource (representation validators and range capability). Define them alongside their first consumer rather than creating speculative model classes. Core exposes no concrete HttpClient, SQLite or WPF types.

A locked allocator now serializes each download’s pending allocations. A worker retains its extent until completion or failure, and all writers are awaited before retrying the job. Durable progress follows file flush then a SQLite transaction. Generation-fenced live-tail reassignment is not implemented; no active extent is handed to another worker.

## Compatibility matrix

| Target | Intended role | Evidence |
| --- | --- | --- |
| Linux x64 / .NET 10 | Domain, HTTP, SQLite development and WPF cross-compilation | Automated tests and end-to-end HTTP fixture verified; Windows binary produced |
| Windows 10 22H2 x64 | Initial product acceptance | Pending exact build and current runtime support verification |
| Supported Windows 11 x64 | Initial product acceptance | Pending exact build and current runtime support verification |
| ARM64 / x86 | Outside baseline | Unverified |

The existing specification section 14 supplies the initial wireframe and add-download flow. Theme library selection, installer format and numerical performance limits remain open until measured.
