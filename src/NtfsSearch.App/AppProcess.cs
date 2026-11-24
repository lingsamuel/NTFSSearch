using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace NtfsSearch.App;

internal static class AppProcess
{
    [SupportedOSPlatform("windows")]
    public static Process Start(IEnumerable<string> arguments, bool elevate, int? processorLimit = null)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位采集进程入口。");
        var start = CreateStartInfo(host, arguments, elevate, elevate && HasAdministratorRights(), processorLimit);
        return Process.Start(start) ?? throw new InvalidOperationException("采集进程未能启动。");
    }

    [SupportedOSPlatform("windows")]
    internal static bool HasAdministratorRights()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static ProcessStartInfo CreateStartInfo(string host, IEnumerable<string> arguments, bool elevate,
        bool hasAdministratorRights, int? processorLimit = null)
    {
        // 已提升的后端可将管理员令牌直接传给子进程，无需再经 Shell 请求提权。
        bool useShellElevation = elevate && !hasAdministratorRights;
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = useShellElevation,
            CreateNoWindow = !useShellElevation,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(AppProcess).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (useShellElevation) start.Verb = "runas";
        if (processorLimit is not null)
        {
            if (useShellElevation || processorLimit < 1) throw new ArgumentException("处理器限制仅用于非Shell采集子进程。");
            start.Environment["DOTNET_PROCESSOR_COUNT"] = processorLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return start;
    }
}
