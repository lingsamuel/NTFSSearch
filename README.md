# NTFS Search

Windows NTFS 文件搜索工具，版本 0.11.0。桌面界面使用 Tauri 和 Vue，.NET 负责索引和搜索。

## 使用

Windows 便携版解压后双击「启动搜索.cmd」。启动、搜索、索引状态和升级步骤见[使用说明](manual/README.md)。

## 搜索

- 默认匹配完整路径，可切换为仅匹配名称。
- `hello world`：两个条件都满足，允许不相邻。
- `"hello world"`：包含空格的连续文本。
- 带反斜杠的条件按路径匹配，支持跨父路径与文件名边界。
- 多卷结果按同一排序规则归并，显示精确总数；每页仅构造200条结果。
- “用户查询”耗时只在输入、排序、条件或翻页请求完成后更新。后台索引变化继续刷新结果和数量，但合并刷新请求，并保持用户耗时、当前页、仍存在的选中项和滚动位置。
- 打开文件与所在目录、复制路径均使用当前桌面用户权限。

支持名称、路径、文件大小和修改时间排序；大小和时间从真实 NTFS 元数据读取，增量更新也保持相同语义。USN 事件时间不会冒充文件修改时间。

## 空间分析

在「空间分析」中查看已索引 NTFS 卷的逻辑大小树图。矩形大小对应条目的字节数，可逐级进入目录。首次打开时在后台按选中卷汇总内存索引，随后随 USN 变更更新。目录合计按路径累计文件大小，因此同一个硬链接文件的多个路径会分别计入。

可在地址栏输入或粘贴本地目录路径，按 Enter 直接打开该目录的树图。

树图随矩形大小自动显示内部结构。单击任意层的文件夹临时展开，可沿子目录继续下探。双击将目录设为视图根。右键可打开文件和目录、选择打开方式或临时排除条目。已显示文件夹内有实时文件变化时会短暂高亮。

## 自动采集与退出快照

- 程序发现本机 NTFS 磁盘，管理员采集进程通过本地管道向普通权限搜索进程传输初扫数据和 USN 变更。卷完成后即可搜索。
- 运行时索引、排序、采集状态和增量均留在内存。搜索使用已发布的卷视图，过时记录按卷后台合并。日志丢失或手工重扫时重新读取对应卷。
- 正常退出时把各卷当前有效的文件元数据与 USN 水位逐卷用 Zstd 等级 3 单线程压缩，写成一份快照 `Data/index.ntsnap`。下次启动按卷解压并重建排序，按 USN 接续停机期间的变化。
- 可在「索引状态」启用「退出时不保存索引」。程序将偏好保存到 `Data/settings.json`，下次启动沿用。
- 0.10.0 启用压缩快照格式，升级后的首次启动从 NTFS 重建索引，正常保存后下次启动沿用压缩快照。明确启动开发诊断时按诊断说明使用其报告目录。
- 「索引状态」逐卷显示检查快照、加载、重建排序、USN 补读和全卷采集阶段。进入实时监控后仍显示该卷是从快照恢复还是经过全卷采集。
- 卷身份、硬链接和目录层级按完整 NTFS 文件引用记录。界面显示卷处理进度和错误原因。

## 内存与排序

名称精确去重，可逆压缩适用的ASCII文件名；其余保留原始UTF-16，显示大小写不变。记录由32字节压缩至20字节，大文件大小另存完整64位值；目录身份图复用基线名称与记录，仅为变化保留覆盖。大型长期数组采用独立分配区域，减少加载后碎片，不强制GC或修剪进程工作集。

四列排序只缓存行号排列，不复制四份文件数据。适用卷的行号由4字节压至3字节，超过范围自动使用4字节，不限制单卷容量。固定3441万对象的四列排列由525MiB降至394MiB。没有采用切换时全量重算：同机九卷实测重建一列需要2.12至7.01秒，舍弃其他三列只能省约394MiB，无法保留近瞬间切换。

## 当前边界

这是持续开发中的实现。0.4.1实际界面约3441万对象，私有内存5.35GiB；0.5.0相同九卷约3443万对象的最终界面自然私有内存约3.38GiB，重启无全卷扫描；详细测量见`docs/benchmarks/compact-memory-0.5.0`。完整记录区分自然进程占用、诊断GC后活对象、静态查询与实时界面，不能相互替代。硬链接补全后，与当前Everything库的剩余数量差仍在逐文件核对，不能以总数接近宣布完全一致。部分查询仍需遍历数千万行，尚未达到Everything的全部即时体验；初次完整索引、大规模变化与亿级容量仍需同机验收。

## 开发与验证

```sh
dotnet test NtfsSearch.slnx -c Release
dotnet run --project src/NtfsSearch.Cli -c Release -- drives
dotnet run --project src/NtfsSearch.Cli -c Release -- bench 1000000 report.json shuffled 5
```

在 Windows 安装 Node.js、Rust MSVC、Microsoft C++ Build Tools 和 .NET 10 SDK，使用 PowerShell 执行 `scripts/build-windows.ps1` 构建便携版。交付目录为项目工作区的 `artifacts/windows`。从 Windows 构建镜像或临时 Git worktree 构建时，也将成品放到该交付目录。 [构建与交付约定](AGENTS.md)

Windows 使用 WebView2 Runtime 渲染，核心测试也可在 Linux 运行。

诊断参数可传给便携版顶层的 `NtfsSearch.App.exe` 或 `NtfsSearch.Backend.exe`。`--smoke-test report.txt` 执行后端自动流程自检；`--probe-volumes report.json` 每卷少量 MFT 采样并核对元数据与 USN 状态；`--automatic-test report.json` 运行正常自动采集链路，首卷真实结果可查询后停止；`--parallel-test report.json` 使用隔离缓存验证实际多盘重叠读取，首卷可查且观察到至少两个采集进程后停止；`--verify-live report.json` 在专用测试目录验证实际硬链接、目录移动、检查点与停机回放；`--full-audit report.json` 使用报告旁的隔离数据目录等待全部卷进入增量模式，并记录本次启动的扫描卷，供重启免重扫验收。

便携版构建后，可运行 `node tests/desktop-window.mjs artifacts/windows/NtfsSearch.App.exe artifacts/desktop-window`，在 Windows 窗口中验证搜索交互和退出。该自检使用独立的演示索引。

便携版顶层的 `NtfsSearch.Cli.exe` 提供 EFU 导入、快照查询和显式扫描等开发诊断能力，与桌面后端共用同目录的程序集。0.1.0 容量数据和当前验证记录见 `docs/benchmarks`。

参考：[微软 MFT 枚举](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)、[按文件ID打开](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)、[USN V2格式](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)。此前阅读 everyfind 作为参考，本项目不依赖该程序运行。NTFS 数据流读取复用 DiscUtils，依赖与许可见 `docs/native-dependencies.md`。
