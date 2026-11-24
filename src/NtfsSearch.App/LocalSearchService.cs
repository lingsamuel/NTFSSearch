using System.Net.Sockets;
using NtfsSearch.Core;

namespace NtfsSearch.App;

/// <summary>管理同一搜索主机的 HTTP 监听。桌面会话负责串行调用与发布状态。</summary>
internal sealed class LocalSearchService(SearchHost host, string? portOverride, Func<DesktopLocale> locale) : IAsyncDisposable
{
    private LocalSearchApi? listener;

    internal bool PortOverridden => LocalSearchEndpoint.IsPortOverridden(portOverride);
    internal Uri? Address { get; private set; }
    internal bool Running => listener is not null;

    internal async Task InitializeAsync(bool enabled, int port)
    {
        Address = ResolveAddress(enabled, port);
        if (enabled && Address is Uri address) listener = await BindAsync(address);
    }

    /// <summary>新地址绑定后才提交配置。配置提交失败时释放候选监听，原监听继续处理请求。</summary>
    internal async Task ApplyAsync(bool enabled, int port, Action save)
    {
        try { LocalSearchEndpoint.ValidatePort(port); }
        catch (ArgumentException error) { throw new ArgumentException(locale().Text("backend.service.invalidPort"), error); }
        Uri? address = ResolveAddress(enabled, port);
        LocalSearchApi? candidate = enabled && address is Uri endpoint && (listener is null || endpoint != Address)
            ? await BindAsync(endpoint) : null;
        try { save(); }
        catch
        {
            if (candidate is not null) await candidate.DisposeAsync();
            throw;
        }

        Address = address;
        if (!enabled || candidate is not null)
        {
            var previous = listener;
            listener = candidate;
            if (previous is not null) await previous.DisposeAsync();
        }
    }

    internal async Task RestartAsync(bool enabled, int port)
    {
        if (!enabled) throw new InvalidOperationException(locale().Text("backend.service.disabled"));
        Address = ResolveAddress(enabled, port)!;
        // 重启复用相同地址，须先释放旧监听；索引由桌面主机持有。
        await DisposeAsync();
        listener = await BindAsync(Address);
    }

    public async ValueTask DisposeAsync()
    {
        var previous = listener;
        listener = null;
        if (previous is not null) await previous.DisposeAsync();
    }

    private Uri? ResolveAddress(bool enabled, int port)
    {
        try { return LocalSearchEndpoint.Resolve(portOverride, port); }
        catch (ArgumentException error)
        {
            if (!enabled) return null;
            string message = PortOverridden ? locale().Text("backend.service.invalidPortOverride", ("variable", LocalSearchEndpoint.PortVariable))
                : locale().Text("backend.service.invalidPort");
            throw new ArgumentException(message, error);
        }
    }

    private async Task<LocalSearchApi> BindAsync(Uri address)
    {
        try { return await LocalSearchApi.StartAsync(host, address); }
        catch (Exception error) when (error is IOException or SocketException or InvalidOperationException)
        { throw new InvalidOperationException(locale().Text("backend.service.bindFailed", ("port", address.Port), ("detail", error.Message)), error); }
    }
}
