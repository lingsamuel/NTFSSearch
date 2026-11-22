using System.Reflection;
using System.Text.Json;
using NtfsSearch.Core;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    switch (args.FirstOrDefault())
    {
        case "drives":
            Console.WriteLine(JsonSerializer.Serialize(VolumeDiscovery.Discover())); break;
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
            Console.WriteLine($"已保存 {scanned.Count:N0} 条目录项，含硬链接与可用大小/修改时间；静态诊断命令不启动实时监控。");
            break;
        case "search" or "status":
#if HTTP_SEARCH_SERVICE
            await NtfsSearch.Cli.LocalSearchClient.RunAsync(args, cancellation.Token); break;
#else
            throw new NotSupportedException("实时查询需使用带查询服务的版本。");
#endif
        case "search-snapshot" when args.Length >= 3:
            var loaded = SnapshotStore.Load(args[1], cancellation.Token);
            var sort = args.Length > 3 ? Enum.Parse<SortField>(args[3], true) : SortField.Name;
            bool descending = args.Length > 4 && args[4].Equals("desc", StringComparison.OrdinalIgnoreCase);
            var result = loaded.Search(args[2], sort, descending, cancellationToken: cancellation.Token);
            Console.WriteLine($"精确匹配 {result.TotalCount:N0}；查询 {result.Elapsed.TotalMilliseconds:F3} ms（不含载入）");
            foreach (var row in result.Rows) Console.WriteLine(row.FullPath);
            break;
        case "bench-exit" when args.Length == 3:
            Console.WriteLine(JsonSerializer.Serialize(ExitSnapshotBenchmark.Run(args[1], args[2], cancellation.Token)));
            break;
        case "bench":
            Benchmark.Run(args.Skip(1).ToArray(), cancellation.Token); break;
        default:
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Console.WriteLine($"""
                NtfsSearch {version?.ToString(3)}

                实时索引查询，需先启动桌面程序
                """);
#if HTTP_SEARCH_SERVICE
            Console.WriteLine("""
                  search [query] [--name-only|--match-path] [--sort name|path|size|modified] [--descending]
                         [--offset N] [--limit N] [--min-size-bytes N] [--max-size-bytes N]
                         [--created-from ISO8601] [--created-before ISO8601]
                         [--modified-from ISO8601] [--modified-before ISO8601]
                  status
                """);
#else
            Console.WriteLine("  请使用带查询服务的版本。");
#endif
            Console.WriteLine("""

                诊断命令
                  drives
                  demo <snapshot.ntidx>
                  import <file.efu> <snapshot.ntidx>
                  scan <C:> <snapshot.ntidx>
                  search-snapshot <snapshot.ntidx> <query> [Name|Path|Size|Modified] [desc]
                  bench [count] [report.json] [ordered|shuffled] [repetitions]
                  bench-exit <旧版 Data 目录> <测试退出快照>
                """);
            break;
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("已取消。"); Environment.ExitCode = 130; }
catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception)
{ Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
