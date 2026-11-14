# Architecture baseline

The approved specification remains the requirements source. This document maps it to implementation boundaries.

| Boundary | Responsibility | Current state |
| --- | --- | --- |
| Core | Pure domain contracts, range ownership, scheduler policy | ByteRange implemented; other contracts planned |
| Infrastructure | HTTP, interface discovery/binding, disk, SQLite | Discovery and experimental Windows-bound probe implemented; disk/SQLite planned |
| App.Wpf | Composition, MVVM, resource-backed English UI | Planned |
| Tests | Core invariants, controlled HTTP behavior, Windows acceptance | Interval and loopback HTTP tests, controlled fixture server and probe CLI implemented; Windows acceptance pending |

## Contract rules

`ByteRange` owns `[Start, EndExclusive)` with `0 <= Start < EndExclusive <= long.MaxValue`. Splits preserve exact coverage and cannot introduce an empty interval. HTTP receives `(Start, EndExclusive - 1)`.

Next contracts are NetworkPath (stable identity distinct from address/index/generation), RangeLease (exclusive writable extent and fencing generation), and RemoteResource (representation validators and range capability). Define them alongside their first consumer rather than creating speculative model classes. Core exposes no concrete HttpClient, SQLite or WPF types.

A single logical owner will serialize each download's allocation changes. Workers will report outcomes; the writer must reject stale leases before reassignment. Durable progress will follow data flush then metadata transaction. Neither behavior is implemented in this slice.

## Compatibility matrix

| Target | Intended role | Evidence |
| --- | --- | --- |
| Linux x64 / .NET 10 | Domain development and HTTP tests | Core validation only |
| Windows 10 22H2 x64 | Initial product acceptance | Pending exact build and current runtime support verification |
| Supported Windows 11 x64 | Initial product acceptance | Pending exact build and current runtime support verification |
| ARM64 / x86 | Outside baseline | Unverified |

The existing specification section 14 supplies the initial wireframe and add-download flow. Theme library selection, installer format and numerical performance limits remain open until measured.
