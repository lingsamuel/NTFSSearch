using System.IO;
using NtfsSearch.Core;

namespace NtfsSearch.App;

/// <summary>仅诊断入口使用：验证真实采集、落盘与前台发布整条链路。</summary>
internal static class AutomaticLiveVerification
{
    internal static async Task<object> RunAsync(SearchCatalog catalog, string directory, CancellationToken token)
    {
        string name = "ntfssearch-live-audit-" + Guid.NewGuid().ToString("N");
        string first = Path.Combine(directory, name + ".txt");
        string renamed = Path.Combine(directory, name + "-renamed.txt");
        string offline = Path.Combine(directory, "ntfssearch-offline-verification.txt");
        bool? offlineRecovered = null;
        if (File.Exists(offline))
        {
            await WaitAsync(offline, 1);
            offlineRecovered = true;
        }
        try
        {
            await File.WriteAllTextAsync(first, "automatic live verification", token);
            await WaitAsync(first, 1);
            File.Move(first, renamed);
            await WaitAsync(first, 0); await WaitAsync(renamed, 1);
            File.Delete(renamed); await WaitAsync(renamed, 0);
            return new { Create = true, Rename = true, Delete = true, OfflineRecovered = offlineRecovered };
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(renamed)) File.Delete(renamed);
        }

        async Task WaitAsync(string path, long expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var result = await Task.Run(() => catalog.Search('"' + path + '"', limit: 1, cancellationToken: token), token);
                if (result.TotalCount == expected) return;
                await Task.Delay(200, token);
            }
            throw new InvalidDataException($"自动增量链路未及时发布 {Path.GetFileName(path)}，预期数量 {expected}。");
        }
    }
}
