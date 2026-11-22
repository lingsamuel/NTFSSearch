using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class DesktopSettingsTests
{
    [Fact]
    public void ExitSavePreferenceSurvivesRestartAlongsideTheExistingSnapshot()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(DesktopSettings.Load(directory).SkipExitIndexSave);
            Assert.False(Directory.Exists(directory));
            Directory.CreateDirectory(directory);
            string snapshot = Path.Combine(directory, ExitSnapshotStore.FileName);
            byte[] existing = [2, 4, 6, 8];
            File.WriteAllBytes(snapshot, existing);

            new DesktopSettings(SkipExitIndexSave: true).Save(directory);
            Assert.True(DesktopSettings.Load(directory).SkipExitIndexSave);
            Assert.Equal(existing, File.ReadAllBytes(snapshot));

            new DesktopSettings().Save(directory);
            Assert.False(DesktopSettings.Load(directory).SkipExitIndexSave);
            Assert.Equal(existing, File.ReadAllBytes(snapshot));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
