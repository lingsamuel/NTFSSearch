# NTFS Search

Windows 上的本地 NTFS 文件和文件夹搜索工具。

## 功能

- 自动索引本机 NTFS 磁盘，实时更新搜索结果。
- 按名称或路径搜索，支持短语匹配，以及类型、后缀、大小和日期筛选。
- 搜索结果支持排序、打开、复制、移动、重命名和删除。
- 用交互式树图查看磁盘和文件夹的空间占用。
- 带查询服务的版本支持本机 HTTP API 和实时 CLI 查询。

## 下载与运行

下载 Windows x64 便携 ZIP，解压到可写目录，双击 `NtfsSearch.App.exe`。出现 UAC 提示时，请授权程序采集磁盘索引。[GitHub Releases](https://github.com/lingsamuel/NTFSSearch/releases)

所有版本均需要 Microsoft Edge WebView2 Runtime。[下载 WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download)

| 便携包后缀 | HTTP API 和实时 CLI | 需另装的 .NET 10 运行时（x64） |
| --- | --- | --- |
| `framework-dependent` | 支持 | .NET Runtime 和 ASP.NET Core Runtime |
| `framework-dependent-no-http` | 无 | .NET Runtime |
| `self-contained` | 支持 | 无 |
| `self-contained-no-http` | 无 | 无 |

[下载 .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [使用手册](manual/README.md) · [开发文档](docs/development.md)
