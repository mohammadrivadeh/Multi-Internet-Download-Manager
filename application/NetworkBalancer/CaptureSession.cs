using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.NetworkBalancer;

public sealed record CaptureAdapterChoice(int Index, string Name);

[SupportedOSPlatform("windows")]
public sealed class CaptureSession
{
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CaptureSession(string executable, IReadOnlyList<CaptureAdapterChoice> adapters)
        => Completion = RunAsync(executable, adapters.ToArray());

    public Task Ready => ready.Task;
    public Task Completion { get; }

    public void RequestStop() => stop.Cancel();

    internal static ProcessStartInfo CreateStartInfo(string executable, string pipeName, int parentProcessId,
        IReadOnlyList<CaptureAdapterChoice> adapters)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory
        };
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        foreach (var adapter in adapters)
            start.ArgumentList.Add($"{adapter.Index.ToString(CultureInfo.InvariantCulture)}:{adapter.Name}");
        return start;
    }

    private async Task RunAsync(string executable, IReadOnlyList<CaptureAdapterChoice> adapters)
    {
        try
        {
            var pipeName = $"multi-internet-balancer-{Guid.NewGuid():N}";
            using var pipe = OpenPipe(pipeName);
            using var process = await Task.Run(() => Process.Start(CreateStartInfo(executable, pipeName, Environment.ProcessId, adapters)))
                .ConfigureAwait(false) ?? throw new IOException("Windows did not start the capture host.");

            using var startup = new CancellationTokenSource();
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            await ConnectAsync(pipe, process, startup.Token).ConfigureAwait(false);
            if (stop.IsCancellationRequested)
            {
                ready.TrySetCanceled();
                return;
            }
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await pipe.WriteAsync(new byte[] { (byte)'A' }, startup.Token).ConfigureAwait(false);
            using var stopMonitor = new CancellationTokenSource();
            var stopRequest = SendStopAsync(pipe, stopMonitor.Token);
            try
            {
                var result = await reader.ReadLineAsync(startup.Token).ConfigureAwait(false);
                if (result == "READY")
                {
                    ready.TrySetResult();
                    // An EOF after READY is an unexpected exit, even if its process exit code is zero.
                    result = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                if (result != "STOPPED")
                    ThrowProtocolError(result);
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new IOException($"The capture host exited with code {process.ExitCode}.");
                ready.TrySetCanceled();
            }
            finally
            {
                await stopMonitor.CancelAsync().ConfigureAwait(false);
                await stopRequest.ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            var reported = error is OperationCanceledException
                ? new TimeoutException("The capture host did not respond in time.", error)
                : error;
            ready.TrySetException(reported);
            if (!ReferenceEquals(reported, error))
                throw reported;
            throw;
        }
    }

    private static NamedPipeServerStream OpenPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Could not identify the signed-in Windows user.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 1024, 1024, security);
    }

    private static async Task ConnectAsync(NamedPipeServerStream pipe, Process process, CancellationToken token)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token);
        var connected = pipe.WaitForConnectionAsync(pending.Token);
        var exited = process.WaitForExitAsync(pending.Token);
        try
        {
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                throw new IOException($"The capture host exited before connecting (code {process.ExitCode}).");
            }
            await connected.ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (pid != (uint)process.Id)
                throw new IOException("The capture host could not be authenticated.");
        }
        finally
        {
            await pending.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task SendStopAsync(NamedPipeServerStream pipe, CancellationToken monitorToken)
    {
        using var signals = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, monitorToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, signals.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (signals.IsCancellationRequested) { }
        if (monitorToken.IsCancellationRequested)
            return;
        try { await pipe.WriteAsync(new byte[] { (byte)'S' }, monitorToken).ConfigureAwait(false); }
        catch (IOException) { }
        catch (OperationCanceledException) when (monitorToken.IsCancellationRequested) { }
    }

    private static void ThrowProtocolError(string? response) =>
        throw new IOException(response?.StartsWith("ERROR ", StringComparison.Ordinal) == true
            ? response[6..]
            : "The capture host disconnected or returned an unexpected response. Route cleanup is unconfirmed.");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
