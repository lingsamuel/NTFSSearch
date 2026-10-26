namespace NtfsSearch.Core;

/// <summary>发布已经写入并刷盘的临时文件；替换期间始终保留原目标的完整版本。</summary>
public static class AtomicFile
{
    public static void Publish(string temporary, string destination, string? backup = null, CancellationToken token = default)
    {
        Retry(() =>
        {
            // Windows的覆盖Move即使目标允许Delete共享，也不能替换仍被尾读者持有的文件。
            if (File.Exists(destination)) File.Replace(temporary, destination, backup);
            else File.Move(temporary, destination, overwrite: true);
        }, OperatingSystem.IsWindows(), token);
    }

    internal static void Retry(Action publish, bool windows, CancellationToken token = default, bool allowMissing = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { publish(); return; }
            catch (Exception error) when (windows && attempt < 11 && IsTransient(error, allowMissing))
            {
                // 1175及共享/权限冲突保留原目标和候选的路径，可重试同一原子操作。
                // 1176/1177可能已部分改名，不能在不了解磁盘状态时重复或退化成覆盖复制。
                int delay = Math.Min(10 << Math.Min(attempt, 5), 250);
                if (token.CanBeCanceled) { if (token.WaitHandle.WaitOne(delay)) token.ThrowIfCancellationRequested(); }
                else Thread.Sleep(delay);
            }
        }
    }

    internal static bool ExistsWithReplaceGrace(string path)
    {
        if (File.Exists(path)) return true;
        if (!OperatingSystem.IsWindows()) return false;
        // 初次没有缓存只等待35ms；已发布目标的ReplaceFile改名窗口不能被误认成整卷缺失。
        foreach (int delay in new[] { 5, 10, 20 })
        {
            Thread.Sleep(delay);
            if (File.Exists(path)) return true;
        }
        return false;
    }

    private static bool IsTransient(Exception error, bool allowMissing) => error is IOException or UnauthorizedAccessException
        && ((error.HResult & 0xffff) is 5 or 32 or 33 or 1175 || allowMissing && (error.HResult & 0xffff) is 2 or 3);
}
