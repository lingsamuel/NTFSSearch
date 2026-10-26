using System.Runtime.CompilerServices;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

[CollectionDefinition("BuilderRetention", DisableParallelization = true)]
public class BuilderRetentionCollection;

[Collection("BuilderRetention")]
public class BuilderRetentionTests
{
    [Fact]
    public void RetainingCompletedBuilderDoesNotRetainDuplicateLargeSizeTable()
    {
        IndexBuilder?[] retained = new IndexBuilder?[1];
        var index = Build(retained);
        long withBuilder = GC.GetTotalMemory(forceFullCollection: true);
        retained[0] = null;
        long withoutBuilder = GC.GetTotalMemory(forceFullCollection: true);
        GC.KeepAlive(index);
        Assert.True(withBuilder - withoutBuilder < 64 * 1024,
            $"构建器额外持有 {withBuilder - withoutBuilder:N0} 字节，疑似重复保留大小旁表。");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SearchIndex Build(IndexBuilder?[] retained)
    {
        var builder = new IndexBuilder(12000);
        for (int i = 0; i < 12000; i++) builder.Add(@"C:\", "same.bin", (long)int.MaxValue + i + 1);
        retained[0] = builder;
        return builder.Build();
    }
}
