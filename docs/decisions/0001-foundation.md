# ADR 0001: Foundation and verification boundaries

- Date: 2026-11-14
- Status: Accepted
- Scope: Phase 0

## Context

The user authorized development after the specification-only commit. The available machine is Linux x64; the product targets Windows 10/11 x64 and WPF. Networking correctness must precede the visual shell.

## Decision

Pin .NET SDK 10.0.404 with servicing patch roll-forward. Use nullable types, SDK analyzers, deterministic builds and warnings as errors. Centralize test dependency versions. Start with Core and its tests; introduce Infrastructure and App.Wpf only with working behavior. Use xUnit for deterministic boundary and seeded randomized interval tests. These dependencies are test-only; package license and maintenance review is required when adding or updating dependencies. No test framework is shipped with the product.

Represent nonempty byte intervals as immutable 64-bit half-open ranges. Reject empty/inverted ranges; an empty resource will have zero intervals, rather than a zero-length interval. Convert to inclusive HTTP endpoints only at the boundary. Allocation, leases and persistence remain future work.

Do not advance past the networking proof gate on the basis of Linux tests. Keep real-device checks explicit and pending. No background service, VPN, global routing changes or UI dependency is introduced.

## Consequences

The initial slice has no external production dependencies. It tests arithmetic and coverage, not an engine or real interface selection. All phase completion claims require the evidence described in the approved plan. Future SDK/package servicing should update pins and rerun the relevant checks.
