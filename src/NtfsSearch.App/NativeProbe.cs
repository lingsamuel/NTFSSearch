using System.Security.Principal;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class NativeProbe
{
    public static async Task<int> RunAsync(string reportPath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            using var child = AppProcess.Start(["--probe-volumes", reportPath], elevate: true);
            await child.WaitForExitAsync(); return child.ExitCode;
        }
        var results = new List<object>();
        bool success = true;
        foreach (var volume in VolumeDiscovery.Discover())
        {
            try { results.Add(new { volume.Root, RecordsInFirstBatch = NtfsEnumerator.Probe(volume.Root[0]), MetadataMatches = NtfsEnumerator.ProbeMetadata(volume.Root[0]), Journal = NativeJournal.Query(volume.Root[0]), Error = (string?)null }); }
            catch (Exception error) { success = false; results.Add(new { volume.Root, RecordsInFirstBatch = 0, Error = error.Message }); }
        }
        AtomicJson.Write(reportPath, new { Mode = "只读，每卷少量MFT采样并核对元数据与USN状态，不建立磁盘索引", Elevated = true, Results = results });
        return success ? 0 : 1;
    }
}
