using System.Text.Json;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 设置与退出索引快照分别发布，修改偏好不会触碰已有快照。
internal sealed record DesktopSettings(bool SkipExitIndexSave = false)
{
    private const string FileName = "settings.json";

    internal static DesktopSettings Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<DesktopSettings>(AtomicJson.ReadText(path))
            ?? throw new JsonException("应用设置为空。");
    }

    internal void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        AtomicJson.Write(Path.Combine(directory, FileName), this);
    }
}
