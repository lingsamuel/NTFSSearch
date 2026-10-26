using System.IO;
using System.Windows;

namespace NtfsSearch.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Length == 2 && e.Args[0] == "--probe-volumes")
        {
            try { Shutdown(await NativeProbe.RunAsync(e.Args[1])); }
            catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.FirstOrDefault() is "--collect" or "--scan-volume")
        {
            try { Shutdown(e.Args[0] == "--collect" ? await CollectorHost.RunAsync(e.Args) : await CollectorHost.ScanVolumeAsync(e.Args)); }
            catch (Exception error)
            {
                if (e.Args.Length > 1 && Directory.Exists(e.Args[1]))
                    File.WriteAllText(Path.Combine(e.Args[1], "collector-error.txt"), error.Message);
                Shutdown(1);
            }
            return;
        }
        bool smoke = e.Args.Length == 2 && e.Args[0] == "--smoke-test";
        var window = new MainWindow(smoke); MainWindow = window;
        if (e.Args.Length == 2 && e.Args[0] == "--automatic-test")
        {
            try { NtfsSearch.Core.AtomicJson.Write(e.Args[1], await window.AutomaticReadTestAsync()); window.Close(); Shutdown(0); }
            catch (Exception error) { File.WriteAllText(e.Args[1], error.ToString()); window.Close(); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            try
            {
                await window.SmokeTestAsync();
                File.WriteAllText(e.Args[1], "WPF 自动发现/加载流程、真实启动入口、异步搜索与全局排序通过；测试后端代替实际提权，未显示窗口或模拟输入。\n");
                window.Close(); Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(e.Args[1], error.ToString()); window.Close(); Shutdown(1);
            }
            return;
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }
}
