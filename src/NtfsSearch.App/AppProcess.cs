using System.Diagnostics;
using System.IO;

namespace NtfsSearch.App;

internal static class AppProcess
{
    public static Process Start(IEnumerable<string> arguments, bool elevate)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位采集进程入口。");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(App).Assembly.Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (elevate) start.Verb = "runas";
        return Process.Start(start) ?? throw new InvalidOperationException("采集进程未能启动。");
    }
}
