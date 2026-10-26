using System.Text.Json;
using NtfsSearch.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    switch (args.FirstOrDefault())
    {
        case "demo" when args.Length == 2:
            SnapshotStore.Save(DemoData.Create(), args[1], cancellation.Token);
            Console.WriteLine("已保存 1000 条演示数据。"); break;
        case "import" when args.Length == 3:
            using (var reader = File.OpenText(args[1]))
            {
                var index = EfuImporter.Read(reader, cancellation.Token, new Progress<long>(n => Console.Error.WriteLine($"已导入 {n:N0} 条")));
                SnapshotStore.Save(index, args[2], cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(index.Statistics));
            }
            break;
        case "scan" when args.Length == 3 && args[1].Length is >= 1 and <= 3:
            var scanned = NtfsEnumerator.Scan(args[1][0], cancellation.Token, new Progress<string>(Console.Error.WriteLine));
            SnapshotStore.Save(scanned, args[2], cancellation.Token);
            Console.WriteLine($"已保存 {scanned.Count:N0} 条基础名称记录；大小/修改时间/硬链接尚未补全，无实时监控。");
            break;
        case "search" when args.Length >= 3:
            var loaded = SnapshotStore.Load(args[1], cancellation.Token);
            var sort = args.Length > 3 ? Enum.Parse<SortField>(args[3], true) : SortField.Name;
            bool descending = args.Length > 4 && args[4].Equals("desc", StringComparison.OrdinalIgnoreCase);
            var result = loaded.Search(args[2], sort, descending, cancellationToken: cancellation.Token);
            Console.WriteLine($"精确匹配 {result.TotalCount:N0}；查询 {result.Elapsed.TotalMilliseconds:F3} ms（不含载入）");
            foreach (var row in result.Rows) Console.WriteLine(row.FullPath);
            break;
        case "bench":
            Benchmark.Run(args.Skip(1).ToArray(), cancellation.Token); break;
        default:
            Console.WriteLine("NtfsSearch 0.1.0 技术原型\n\n  demo <snapshot.ntidx>\n  import <file.efu> <snapshot.ntidx>\n  scan <C:> <snapshot.ntidx>    Windows 管理员权限\n  search <snapshot.ntidx> <query> [Name|Path|Size|Modified] [desc]\n  bench [count] [report.json] [ordered|shuffled] [repetitions]\n\n空格分隔 AND 条件，双引号匹配连续文本；默认匹配完整路径。");
            break;
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("已取消。"); Environment.ExitCode = 130; }
catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
