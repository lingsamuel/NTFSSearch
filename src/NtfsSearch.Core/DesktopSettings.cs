using System.Text.Json;

namespace NtfsSearch.Core;

// 旧设置没有 HTTP 开关，正式桌面与隔离演示可各自决定启动默认值。
internal sealed record DesktopSettings(bool SkipExitIndexSave = false, bool? SearchServiceEnabled = null,
    int SearchServicePort = LocalSearchEndpoint.DefaultPort, bool SearchDirectorySizes = true,
    bool PrecomputeSort = true, bool IndexCreationTime = false, bool IndexModifiedTime = true, bool IndexFileSize = true,
    bool ShowSpaceVolumeCapacity = true, bool SpaceVolumeShowFree = false, bool SpaceVolumeCapacityBar = true,
    string Language = DesktopSettings.AutomaticLanguage)
{
    internal const string FileName = "settings.json";
    internal const string AutomaticLanguage = "auto";

    internal IndexMetadata Metadata =>
        (IndexCreationTime ? IndexMetadata.CreationTime : IndexMetadata.None)
        | (IndexModifiedTime ? IndexMetadata.ModifiedTime : IndexMetadata.None)
        | (IndexFileSize ? IndexMetadata.FileSize : IndexMetadata.None);

    internal static DesktopSettings Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!AtomicFile.ExistsWithReplaceGrace(path)) return new();
        return JsonSerializer.Deserialize<DesktopSettings>(AtomicJson.ReadText(path))
            ?? throw new JsonException("应用设置为空。");
    }

    internal void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        AtomicJson.Write(Path.Combine(directory, FileName), this);
    }
}
