namespace NtfsSearch.Core;

/// <summary>所有默认数据随程序目录移动；调用者可以显式传入其他数据目录。</summary>
public static class PortableDataDirectory
{
    public static string Resolve(string? executableDirectory = null) =>
        Path.GetFullPath(executableDirectory ?? AppContext.BaseDirectory);
}
