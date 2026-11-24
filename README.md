# NTFS Search

Windows NTFS 文件搜索工具，版本 0.25.0。桌面界面使用 Tauri 和 Vue，.NET 负责索引和搜索。

## Windows x64 发布包

四种便携 ZIP 均支持桌面实时搜索、空间分析和 CLI 静态诊断。桌面界面使用系统的 WebView2 Runtime。[GitHub Releases](https://github.com/lingsamuel/NTFSSearch/releases) · [WebView2 Runtime 下载](https://developer.microsoft.com/en-us/microsoft-edge/webview2/#download)

| ZIP 文件名后缀 | HTTP API 和实时 CLI | 需安装的 .NET 运行时（Windows x64） | 随包提供的 .NET 运行时 |
| --- | --- | --- | --- |
| `framework-dependent` | 支持 | .NET Runtime 10 和 ASP.NET Core Runtime 10 | 无 |
| `framework-dependent-no-http` | 无 | .NET Runtime 10 | 无 |
| `self-contained` | 支持 | 无 | .NET Runtime 10 和 ASP.NET Core Runtime 10 |
| `self-contained-no-http` | 无 | 无 | .NET Runtime 10 |

需要安装 .NET 运行时时，请按表中的运行时名称选择 Windows x64 安装程序。[.NET 10 下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

## 使用

Windows 便携版解压后双击「启动搜索.cmd」。启动、搜索、索引状态和升级步骤见[使用说明](manual/README.md)。

关闭搜索窗口后，程序留在托盘继续更新索引。左键点击托盘图标恢复窗口，右键选择「退出」结束程序。

在「设置 → 通用」切换桌面语言，默认跟随 Windows 显示语言。简体中文和英文随程序提供。[翻译贡献指南](docs/localization.md)

## 搜索

- 默认匹配完整路径，可切换为仅匹配名称。
- `hello world`：两个条件都满足，允许不相邻。
- `"hello world"`：包含空格的连续文本。
- 带反斜杠的条件按路径匹配，支持跨父路径与文件名边界。
- 多卷结果按同一排序规则归并，显示精确总数；每页仅构造200条结果。
- “用户查询”耗时只在输入、排序、条件或翻页请求完成后更新。后台索引变化继续刷新结果和数量，但合并刷新请求，并保持用户耗时、当前页、仍存在的选中项和滚动位置。
- 打开文件与所在目录、复制路径均使用当前桌面用户权限。

支持名称、路径、大小和修改时间排序。桌面大小列包含目录内已索引文件的逻辑大小合计，按卷后台汇总后自动显示，并与文件按大小一起排序。文件大小和时间从 NTFS 元数据读取，实时变化会更新结果。

带 HTTP 服务的版本中，本机 HTTP 接口与 CLI 使用同一份实时索引，支持创建时间、修改时间和文件大小的组合筛选。默认地址为 `http://127.0.0.1:18791`。可从主窗口或托盘打开「设置」，启停服务、修改端口或重启服务。[接口及 CLI 用法](manual/README.md#通过-http-和-cli-查询)

「设置 → 索引」可分别控制预计算排序、创建时间、修改时间和文件大小。预计算排序保存后立即生效，三个元数据设置在下次启动生效。只有创建时间默认关闭。关闭文件大小会停用大小列、大小排序和筛选、文件夹大小统计及空间分析。[索引设置说明](manual/README.md#调整索引内存占用)

## 空间分析

在「空间分析」中查看已索引 NTFS 卷的逻辑大小树图。矩形大小对应条目的字节数，可逐级进入目录。首次打开时在后台按选中卷汇总内存索引，随后随 USN 变更更新。目录合计按路径累计文件大小，因此同一个硬链接文件的多个路径会分别计入。

可在地址栏输入或粘贴本地目录路径，按 Enter 直接打开该目录的树图。

树图随矩形大小自动显示内部结构。单击任意层的文件夹临时展开，可沿子目录继续下探。双击将目录设为视图根。右键可打开文件和目录、选择打开方式或临时排除条目。已显示文件夹内有实时文件变化时会短暂高亮。

## 自动采集与退出快照

- 程序发现本机 NTFS 磁盘，管理员采集进程通过本地管道向普通权限搜索进程传输初扫数据和 USN 变更。卷完成后即可搜索。
- 运行时索引、排序、采集状态和增量均留在内存。搜索使用已发布的卷视图，过时记录按卷后台合并。日志丢失或手工重扫时重新读取对应卷。
- 正常退出时把各卷当前有效的文件元数据与 USN 水位逐卷用 Zstd 等级 3 单线程压缩，写成程序同目录的 `index.ntsnap`。下次启动按卷解压、按设置建立排序，再按 USN 接续停机期间的变化。
- 在「设置」可关闭搜索结果中的文件夹大小统计，并设置是否退出时保存索引。「索引状态」也可即时切换退出保存。偏好保存到程序同目录的 `settings.json`。
- 退出保存时可选择「此次不保存」，程序清理本次临时快照后退出。进入最终替换阶段后会完成保存。
- 启用旧快照缺少的元数据字段时，程序只为缺少所需字段的卷重新采集。正常退出保存后，下次可使用新的快照。
- 「索引状态」逐卷显示检查快照、加载、重建排序、USN 补读和全卷采集阶段。进入实时监控后仍显示该卷是从快照恢复还是经过全卷采集。
- 卷身份、硬链接和目录层级按完整 NTFS 文件引用记录。界面显示卷处理进度和错误原因。

## 内存与排序

名称精确去重。适用的 ASCII 文件名采用可逆压缩，其余名称保留原始 UTF-16。基础记录占 8 字节，启用的大小和时间字段各自占用独立的列。大文件大小另存完整 64 位值。目录身份图复用基线名称与记录，仅为变化保留覆盖。大型长期数组采用独立分配区域，减少加载后碎片。

排序缓存保存行号。适用卷的行号由 4 字节压至 3 字节，超过范围自动使用 4 字节。关闭预计算后，首次查询或切换到尚未缓存的排序列时按需计算。同一列升降序和翻页可复用排列。文件大小、修改时间和创建时间各自独立存储，关闭相应设置可跳过整列分配。

## 当前边界

这是持续开发中的实现。0.4.1实际界面约3441万对象，私有内存5.35GiB；0.5.0相同九卷约3443万对象的最终界面自然私有内存约3.38GiB，重启无全卷扫描；详细测量见`docs/benchmarks/compact-memory-0.5.0`。完整记录区分自然进程占用、诊断GC后活对象、静态查询与实时界面，不能相互替代。硬链接补全后，与当前Everything库的剩余数量差仍在逐文件核对，不能以总数接近宣布完全一致。部分查询仍需遍历数千万行，尚未达到Everything的全部即时体验；初次完整索引、大规模变化与亿级容量仍需同机验收。

## 开发与验证

```sh
dotnet test NtfsSearch.slnx -c Release
dotnet run --project src/NtfsSearch.Cli -c Release -- drives
dotnet run --project src/NtfsSearch.Cli -c Release -- bench 1000000 report.json shuffled 5
```

在 Windows 安装 Node.js、Rust MSVC、Microsoft C++ Build Tools 和 .NET 10 SDK，使用 PowerShell 执行 `scripts/build-windows.ps1` 构建便携版。交付目录为项目工作区的 `artifacts/windows`。从 Windows 构建镜像或临时 Git worktree 构建时，也将成品放到该交付目录。 [构建与交付约定](AGENTS.md)

使用 `-SelfContained` 随包发布运行时，使用 `-WithoutHttp` 去掉 HTTP 服务与 ASP.NET Core 依赖。`-PackageAll` 一次生成上表中的四种 ZIP，默认放入 `artifacts/windows/packages`。各包均省略调试符号。每次分支推送生成 Nightly，在仓库的 Actions 产物有效期内最多保留最近四次。推送 tag 时，GitHub Actions 将四种 ZIP 作为该 tag 的 Release 附件发布。

核心测试也可在 Linux 运行。

诊断参数可传给便携版顶层的 `NtfsSearch.App.exe` 或 `NtfsSearch.Backend.exe`。`--smoke-test report.txt` 执行后端自动流程自检；`--probe-volumes report.json` 每卷少量 MFT 采样并核对元数据与 USN 状态；`--automatic-test report.json` 运行正常自动采集链路，首卷真实结果可查询后停止；`--parallel-test report.json` 使用隔离缓存验证实际多盘重叠读取，首卷可查且观察到至少两个采集进程后停止；`--verify-live report.json` 在专用测试目录验证实际硬链接、目录移动、检查点与停机回放；`--full-audit report.json` 使用报告旁的隔离数据目录等待全部卷进入增量模式，并记录本次启动的扫描卷，供重启免重扫验收。

便携版构建后，在独立 Windows 测试桌面运行 `tests/desktop-window.mjs` 验证搜索交互和退出。`--search-results` 验证匹配加粗和结果表滚动，`--advanced-search` 验证顶部菜单、多选筛选和空间分析的快捷切换。该自检使用独立的演示索引。 [独立 Windows 测试桌面指南](docs/windows-test-desktop.md)

`--desktop-interaction` 验证窗口激活、设置草稿、多选、框选和文件拖放的原生调用，可在独立的非输入桌面运行。`--native-file-drag` 使用 Edge 测试跨目录文件上传，并在运行过程中移动真实鼠标，请在独立 Windows 虚拟机或专用测试会话运行。

带 HTTP 服务的便携版可用顶层的 `NtfsSearch.Cli.exe` 查询实时索引。所有版本均可用 EFU 导入、静态快照查询和显式扫描进行开发诊断。0.1.0 容量数据和当前验证记录见 `docs/benchmarks`。

参考：[微软 MFT 枚举](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)、[按文件ID打开](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)、[USN V2格式](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)。此前阅读 everyfind 作为参考，本项目不依赖该程序运行。NTFS 数据流读取复用 DiscUtils，依赖与许可见 `docs/native-dependencies.md`。
