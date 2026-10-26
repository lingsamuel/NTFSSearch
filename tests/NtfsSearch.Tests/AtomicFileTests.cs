using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ntfs-atomic-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(root, "head.json");

    [Fact]
    public void JsonReplacementKeepsPriorHeadAsBackup()
    {
        Directory.CreateDirectory(root);
        AtomicJson.Write(Target, new { Sequence = 1 }, keepBackup: true);
        AtomicJson.Write(Target, new { Sequence = 2 }, keepBackup: true);
        Assert.Contains("\"Sequence\":2", AtomicJson.ReadText(Target));
        Assert.Contains("\"Sequence\":1", AtomicJson.ReadText(Target + ".bak"));
    }

    [Fact]
    public async Task WindowsTemporaryHeadLockCanReleaseBeforeAtomicPublicationCompletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(root);
        AtomicJson.Write(Target, new { Sequence = 1 }, keepBackup: true);
        using var locked = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var started = new TaskCompletionSource();
        Task publish = Task.Run(() => { started.SetResult(); AtomicJson.Write(Target, new { Sequence = 2 }, keepBackup: true); });
        await started.Task;
        await Task.Delay(100);
        Assert.False(publish.IsCompleted);
        Assert.Contains("\"Sequence\":1", AtomicJson.ReadText(Target));
        locked.Dispose();
        await publish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"Sequence\":2", AtomicJson.ReadText(Target));
        Assert.Contains("\"Sequence\":1", AtomicJson.ReadText(Target + ".bak"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(1175)]
    public void TransientWindowsErrorsRetrySameCandidateWithoutChangingOldHead(int code)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Target, "head-1"); File.WriteAllText(Target + ".bak", "head-0");
        string candidate = Path.Combine(root, "candidate.tmp"); File.WriteAllText(candidate, "head-2");
        int attempts = 0;
        AtomicFile.Retry(() =>
        {
            attempts++;
            if (attempts < 3)
            {
                Assert.Equal("head-1", File.ReadAllText(Target));
                Assert.Equal("head-0", File.ReadAllText(Target + ".bak"));
                throw Failure(code);
            }
            File.Replace(candidate, Target, Target + ".bak");
        }, windows: true);
        Assert.Equal(3, attempts);
        Assert.Equal("head-2", File.ReadAllText(Target));
        Assert.Equal("head-1", File.ReadAllText(Target + ".bak"));
    }

    [Theory]
    [InlineData(1176)]
    [InlineData(1177)]
    [InlineData(112)]
    public void OtherErrorsAreNotRetried(int code)
    {
        int attempts = 0;
        var failure = Failure(code);
        Assert.Same(failure, Assert.Throws<IOException>(() => AtomicFile.Retry(() =>
        { attempts++; throw failure; }, windows: true)));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void ExhaustedReplacementLeavesHeadAndBackupIntact()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Target, "head-1"); File.WriteAllText(Target + ".bak", "head-0");
        string candidate = Path.Combine(root, "candidate.tmp"); File.WriteAllText(candidate, "head-2");
        var failure = Failure(1175);
        Assert.Same(failure, Assert.Throws<IOException>(() => AtomicFile.Retry(() => throw failure, windows: true)));
        Assert.Equal("head-1", File.ReadAllText(Target)); Assert.Equal("head-0", File.ReadAllText(Target + ".bak"));
        Assert.Equal("head-2", File.ReadAllText(candidate));
    }

    [Fact]
    public void ReplacementRetryCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        int attempts = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => AtomicFile.Retry(() =>
        { attempts++; cancellation.Cancel(); throw Failure(1175); }, windows: true, cancellation.Token));
        Assert.Equal(1, attempts);
    }

    private static IOException Failure(int code) => new("injected atomic replacement failure", unchecked((int)0x80070000) | code);

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
