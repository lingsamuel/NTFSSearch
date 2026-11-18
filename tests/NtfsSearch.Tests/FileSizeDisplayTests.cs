using System.Globalization;
using NtfsSearch.App;
using Xunit;

namespace NtfsSearch.Tests;

public class FileSizeDisplayTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(long.MaxValue, "8 EB")]
    [InlineData(null, "—")]
    public void FormatSelectsUnitFromActualBytes(long? bytes, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal(expected, FileSizeDisplay.Format(bytes));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
