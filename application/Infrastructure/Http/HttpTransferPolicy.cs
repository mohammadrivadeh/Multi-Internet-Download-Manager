using System.Net;

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

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Response headers timed out."); }
    }

    internal static async ValueTask<int> ReadAsync(Stream body, Memory<byte> buffer, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await body.ReadAsync(buffer, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Response body became inactive."); }
    }
}
