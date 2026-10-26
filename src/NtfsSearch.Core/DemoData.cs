namespace NtfsSearch.Core;

public static class DemoData
{
    public static SearchIndex Create(int count = 1000)
    {
        var b = new IndexBuilder(count);
        string[] extensions = ["txt", "dll", "png", "json", "md", "exe", "zip", "log"];
        for (int i = 0; i < count; i++)
        {
            string name = i % 17 == 0 ? $"hello world {i:D6}.txt" : $"文件 report {i:D6}.{extensions[i % extensions.Length]}";
            b.Add($"C:\\演示\\目录{i / 100:D3}\\", name, i * 1024L,
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i).Ticks);
        }
        return b.Build();
    }
}
