using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Networking;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Infrastructure.Http;

/// <summary>A single validated range transfer. Caller owns transport, file, and exclusive range ownership.</summary>
public static class RangeTransfer
{
    public static async Task<long> WriteAsync(HttpClient client, Uri source, ByteRange range,
        long resourceLength, string strongETag, SafeFileHandle destination, CancellationToken cancellationToken, PayloadRateLimiter? rateLimiter = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(destination);
        PathProbe.ValidateDestination(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceLength);
        if (range.EndExclusive > resourceLength)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (!EntityTagHeaderValue.TryParse(strongETag, out var tag) || tag.IsWeak || tag.Tag == "*")
            throw new ArgumentException("A strong, concrete ETag is required.", nameof(strongETag));
        if (destination.IsClosed || destination.IsInvalid)
            throw new ArgumentException("An open writable file handle is required.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();

        using var request = new HttpRequestMessage(HttpMethod.Get, source)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var (from, to) = range.ToHttpRange();
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.IfRange = new RangeConditionHeaderValue(tag);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await HttpTransferPolicy.SendAsync(client, request, cancellationToken);
        ValidateResponse(response, range, resourceLength, tag);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long written = 0;
            while (written < range.Length)
            {
                var count = await HttpTransferPolicy.ReadAsync(body, buffer.AsMemory(0, (int)Math.Min(rateLimiter?.ReadSize ?? buffer.Length, range.Length - written)), cancellationToken);
                if (count == 0)
                    throw new EndOfStreamException("The range body ended before the expected boundary.");
                if (rateLimiter is not null) await rateLimiter.WaitAsync(count, cancellationToken);
                await RandomAccess.WriteAsync(destination, buffer.AsMemory(0, count), range.Start + written, cancellationToken);
                written += count;
            }
            // Chunked/unknown-length responses may contain extra bytes beyond the claimed range.
            if (await HttpTransferPolicy.ReadAsync(body, buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new InvalidDataException("The range body exceeded the expected boundary.");
            return written;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ValidateResponse(HttpResponseMessage response, ByteRange range, long resourceLength, EntityTagHeaderValue tag)
    {
        HttpTransferPolicy.RequireSuccess(response);
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new InvalidDataException("Expected a partial response; no payload was accepted.");
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || contentRange.From != range.Start || contentRange.To != range.EndExclusive - 1 || contentRange.Length != resourceLength)
            throw new InvalidDataException("The response range or resource size does not match.");
        if (response.Headers.ETag is not { IsWeak: false } actualTag || !actualTag.Equals(tag))
            throw new InvalidDataException("The resource validator is missing or changed.");
        if (response.Content.Headers.ContentLength is { } length && length != range.Length)
            throw new InvalidDataException("The response body length does not match the range.");
        if (response.Content.Headers.ContentEncoding.Any(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Encoded range payloads are not supported.");
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidDataException("Multipart range payloads are not supported.");
    }
}
