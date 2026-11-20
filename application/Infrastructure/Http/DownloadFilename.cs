using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Networking;

namespace DownloadManager.Infrastructure.Http;

public static class DownloadFilename
{
    public static async Task<string?> TryResolveAsync(Uri source)
    {
        using var client = SystemRouteTransport.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        return await TryResolveAsync(source, client, timeout.Token);
    }

    public static async Task<string?> TryResolveAsync(Uri source, HttpClient client, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.Range = new RangeHeaderValue(0, 0);
        try
        {
            using var response = await HttpTransferPolicy.SendAsync(client, request, token);
            if (!response.IsSuccessStatusCode) return null;
            var disposition = response.Content.Headers.ContentDisposition;
            var supplied = ValidLeaf(disposition?.FileNameStar) ?? ValidLeaf(disposition?.FileName);
            if (supplied is not null) return supplied;
            var redirected = NameFromUri(HttpTransferPolicy.ResponseUri(response));
            var original = NameFromUri(source);
            if (Path.HasExtension(redirected)) return redirected;
            if (Path.HasExtension(original)) return original;
            var extension = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() switch
            {
                "application/zip" => ".zip",
                "application/x-7z-compressed" => ".7z",
                "application/vnd.rar" => ".rar",
                "application/pdf" => ".pdf",
                "application/vnd.microsoft.portable-executable" or "application/x-msdownload" => ".exe",
                _ => null
            };
            return extension is null ? redirected ?? original : ValidLeaf((redirected ?? original ?? "download") + extension);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or FormatException)
        {
            return null;
        }
    }

    private static string? NameFromUri(Uri uri) =>
        ValidLeaf(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last()));

    private static string? ValidLeaf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return DestinationRules.ValidateLeaf(name.Trim('"')); }
        catch (ArgumentException) { return null; }
    }
}
