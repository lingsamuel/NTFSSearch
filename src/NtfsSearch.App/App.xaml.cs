using System.IO;
using System.Windows;

namespace NtfsSearch.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow(); MainWindow = window;
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            try
            {
                await window.SmokeTestAsync();
                File.WriteAllText(e.Args[1], "WPF XAML、演示数据、异步搜索与排序通过；未显示窗口或模拟输入。\n");
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
