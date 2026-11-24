# 开发与验证

桌面界面使用 Tauri 和 Vue，索引和搜索使用 .NET。Windows 磁盘采集依赖及许可见 [原生依赖](native-dependencies.md)，翻译流程见 [本地化指南](localization.md)。

## Windows 便携版

在 Windows 安装 Node.js、Rust MSVC 工具链、Microsoft C++ Build Tools 和 .NET 10 SDK。从仓库根目录使用 PowerShell 构建。

```powershell
./scripts/build-windows.ps1
./scripts/build-windows.ps1 -PackageAll
```

- 默认在 `artifacts/windows` 生成可运行的便携版。使用 `-SelfContained` 随包提供运行时，使用 `-WithoutHttp` 去掉 HTTP 服务、实时 CLI 和 ASP.NET Core 依赖。
- `-PackageAll` 生成四种组合的 ZIP，默认输出到 `artifacts/windows/packages`。可用 `-OutputDirectory` 指定交付目录。
- 便携版以顶层的 `NtfsSearch.App.exe` 启动。构建与升级要求见 [仓库协作约定](../AGENTS.md)。
- 分支推送后构建 Nightly，在 Actions 产物有效期内保留最近四次。推送 tag 后重建对应提交并发布到 GitHub Releases。[Windows 打包工作流](../.github/workflows/windows-packages.yml)

## 测试与诊断

核心测试可在 Windows 或 Linux 使用 `dotnet test tests/NtfsSearch.Tests -c Release --filter '<相关测试筛选条件>'` 运行。桌面用例通过 `tests/desktop-window.mjs` 直接启动便携版 EXE。隔离方式与命令见 [Windows 测试桌面](windows-test-desktop.md)。

以下诊断参数接受一个报告路径，可传给便携版顶层的 `NtfsSearch.App.exe` 或 `NtfsSearch.Backend.exe`。

| 参数 | 验证内容 |
| --- | --- |
| `--smoke-test` | 后端自动流程自检 |
| `--probe-volumes` | 按卷采样 MFT，核对元数据与 USN 状态 |
| `--automatic-test` | 正常自动采集至首卷可查询 |
| `--parallel-test` | 使用隔离缓存验证多盘同时采集 |
| `--verify-live` | 在独立测试目录验证硬链接、目录移动、检查点和停机回放 |
| `--full-audit` | 用报告旁的隔离数据目录等待各卷进入增量模式，记录本次启动扫描的卷 |

各版本的 CLI 均支持 EFU 导入、静态快照查询和显式扫描。

```sh
dotnet run --project src/NtfsSearch.Cli -c Release -- drives
dotnet run --project src/NtfsSearch.Cli -c Release -- bench 1000000 report.json shuffled 5
```

性能基线、测量方法和原始结果见 [性能测试](benchmarks/README.md)。
