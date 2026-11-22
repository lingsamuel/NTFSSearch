using System.Text;
using System.Text.Json;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args is ["--desktop" or "--desktop-test" or "--desktop-benchmark"])
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            void Publish(object message) => output.WriteLine(JsonSerializer.Serialize(message, JsonSerializerOptions.Web));
            try
            {
                int? demoCount = args[0] switch
                {
                    "--desktop-test" => 1000,
                    "--desktop-benchmark" => 1_000_000,
                    _ => null
                };
                string? settingsDirectory = demoCount.HasValue
                    ? Environment.GetEnvironmentVariable("NTFS_SEARCH_TEST_DATA")
                    : PortableDataDirectory.Resolve();
                await new DesktopSession(new SearchHost(demoCount, settingsDirectory), Publish, settingsDirectory).RunAsync(input);
                return 0;
            }
            catch (Exception error)
            {
                try { Publish(new { Type = "error", Scope = "fatal", Message = error.Message }); }
                catch (IOException) { }
                return 1;
            }
        }

        if (args is ["--memory-collector" or "--memory-scan", string pipeName, string parent]
            && int.TryParse(parent, out int parentPid))
        {
            try
            {
                return args[0] == "--memory-collector"
                    ? await MemoryCollector.RunAsync(pipeName, parentPid)
                    : await MemoryCollector.RunScanAsync(pipeName, parentPid);
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        if (args.Length != 2) return 2;
        string report = args[1];
        try
        {
            switch (args[0])
            {
                case "--verify-live": return await NativeLiveVerification.RunAsync(report);
                case "--probe-volumes": return await NativeProbe.RunAsync(report);
                case "--smoke-test":
                    await new SearchHost(demoCount: 1000).SmokeTestAsync();
                    File.WriteAllText(report, "桌面后端自动发现、缓存加载、搜索及排序通过。\n");
                    return 0;
                case "--automatic-test":
                case "--parallel-test":
                case "--full-audit":
                    bool audit = args[0] == "--full-audit", parallel = args[0] == "--parallel-test";
                    string directory = Path.GetDirectoryName(Path.GetFullPath(report))!;
                    var host = new SearchHost(cacheDirectory: audit || parallel ? Path.Combine(directory, "Data") : null);
                    AtomicJson.Write(report, audit ? await host.FullAuditAsync(directory) : await host.AutomaticReadTestAsync(parallel));
                    return 0;
                default: return 2;
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(report, error.ToString());
            return 1;
        }
    }
}
