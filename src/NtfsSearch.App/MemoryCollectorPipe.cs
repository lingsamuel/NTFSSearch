using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.App;

internal static class MemoryCollectorPipe
{
    internal static string NewName() => "ntfs-search-" + Guid.NewGuid().ToString("N");

    internal static NamedPipeServerStream CreateServer(string name, bool acrossElevation)
    {
        const int buffer = 64 << 10;
        if (!acrossElevation)
            return new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, buffer, buffer);

        // UAC可以使用不同的管理员凭据。ACL允许连接，随后严格核对实际启动的进程ID。
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, buffer, buffer, security);
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync(string name, int serverPid, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(15_000, token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != serverPid)
                throw new UnauthorizedAccessException("采集管道的服务进程身份不匹配。");
            return pipe;
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    internal static async Task AcceptAsync(NamedPipeServerStream pipe, int clientPid, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
                if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) && peer == clientPid) return;
                pipe.Disconnect();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待采集进程连接超时。"); }
    }

    // 数据管道和控制管道都没有反向数据。父端退出或关闭连接会取消正在等待的采集。
    internal static async Task WaitUntilClosedAsync(PipeStream pipe, CancellationToken token)
    {
        try { await pipe.ReadExactlyAsync(new byte[1], token); }
        catch (IOException) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

internal sealed class MemoryCollectorConnection : IDisposable
{
    private readonly NamedPipeServerStream control;
    private readonly CollectorFrameWriter writer;
    private readonly SemaphoreSlim writes = new(1);
    private readonly CancellationTokenSource stop = new();
    internal int ProcessId { get; }
    internal Task Completion { get; }

    private MemoryCollectorConnection(NamedPipeServerStream control, int processId)
    {
        this.control = control;
        ProcessId = processId;
        writer = new(control);
        Completion = MemoryCollectorPipe.WaitUntilClosedAsync(control, stop.Token);
    }

    internal static async Task<MemoryCollectorConnection> StartAsync(CancellationToken token)
    {
        string name = MemoryCollectorPipe.NewName();
        var pipe = MemoryCollectorPipe.CreateServer(name, acrossElevation: true);
        var launch = Task.Run(() => AppProcess.Start(["--memory-collector", name, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], elevate: true));
        try
        {
            using var child = await launch.WaitAsync(token);
            await MemoryCollectorPipe.AcceptAsync(pipe, child.Id, token);
            return new(pipe, child.Id);
        }
        catch
        {
            await pipe.DisposeAsync();
            // ShellExecute等待UAC时不能取消。主进程已关闭时，稍后启动的采集器会因管道不存在自行退出。
            if (token.IsCancellationRequested)
                _ = launch.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else _ = task.Exception;
                }, TaskScheduler.Default);
            throw;
        }
    }

    internal async Task RequestAsync(MemoryCollectorRequest request, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            // 控制消息一旦开始传输，只能由整条控制连接取消，避免半帧破坏后续卷请求。
            token.ThrowIfCancellationRequested();
            await writer.WriteAsync(CollectorFrameKind.OpenVolume, request.Write, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { throw new IOException("采集进程的控制连接已关闭。"); }
        finally { writes.Release(); }
    }

    public void Dispose()
    {
        stop.Cancel();
        control.Dispose();
    }
}
