using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.Infrastructure.Http;

internal sealed record RemoteResource(long? Length, string? ETag, bool CanSegment, Uri EffectiveUri);

internal static class ResourceProbe
{
    internal static async Task<RemoteResource> ProbeAsync(HttpClient client, Uri source, CancellationToken token)
    {
        PathProbe.ValidateDestination(source);
        using var request = new HttpRequestMessage(HttpMethod.Get, source)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Range = new RangeHeaderValue(0, 0);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await HttpTransferPolicy.SendAsync(client, request, token);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == 0)
            return new(0, null, false, HttpTransferPolicy.ResponseUri(response));
        HttpTransferPolicy.RequireSuccess(response);
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Encoded representations are not supported.");
        if (response.StatusCode == HttpStatusCode.OK)
            return new(response.Content.Headers.ContentLength, null, false, HttpTransferPolicy.ResponseUri(response));
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != 0 || range.To != 0 || range.Length is not > 0)
            throw new InvalidDataException("The probe returned an invalid range response.");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[2];
        var first = await HttpTransferPolicy.ReadAsync(body, buffer.AsMemory(0, 1), token);
        if (first != 1 || await HttpTransferPolicy.ReadAsync(body, buffer.AsMemory(1, 1), token) != 0)
            throw new InvalidDataException("The probe returned an invalid payload length.");
        var tag = response.Headers.ETag;
        return new(range.Length, tag?.ToString(), tag is { IsWeak: false } && tag.Tag != "*", HttpTransferPolicy.ResponseUri(response));
    }
}
