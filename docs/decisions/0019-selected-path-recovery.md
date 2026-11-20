# ADR 0019: Recover download ranges across selected adapters

Status: Implemented. Physical multi-uplink acceptance is tracked separately.

Jobs persist the selected adapter identifiers and source addresses. At execution, resolve those selections against current Windows adapter snapshots. Start with the available selected paths. If none remain, report a network failure with no implicit fallback to Windows system routing.

For a segmented download, obtain the effective URL, range support, length, and strong ETag before using a path. A reachable path with a different representation blocks the transfer. A path with a transport failure can be skipped. Unsupported ranges retain the existing sequential mode on one selected path.

A single worker retains ownership of its assigned byte range until the write and checkpoint finish. If a path fails after its retry budget, retire it for the current transfer and replay that range on another validated path. Prefix progress counts accepted file offsets, while per-path logs count all payload received, including retries. The data still flushes before checkpoint metadata is committed. Final validation uses a surviving selected path and checks the expected checksum when supplied.

Cancellation of the job or its schedule ends all workers. Windows configuration changes invalidate the affected adapter snapshot. Server status errors, resource identity mismatches and storage failures retain their existing failure handling.

Use the downloader's HTTP range scheduler for per-file acceleration. The optional system-wide net-merge host assigns independent TCP/UDP flows to eligible adapters. The two mechanisms have different unit-of-work and acceptance criteria. See [ADR 0018](0018-windows-network-balancer.md).
