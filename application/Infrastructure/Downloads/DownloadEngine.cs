using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DownloadManager.Core;
using DownloadManager.Infrastructure.Http;
using DownloadManager.Infrastructure.Persistence;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Infrastructure.Downloads;

/// <summary>Caller supplies allowed, isolated transports. No transport is silently added.</summary>
public sealed class DownloadEngine
{
    public async Task DownloadAsync(Uri source, string destination, IReadOnlyList<HttpClient> transports,
        DownloadOptions? options = null, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        options.Validate();
        var rateLimiter = options.BytesPerSecondLimit is { } limit ? new PayloadRateLimiter(limit) : null;
        ArgumentNullException.ThrowIfNull(transports);
        if (transports.Count == 0 || transports.Any(t => t is null)) throw new ArgumentException("At least one allowed transport is required.", nameof(transports));
        if (transports.Count > options.Workers) throw new ArgumentException("Set the worker count to at least the number of selected transports.", nameof(transports));
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Destination already exists; choose another filename.");
        var part = destination + ".part";
        var database = part + ".sqlite";
        progress?.Report(new(DownloadStage.Probing, 0, null, false));
        var resource = await RetryAsync(() => ResourceProbe.ProbeAsync(transports[0], source, cancellationToken), options.RetryLimit, cancellationToken);
        if (!resource.CanSegment)
        {
            await DownloadSequentialAsync(source, destination, part, database, transports[0], resource, options, progress, rateLimiter, cancellationToken);
            return;
        }
        var length = resource.Length!.Value;
        // Every selected path must see exactly the same representation before combining bytes.
        foreach (var transport in transports.Skip(1))
        {
            var other = await RetryAsync(() => ResourceProbe.ProbeAsync(transport, source, cancellationToken), options.RetryLimit, cancellationToken);
            if (other != resource) throw new InvalidDataException("Selected paths do not expose the same representation.");
        }
        if (File.Exists(part) && !File.Exists(database))
            throw new InvalidDataException("A partial file exists without recovery metadata. Preserve it and choose a new destination.");
        var resumed = File.Exists(part);
        using (var handle = File.OpenHandle(part, resumed ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous))
        using (var store = new CheckpointStore(database, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri + (HttpTransferPolicy.CanonicalUri(source) == resource.EffectiveUri ? "" : "\n" + resource.EffectiveUri.AbsoluteUri)))), length, resource.ETag!))
        using (var checkpoint = new SemaphoreSlim(1))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (RandomAccess.GetLength(handle) > length) throw new InvalidDataException("The partial file exceeds the expected length.");
            var saved = store.Load();
            _ = new RangeAllocator(length, saved.Select(s => s.Range)); // Reject corrupt/overlapping metadata before trusting any bytes.
            var verified = new List<ByteRange>();
            foreach (var row in saved)
            {
                if (await HashAsync(handle, row.Range.Start, row.Range.Length, cancellationToken) == row.Hash) verified.Add(row.Range);
                else store.Remove(row.Range.Start);
            }
            var pool = new RangeAllocator(length, verified);
            var allocationLock = new object();
            long completed = verified.Sum(r => r.Length);
            progress?.Report(new(DownloadStage.Downloading, completed, length, resumed));
            var coverage = new TransferCoverage(length, completed, options.Workers);
            var reportLock = new object();
            var reportWatch = Stopwatch.StartNew();
            var firstSample = true;
            long lastAccepted = completed, lastDurable = completed;
            void ReportCoverage(TransferCoverageSnapshot snapshot, bool force)
            {
                lock (reportLock)
                {
                    if (snapshot.AcceptedBytes < lastAccepted || snapshot.DurableBytes < lastDurable) return;
                    if (!force && !firstSample && reportWatch.ElapsedMilliseconds < 100) return;
                    lastAccepted = snapshot.AcceptedBytes; lastDurable = snapshot.DurableBytes;
                    firstSample = false; reportWatch.Restart();
                    progress?.Report(new DownloadProgress(DownloadStage.Downloading, snapshot.DurableBytes, length, resumed) { AcceptedBytes = snapshot.AcceptedBytes });
                }
            }
            var workers = Enumerable.Range(0, options.Workers).Select(i => WorkerAsync(i, transports[i % transports.Count])).ToArray();
            await Task.WhenAll(workers); // All writers end before verification, finalization or a caller retry.
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != length || RandomAccess.GetLength(handle) != length) throw new InvalidDataException("Incomplete coverage.");
            progress?.Report(new(DownloadStage.Verifying, length, length, resumed));
            var finalResource = await ResourceProbe.ProbeAsync(transports[0], source, cancellationToken);
            if (finalResource != resource) throw new InvalidDataException("The resource changed before finalization.");
            await VerifyHashAsync(handle, length, options.ExpectedSha256, cancellationToken);
            RandomAccess.FlushToDisk(handle);

            async Task WorkerAsync(int worker, HttpClient transport)
            {
                long target = options.MinimumRangeBytes;
                try
                {
                    while (true)
                    {
                        ByteRange? range;
                        lock (allocationLock) range = pool.Take(target);
                        if (range is null) return;
                        coverage.Start(worker, range.Length);
                        var watch = Stopwatch.StartNew();
                        await RetryAsync(() => RangeTransfer.WriteAsync(transport, source, range, length, resource.ETag!, handle, stop.Token, rateLimiter, resource.EffectiveUri, prefix => ReportCoverage(coverage.Advance(worker, prefix), false)), options.RetryLimit, stop.Token);
                        var hash = await HashAsync(handle, range.Start, range.Length, stop.Token) ?? throw new EndOfStreamException();
                        await checkpoint.WaitAsync(stop.Token);
                        try
                        {
                            // Accepted data reaches disk before corresponding metadata is committed.
                            RandomAccess.FlushToDisk(handle);
                            store.Save(range, hash);
                            completed += range.Length;
                            ReportCoverage(coverage.Commit(worker), true);
                        }
                        finally { checkpoint.Release(); }
                        var estimate = range.Length / Math.Max(watch.Elapsed.TotalSeconds, 0.001) * 2;
                        target = (long)Math.Clamp(estimate, options.MinimumRangeBytes, options.MaximumRangeBytes);
                    }
                }
                catch { await stop.CancelAsync(); throw; }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(part, destination, overwrite: false);
        File.Delete(database);
        progress?.Report(new(DownloadStage.Completed, length, length, resumed));
    }

    private static async Task DownloadSequentialAsync(Uri source, string destination, string part, string database, HttpClient client,
        RemoteResource resource, DownloadOptions options, IProgress<DownloadProgress>? progress, PayloadRateLimiter? rateLimiter, CancellationToken token)
    {
        if (File.Exists(part) || File.Exists(database)) throw new IOException("Existing partial state cannot be safely resumed in sequential mode.");
        var expectedLength = resource.Length;
        var created = false;
        try
        {
            using (var handle = File.OpenHandle(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous))
            {
                created = true;
                using var request = new HttpRequestMessage(HttpMethod.Get, source) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.AcceptEncoding.ParseAdd("identity");
                using var response = await HttpTransferPolicy.SendAsync(client, request, token);
                HttpTransferPolicy.RequireSuccess(response);
                if (HttpTransferPolicy.ResponseUri(response) != resource.EffectiveUri)
                    throw new InvalidDataException("The sequential resource URI changed after probing.");
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Unexpected sequential representation.");
                var total = response.Content.Headers.ContentLength;
                if (expectedLength is { } known && total is { } actual && known != actual) throw new InvalidDataException("Resource size changed.");
                await using var body = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[65536];
                long offset = 0;
                int count;
                while ((count = await HttpTransferPolicy.ReadAsync(body, buffer.AsMemory(0, rateLimiter?.ReadSize ?? buffer.Length), token)) != 0)
                {
                    if (rateLimiter is not null) await rateLimiter.WaitAsync(count, token);
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, count), offset, token);
                    offset = checked(offset + count);
                    progress?.Report(new(DownloadStage.Downloading, offset, total, false));
                }
                if ((total ?? expectedLength) is { } size && offset != size) throw new EndOfStreamException("Sequential size mismatch.");
                progress?.Report(new(DownloadStage.Verifying, offset, offset, false));
                await VerifyHashAsync(handle, offset, options.ExpectedSha256, token);
                RandomAccess.FlushToDisk(handle);
                expectedLength = offset;
            }
            token.ThrowIfCancellationRequested();
            File.Move(part, destination, false);
            created = false;
            progress?.Report(new(DownloadStage.Completed, expectedLength!.Value, expectedLength, false));
        }
        finally { if (created) File.Delete(part); }
    }

    private static async Task VerifyHashAsync(SafeFileHandle handle, long length, string? expected, CancellationToken token)
    {
        if (expected is not null && !string.Equals(await HashAsync(handle, 0, length, token), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The authoritative SHA-256 does not match. The file was not finalized.");
    }

    private static async Task<string?> HashAsync(SafeFileHandle handle, long start, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long done = 0;
        while (done < length)
        {
            var count = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), start + done, token);
            if (count == 0) return null;
            hash.AppendData(buffer, 0, count);
            done += count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, int retries, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action(); }
            catch (Exception ex) when (attempt < retries && !token.IsCancellationRequested && IsTransient(ex))
            {
                var delay = ex is HttpStatusFailure { RetryAfter: { } serverDelay } ? serverDelay
                    : TimeSpan.FromMilliseconds(200 * (1 << attempt) + Random.Shared.Next(100));
                // Do not retry sooner than a server delay that exceeds this operation's retry budget.
                if (delay > TimeSpan.FromSeconds(30)) throw;
                await Task.Delay(delay, token);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is TimeoutException or EndOfStreamException or HttpIOException
        || ex is HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable };
}
