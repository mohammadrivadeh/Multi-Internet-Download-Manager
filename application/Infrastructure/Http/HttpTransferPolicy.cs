using System.Net;
using DownloadManager.Infrastructure.Diagnostics;

namespace DownloadManager.Infrastructure.Http;

public sealed class HttpStatusFailure(HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException("The server did not accept the transfer request.", null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal static class HttpTransferPolicy
{
    internal static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) ThrowStatus(response);
    }

    internal static void ThrowStatus(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        throw new HttpStatusFailure(response.StatusCode, delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
    }

    internal const int MaximumRedirects = 8;

    internal static Uri CanonicalUri(Uri uri) => new(uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped), UriKind.Absolute);

    internal static Uri ResponseUri(HttpResponseMessage response) => CanonicalUri(response.RequestMessage?.RequestUri
        ?? throw new InvalidDataException("The response has no resource URI."));

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null)
            throw new ArgumentException("Only bodyless GET requests support this redirect policy.", nameof(request));
        // Default headers would be reapplied by HttpClient at every hop, bypassing our forwarding allowlist.
        if (client.DefaultRequestHeaders.Any())
            throw new InvalidOperationException("Use an isolated client without default request headers.");
        var initial = request.RequestUri ?? throw new ArgumentException("An absolute request URI is required.", nameof(request));
        Networking.PathProbe.ValidateDestination(initial);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var visited = new HashSet<string>(StringComparer.Ordinal) { CanonicalUri(initial).AbsoluteUri };
        var ownedRequests = new List<HttpRequestMessage>();
        var current = request;
        try
        {
            for (var hops = 0; ; hops++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (DownloadPathTrace.Current is { } trace) current.Options.Set(DownloadPathTrace.RequestContext, trace.Detail);
                var response = await client.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (DownloadPathTrace.Current is { } path && !response.IsSuccessStatusCode)
                    LocalDiagnostics.Trace("download-http-status", $"{path.Detail} status={(int)response.StatusCode}");
                response.RequestMessage ??= current;
                Uri receivedAt;
                try { receivedAt = ResponseUri(response); }
                catch { response.Dispose(); throw; }
                if (receivedAt != CanonicalUri(current.RequestUri!))
                {
                    response.Dispose();
                    throw new InvalidDataException("Automatic handler redirects must be disabled.");
                }
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;
                Uri next;
                try
                {
                    if (hops >= MaximumRedirects) throw new InvalidDataException("Redirect limit exceeded.");
                    var location = response.Headers.Location ?? throw new InvalidDataException("Redirect target is missing.");
                    var resolved = new Uri(current.RequestUri!, location);
                    Networking.PathProbe.ValidateDestination(resolved);
                    next = CanonicalUri(resolved);
                    if (current.RequestUri!.Scheme == "https" && next.Scheme != "https")
                        throw new InvalidDataException("HTTPS downgrade redirects are not allowed.");
                    if (!visited.Add(next.AbsoluteUri)) throw new InvalidDataException("Redirect loop detected.");
                }
                catch (Exception ex) when (ex is ArgumentException or UriFormatException)
                { throw new InvalidDataException("Invalid redirect target."); }
                finally { response.Dispose(); }
                var follow = new HttpRequestMessage(HttpMethod.Get, next)
                { Version = request.Version, VersionPolicy = request.VersionPolicy };
                foreach (var name in new[] { "Range", "If-Range", "Accept-Encoding" })
                    if (request.Headers.TryGetValues(name, out var values)) follow.Headers.TryAddWithoutValidation(name, values);
                ownedRequests.Add(follow);
                current = follow;
            }
        }
        catch (OperationCanceledException ex)
        {
            if (DownloadPathTrace.Current is { } path)
                LocalDiagnostics.Record("download-request-ended", ex, $"{path.Detail} phase=response-headers reason={(token.IsCancellationRequested ? "cancelled" : "timeout")}");
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Response headers or redirects timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            if (DownloadPathTrace.Current is { } path)
                LocalDiagnostics.Record("download-request-failed", ex, $"{path.Detail} phase=response-headers reason={ex.HttpRequestError}");
            throw;
        }
        finally { foreach (var follow in ownedRequests) follow.Dispose(); }
    }

    internal static async ValueTask<int> ReadAsync(Stream body, Memory<byte> buffer, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await body.ReadAsync(buffer, deadline.Token); }
        catch (OperationCanceledException ex)
        {
            if (DownloadPathTrace.Current is { } path)
                LocalDiagnostics.Record("download-read-ended", ex, $"{path.Detail} phase=response-body reason={(token.IsCancellationRequested ? "cancelled" : "timeout")}");
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("Response body became inactive.", ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            if (DownloadPathTrace.Current is { } path)
                LocalDiagnostics.Record("download-read-failed", ex, $"{path.Detail} phase=response-body");
            throw;
        }
    }
}
