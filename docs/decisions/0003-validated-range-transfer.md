# ADR 0003: Isolated validated HTTP range transfer

- Date: 2026-11-14
- Status: Accepted for the single-range primitive; engine integration pending

## Context

The user asked to continue development while they perform Windows acceptance. Phase 1's real interface-routing gate stays open. Independent HTTP correctness can be implemented and validated through loopback without claiming that gate has passed.

## Decision

Add a single-range operation in Infrastructure, referencing Core's ByteRange. The caller supplies an already configured HttpClient, an open writable asynchronous file handle, a known resource length, a strong concrete ETag, exclusive ownership of the destination extent and cancellation. No default transport, redirect policy, proxy route, retry, concurrency or file path is silently selected by this operation. Production integration must use a transport with redirects and decompression disabled, appropriate connection/header/inactivity timeouts and valid interface generation. The caller owns the client/file lifecycle and must await the operation before disposal or range reassignment.

Send HTTP/1.1 GET, Range, If-Range and Accept-Encoding: identity. Before any file write, require 206, exact bytes Content-Range endpoints and total, an unchanged strong response ETag, matching Content-Length when supplied, identity encoding and a non-multipart body. A 200 fallback, 416, authentication failure or throttling response is rejected here, without retry. Later orchestration must classify these responses and choose safe fallback/backoff rather than blindly retrying this operation.

Stream using one pooled 64 KiB buffer and RandomAccess.WriteAsync at 64-bit offsets. Accept exactly the expected number of bytes; reject a short body or an observable extra byte. Bytes beyond an explicit Content-Length are HTTP framing territory handled by HttpClient, not exposed payload. Keep certificate handling with the supplied transport's normal verification; this primitive introduces no bypass.

## Failure and durability contract

A successful return means the requested bytes were written and the response payload boundary checked. It does not mean durable progress, full-file integrity, or download completion. There is no flush, rename, range map or SQLite checkpoint. Header failures leave the file unchanged. Network/body, disk and cancellation failures may leave an uncertain partial extent; callers must not mark any part durable or reassign it while a writer is active. Once the operation has ended, the entire failed extent must be redownloaded under the same validated representation before it can be accepted.

Unknown/weak identity and sequential fallback are not implemented. Monotonic lease fencing, the complete downloader and Windows transport integration remain future slices. This operation is not yet exposed in the user-facing probe or a UI.

## Validation

Real loopback HTTP exchanges verify request headers, exact offset writes and untouched adjacent bytes; reject 200/416/429, missing or shifted ranges, changed size, absent/weak/changed ETags, wrong body lengths, gzip and multipart. Separate tests verify cancellation during a stalled body, malformed input validators and a sparse-file write beyond 4 GiB. Sparse-file testing verifies addressing and bounded allocation, not physical disk capacity or a real 4 GiB transfer. Windows path binding is not exercised by these tests.
