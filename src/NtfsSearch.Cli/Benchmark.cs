using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NtfsSearch.Core;

internal static class Benchmark
{
    public static void Run(string[] args, CancellationToken token)
    {
        int count = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 1_000_000;
        string? reportPath = args.Length > 1 ? args[1] : null;
        bool ordered = args.Length > 2 && args[2] == "ordered";
        int repeats = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
        if (count < 1 || repeats < 1) throw new ArgumentException("数量和测量次数必须为正数。");
        const int multiplier = 1_000_003;
        if (!ordered && count % multiplier == 0) throw new ArgumentException("该数量与合成排列乘数不互质，请使用 ordered 或其他数量。");
        Console.WriteLine($"构建 {count:N0} 条唯一名称，{(ordered ? "顺序输入" : "确定性打乱输入")}，4 列排序；合成数据，不创建磁盘文件。");
        var timer = Stopwatch.StartNew();
        var builder = new IndexBuilder(count);
        string[] extensions = ["txt", "dll", "png", "json", "md", "exe", "zip", "log"];
        string[] parents = Enumerable.Range(0, (count - 1) / 1000 + 1).Select(i => $"C:\\bench\\group{i:D6}\\").ToArray();
        Span<char> name = stackalloc char[64];
        for (int i = 0; i < count; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            int id = ordered ? i : (int)((long)i * multiplier % count);
            "file".AsSpan().CopyTo(name);
            id.TryFormat(name[4..], out int digits, "D9", CultureInfo.InvariantCulture);
            int length = 4 + digits;
            if (id % 997 == 0) { " hello world".AsSpan().CopyTo(name[length..]); length += 12; }
            name[length++] = '.'; extensions[id % 8].AsSpan().CopyTo(name[length..]); length += extensions[id % 8].Length;
            builder.Add(parents[id / 1000], name[..length], ordered ? id : (long)id * 7919 % count,
                638_000_000_000_000_000L + (ordered ? id : (long)id * 3571 % count));
            if (i > 0 && i % 5_000_000 == 0) Console.WriteLine($"已生成 {i:N0} 条，{timer.Elapsed.TotalSeconds:F1} s");
        }
        double generationSeconds = timer.Elapsed.TotalSeconds;
        Console.WriteLine($"名称已生成（{generationSeconds:F2} s），建立排序…");
        var index = builder.Build(token, new Progress<string>(Console.WriteLine));
        double buildSeconds = timer.Elapsed.TotalSeconds;
        builder = null!; parents = [];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        long privateBytes = process.PrivateMemorySize64, workingSet = process.WorkingSet64;
        Console.WriteLine($"构建 {buildSeconds:F2} s；工作集 {workingSet / 1073741824d:F3} GiB；私有提交 {privateBytes / 1073741824d:F3} GiB");
        var results = new List<object>();
        (string Query, long Expected)[] cases =
        [
            ("", count), (".dll", count / 8 + (count % 8 > 1 ? 1 : 0)),
            ("file", count), ("\"hello world\"", (count - 1L) / 997 + 1),
            ("group000000\\", Math.Min(count, 1000)), ("codex-no-such-name-7c2e9b", 0)
        ];
        foreach (var (query, expected) in cases)
            foreach (var field in Enum.GetValues<SortField>())
            {
                index.Search(query, field, cancellationToken: token); // 独立记录热查询，不混入首次 JIT。
                var samples = new double[repeats];
                for (int r = 0; r < repeats; r++)
                {
                    var result = index.Search(query, field, cancellationToken: token);
                    if (result.TotalCount != expected) throw new InvalidDataException($"基准计数错误：{query} 应为 {expected}，实际 {result.TotalCount}。");
                    samples[r] = result.Elapsed.TotalMilliseconds;
                }
                var sorted = samples.Order().ToArray();
                double median = sorted[sorted.Length / 2];
                Console.WriteLine($"{query,-30} {field,-8} {expected,12:N0}  median {median,9:F3} ms  max {sorted[^1],9:F3} ms");
                results.Add(new { Query = query, Sort = field.ToString(), ExpectedCount = expected, SamplesMs = samples });
            }
        process.Refresh();
        var report = new
        {
            Version = typeof(SearchIndex).Assembly.GetName().Version?.ToString(3),
            Environment.OSVersion,
            Environment.ProcessorCount,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Count = count,
            Workload = ordered ? "unique-ordered" : "unique-shuffled",
            Repetitions = repeats,
            GenerationSeconds = generationSeconds,
            BuildSeconds = buildSeconds,
            PrivateBytesBeforeQueries = privateBytes,
            WorkingSetBeforeQueries = workingSet,
            PrivateBytesAfterQueries = process.PrivateMemorySize64,
            WorkingSetAfterQueries = process.WorkingSet64,
            index.Statistics,
            Results = results,
            Limitations = "合成名称与路径；未接入实时更新；未包含生产 FRN 映射；不是与 Everything 的真实数据对标。时间包含精确计数、全局排序首屏，不含界面绘制。"
        };
        if (reportPath is not null)
        {
            reportPath = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
