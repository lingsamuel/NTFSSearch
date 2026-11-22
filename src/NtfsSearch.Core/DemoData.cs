namespace NtfsSearch.Core;

public static class DemoData
{
    public static SearchIndex Create(int count = 1000)
        => Create(count, includeDeepBranch: false);

    internal static SearchIndex Create(int count, bool includeDeepBranch)
    {
        var b = new IndexBuilder(count);
        string[] extensions = ["txt", "dll", "png", "json", "md", "exe", "zip", "log"];
        const int firstFolder = 103, fileInBranch = 108;
        string branchPath = @"C:\演示\目录001\";
        ulong branchParent = 101;
        long movedBytes = 0;
        // 桌面演示用同一份数据验证多层操作，保留既有的总条数、总大小和查询样本。
        if (includeDeepBranch)
            for (int i = firstFolder; i < fileInBranch; i++)
            {
                string name = $"深层{i - firstFolder + 1}";
                ulong reference = 1000 + (ulong)i;
                b.Add(branchPath, name, isDirectory: true, fileReference: reference, parentReference: branchParent);
                branchPath += name + "\\";
                branchParent = reference;
                movedBytes += i * 1024L;
            }
        for (int i = 0; i < count; i++)
        {
            if (includeDeepBranch && i is >= firstFolder and < fileInBranch) continue;
            string name = i % 17 == 0 ? $"hello world {i:D6}.txt" : $"文件 report {i:D6}.{extensions[i % extensions.Length]}";
            bool withinBranch = includeDeepBranch && i / 100 == firstFolder / 100;
            b.Add(withinBranch ? branchPath : $"C:\\演示\\目录{i / 100:D3}\\", name,
                i * 1024L + (includeDeepBranch && i == fileInBranch ? movedBytes : 0),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i).Ticks,
                fileReference: (1UL << 32) + (ulong)i, parentReference: withinBranch ? branchParent : 100 + (ulong)i / 100);
        }
        return b.Build();
    }
}
