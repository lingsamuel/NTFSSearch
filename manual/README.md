# NTFS Search 使用手册

## 启动

解压到 Windows 本地的可写目录，双击 `NtfsSearch.App.exe`。出现 UAC 提示时，授权程序读取磁盘。若已从托盘退出并以管理员身份重新启动，采集器会继承管理员权限。NTFS 磁盘的索引就绪后即可搜索。

所有版本都需要 WebView2 Runtime。`framework-dependent` 还需安装 x64 的 .NET Runtime 10 和 ASP.NET Core Runtime 10，`framework-dependent-no-http` 只需前者。`self-contained` 系列自带 .NET 运行时。

## 搜索

- 默认匹配完整路径，可在「搜索」菜单切换为「仅匹配名称」。
- `hello world` 要求两个词都出现，`"hello world"` 匹配连续短语。路径中的 `/` 和 `\` 可以混用。
- 用搜索框旁的「全部」筛选文件或文件夹。在「搜索 → 高级搜索」按日期、字节大小或后缀筛选，也可手动添加后缀。

## 空间分析

从搜索框旁打开「空间分析」，选择已索引的磁盘，或在地址栏输入本地目录路径。单击文件夹可展开，双击可进入该目录。右键「临时排除」可从统计中扣除条目，随后可从「已排除 N 项」恢复。

树图和搜索结果中的文件夹大小均按已索引文件的逻辑大小合计，硬链接按路径分别计入。

## 索引与升级

- 底栏「索引状态」可查看各卷进度和错误，也可手动重新扫描。
- 「设置 → 索引」中的创建时间索引默认关闭。更改时间或文件大小的索引选项后，须退出并重启。关闭文件大小索引也会停用空间分析。「设置 → 搜索」可单独关闭搜索结果的文件夹大小统计。
- 关窗后程序在托盘继续运行。从托盘菜单选择「退出」时，程序默认保存 `index.ntsnap`，也可点「此次不保存」跳过本次保存。
- 移动或升级前，先从托盘退出，再将 `settings.json` 和 `index.ntsnap` 放到目标 EXE 同目录。旧版文件可能在 `Data` 或 `app/Data`。

## 通过 HTTP 和 CLI 查询

带查询服务的便携版默认监听 `127.0.0.1:18791`。可在「设置 → 搜索服务」查看实际地址、启停服务、更改端口或复制查询命令。

HTTP 用 `GET /v1/status` 查看索引状态，用 `POST /v1/search` 提交 JSON 查询。CLI 使用同一份索引。

```powershell
.\NtfsSearch.Cli.exe status
.\NtfsSearch.Cli.exe search report
```

WSL 2 镜像网络可通过 `127.0.0.1` 访问 HTTP 接口，NAT 模式下可运行 Windows CLI。
