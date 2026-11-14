using System.Net;
using System.Text.Json;
using DownloadManager.Infrastructure.Networking;

try
{
    var catalog = new NetworkPathCatalog();
    if (args is ["list"])
    {
        Console.WriteLine(JsonSerializer.Serialize(catalog.Discover().Select(p => new
        {
            p.AdapterId, p.Name, SourceAddress = p.SourceAddress.ToString(), p.InterfaceIndex,
            Family = p.SourceAddress.AddressFamily.ToString(), p.Generation, Reachability = "Unverified"
        }), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    if (args is ["probe", var adapterId, var source, var url])
    {
        var address = IPAddress.Parse(source);
        var path = catalog.Discover().SingleOrDefault(p => p.AdapterId == adapterId && p.SourceAddress.Equals(address))
            ?? throw new InvalidOperationException("Selected adapter/address is unavailable.");
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = await PathProbe.RunAsync(path, new Uri(url, UriKind.Absolute), catalog, stop.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { path.AdapterId, SourceAddress = address.ToString(), path.InterfaceIndex, path.Generation, Result = result }));
            return result.StatusCode is >= 200 and <= 299 ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    Console.Error.WriteLine("Usage: NetworkProbe list | probe <adapter-id> <source-address> <http(s)-url>");
    return 2;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or HttpRequestException or System.Net.Sockets.SocketException or OperationCanceledException or PlatformNotSupportedException or System.Net.NetworkInformation.NetworkInformationException or System.IO.IOException)
{
    // Do not emit exception messages: they can contain signed URLs or remote text.
    Console.Error.WriteLine($"Probe failed ({ex.GetType().Name}). Check selection, connectivity, TLS, cancellation and Windows support.");
    return 1;
}
