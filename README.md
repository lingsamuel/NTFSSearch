# NTFS Search

Windows NTFS 文件搜索工具，版本 0.2.0。

## 使用

解压 Windows 发布包，双击“启动搜索.cmd”。程序自动发现并读取所有已就绪的本地 NTFS 磁盘，搜索窗口直接显示真实磁盘的进度与结果。

首次本次采集需要 Windows UAC 授权。普通权限搜索窗口会自动启动独立管理员采集进程，一次授权处理所有卷；不用选择索引文件、输入盘符或打开管理员终端。采集进程跟随搜索窗口退出，不安装系统服务或修改 Everything。

缓存由程序自动维护，后续启动自动恢复已有结果，再在后台逐卷刷新。插入与移除 NTFS 磁盘会自动发现，完成一个卷就可以查询该卷。单卷失败会显示原因，其余卷继续处理。可用“重新扫描磁盘”重试，或“取消任务”停止本次采集。

发布包需要 .NET 10 Desktop Runtime x64；当前开发主机已验证具备运行环境。

## 搜索

- 默认匹配完整路径，可切换为仅匹配名称。
- `hello world`：两个条件都满足，允许不相邻。
- `"hello world"`：包含空格的连续文本。
- 带反斜杠的条件按路径匹配，支持跨父路径与文件名边界。
- 多卷结果按同一排序规则归并，显示精确总数；每页仅构造200条结果。
- 打开文件与所在目录、复制路径均使用当前桌面用户权限。

名称与路径排序用于自动扫描结果。核心也支持大小与修改时间排序，但基础 MFT 枚举目前尚未补全这些元数据；未知值显示“—”，不将 USN 时间冒充文件修改时间。

## 自动采集与缓存

- 使用卷 GUID 与挂载路径共同标识缓存，避免盘符复用、重新格式化或挂载变更串库。
- 每卷使用独立采集子进程；普通搜索窗口只负责加载和查询。取消、父进程退出和超时会停止采集任务。
- MFT 枚举保留原始 UTF-16 和完整文件引用。父目录缺失时通过 `OpenFileById` 补查；不可达或循环记录单独统计，不再使整盘失败。
- 快照 SHA-256 校验后加载；排序排列一并持久化，启动时验证并恢复，避免重新排序整个卷。
- 快照完成后才原子发布清单，保留上一代。损坏时自动回退并请求后台重建，同一损坏版本不会每两秒反复加载。
- 同卷发布使用跨进程互斥；读取允许原子替换，避免 Windows 文件共享冲突。卷进度和已完成记录数直接显示在窗口中。
- 全部本地缓存位于 `%LOCALAPPDATA%\NtfsSearch\Volumes`，由程序管理。

## 当前边界

这是持续开发中的实现，尚未完成文件级实时 USN 回放、硬链接补全和原生大小/日期采集。当前每次启动后台刷新各卷，运行期间自动发现卷的插拔；不会以持续全盘重扫冒充文件级实时更新。

已有快照可在后台刷新期间继续查询，直到新一代完整提交。扫描期间目录变动仍需后续 USN 回放保证完整一致性；不可达记录会显示统计。亿级即时体验仍需优化和真实数据对标，不能用0.1.0合成测试宣布达标。

## 开发与验证

```sh
dotnet test NtfsSearch.slnx -c Release
dotnet run --project src/NtfsSearch.Cli -c Release -- drives
dotnet run --project src/NtfsSearch.Cli -c Release -- bench 1000000 report.json shuffled 5
```

PowerShell 执行 `scripts/build-windows.ps1` 构建发布目录。WPF 只能在 Windows 运行，核心测试也可在 Linux 运行。

诊断入口：`app/NtfsSearch.App.exe --smoke-test report.txt` 执行不显示窗口的自动流程自检；`--probe-volumes report.json` 每卷只读取一个真实 MFT 批次；`--automatic-test report.json` 运行正常自动采集链路，首卷真实结果可查询后停止。

命令行保留 EFU 导入、快照查询和显式扫描作为开发诊断能力，不属于桌面正常使用步骤。0.1.0容量数据和当前验证记录见 `docs/benchmarks`。

参考：[微软 MFT 枚举](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)、[按文件ID打开](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid)、[USN V2格式](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)。此前阅读 everyfind 作为参考，本项目不依赖该程序运行。
