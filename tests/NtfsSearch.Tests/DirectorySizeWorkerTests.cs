using System.Threading.Channels;
using NtfsSearch.App;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class DirectorySizeWorkerTests
{
    [Fact]
    public async Task DisablingDetachesOrdersAndReenablingUsesTheNewestVolume()
    {
        var volume = new NtfsVolume("settings-test", @"C:\", 1, "测试卷");
        string key = VolumeCache.Key(volume);
        var catalog = new SearchCatalog();
        var published = Channel.CreateUnbounded<PublishedSearchVolume>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var worker = new DirectorySizeWorker(catalog, () => [(volume, catalog.PublishedLayers(key)!)],
            () => published.Writer.TryWrite(catalog.FindVolume(key)!));
        catalog.Changed += worker.Notify;
        Task running = worker.RunAsync(timeout.Token);
        try
        {
            catalog.Publish(key, Index(10));
            var first = await published.Reader.ReadAsync(timeout.Token);
            Assert.Equal((Int128)10, Size(first));

            Assert.True(worker.SetEnabled(false));
            Assert.Null(first.DirectorySizes);
            catalog.Publish(key, Index(23));
            var hidden = catalog.SearchDesktop("", SortField.Size, true, true, 0, 20, default, includeDirectorySizes: false);
            Assert.False(hidden.DirectorySortPending);
            Assert.Null(hidden.DirectoryRows);
            Assert.Equal("file", hidden.Rows[0].Name);
            Assert.Null(catalog.FindVolume(key)!.DirectorySizes);

            Assert.True(worker.SetEnabled(true));
            var second = await published.Reader.ReadAsync(timeout.Token);
            Assert.NotSame(first, second);
            Assert.Equal((Int128)23, Size(second));
            Assert.Null(first.DirectorySizes);
        }
        finally
        {
            timeout.Cancel();
            await running;
        }
    }

    private static SearchIndex Index(long size)
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "folder", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\folder\", "file", size, fileReference: 11, parentReference: 10);
        return builder.Build();
    }

    private static Int128? Size(PublishedSearchVolume volume)
        => Assert.Single(volume.DirectorySizes!).GetSize(volume.Layers[0].FindFileRows(10)[0]);
}
