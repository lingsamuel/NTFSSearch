# NTFS Search

Windows NTFS 文件名与路径搜索技术原型，版本 0.1.0。

当前可运行桌面界面和命令行，支持静态索引、空格 AND、双引号短语、完整路径匹配、四列双向排序与分页。尚不能作为生产环境的 Everything 替代品：实时 USN 更新、后台服务、硬链接补全和快速恢复仍未实现，亿级即时体验尚未达标。

## 运行 Windows 原型

发布包需要 .NET 10 Desktop Runtime x64。当前开发机器已验证可以运行。

解压后运行 `启动搜索.cmd`，或直接运行 `app/NtfsSearch.App.exe`。默认载入 1000 条演示记录，不会自动扫描磁盘或启动已有的 Everything。

- 输入 `hello world`：两个条件都满足，允许不相邻。
- 输入 `"hello world"`：匹配含空格的连续文本。
- 默认匹配完整路径；取消选项后仅匹配名称，带反斜杠的条件仍匹配路径。
- 通过排序下拉框切换名称、完整路径、大小、修改时间，可选择降序。
- 通过“导入 EFU 清单”读取已有文件清单，或“打开索引”读取本工具保存的快照。
- 搜索包含精确总数，只为每页 200 条结果构造显示对象。

EFU 导入是获取已有元数据的一种方式；导入后搜索独立运行，不调用 Everything，不需要其保持运行。不会自动导出或修改现有 Everything 的数据库。

## NTFS 基础扫描

在 Windows 管理员终端运行发布包中的命令行程序，例如：

```powershell
.\cli\NtfsSearch.Cli.exe scan C: .\C.ntidx
```

然后用普通权限的桌面界面打开生成的索引。普通查询与打开快照不需要管理员权限。

扫描器使用 `FSCTL_ENUM_USN_DATA`，先收集目录关系，再流式建立名称索引。保留原始 UTF-16，检测无进展游标、目录循环和缓冲越界。当前支持 USN V2，遇到其他版本会停止并给出错误。

**扫描边界：** 这不是完整的一致性快照流程。扫描中持续发生的变动尚未通过 USN 回放校正；MFT 枚举未提供的大小、修改时间显示为未知，其他硬链接名称尚未补全。需要完整静态元数据时可使用 EFU 导入。不能把基础扫描的结果视为生产完整性验证通过。

当前会话只验证了非管理员情况下的明确权限错误，未执行管理员全卷扫描。没有更改系统 USN 日志配置。

## 快照与恢复

快照保存前写入同目录临时文件，计算 SHA-256、刷新落盘后替换。覆盖时保留上一代 `.bak`。校验失败时显示错误，用户可以直接打开 `.bak`，不会删除原文件或反复重试。

原型采用完整快照；加载先校验，再重建排序索引。冷启动成本、增量持久化、自动回退和实时恢复尚未优化，不能用热查询数字描述启动性能。

## 开发与验证

安装 .NET 10 SDK 后：

```sh
dotnet test NtfsSearch.slnx -c Release
dotnet run --project src/NtfsSearch.Cli -c Release -- demo demo.ntidx
dotnet run --project src/NtfsSearch.Cli -c Release -- search demo.ntidx '"hello world"' Size desc
dotnet run --project src/NtfsSearch.Cli -c Release -- import files.efu files.ntidx
dotnet run --project src/NtfsSearch.Cli -c Release -- bench 1000000 report.json shuffled 5
```

在 PowerShell 中执行 `scripts/build-windows.ps1` 构建 Windows 发布目录。WPF 本身只能在 Windows 运行，核心库与测试也可在 Linux 运行。

`app/NtfsSearch.App.exe --smoke-test <report.txt>` 可执行不显示窗口、不模拟键鼠输入的 WPF 自检。

## 当前容量测试

已在用户的 Windows 主机运行 3500 万和 1 亿条合成目录项测试。完整数值、输入分布与限制记录在 `docs/benchmarks`。

计时包括全量精确计数和全局排序后的首屏，未包含界面绘制、实时更新和生产文件标识映射。合成数据的名称较短；有序输入会显著降低排序构建成本。不能据此宣称真实数据已对标 Everything。

当前查询核心仍有全表扫描路径，亿级普通条件存在可感知延迟。后续应以代表性真实元数据定位热点，优化候选检索、重复查询复用与排序切换，同时计入实时更新结构的资源成本。

## 实现与参考

- 系统 MFT 枚举接口与 .NET 提供的字符串比较、CSV 解析、并行调度、哈希和原子替换能力直接复用。
- 记录采用 32 字节结构，原始名称放在分段 UTF-16 区域，每条记录只保存父路径编号，不复制完整路径。
- 查询使用临时匹配位图与精确计数；稀疏结果直接排序，大结果使用预建排列筛选。
- 已阅读 everyfind 的 MFT、连续名称区和目录关系实现作为参考，没有直接复制其源码。其许可证为 MIT/Apache-2.0；本原型不依赖该项目运行。

参考：[微软 MFT 接口](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)、[USN V2 格式](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2)、[everyfind](https://github.com/kyo5uke/everyfind)。
