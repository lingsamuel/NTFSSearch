using NtfsSearch.App;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class AppProcessTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void RequestsShellElevationOnlyWhenAdministratorRightsAreRequiredAndMissing(
        bool elevate, bool hasAdministratorRights, bool useShellElevation)
    {
        var start = AppProcess.CreateStartInfo("NtfsSearch.Backend.exe", ["--memory-collector", "pipe", "42"],
            elevate, hasAdministratorRights);

        Assert.Equal(useShellElevation, start.UseShellExecute);
        Assert.Equal(useShellElevation ? "runas" : "", start.Verb);
        Assert.Equal(!useShellElevation, start.CreateNoWindow);
        Assert.Equal(new[] { "--memory-collector", "pipe", "42" }, start.ArgumentList);
    }
}
