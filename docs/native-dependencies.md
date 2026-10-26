# 原生 NTFS 与 USN 依赖约定

完整索引使用 `DiscUtils.Ntfs 0.16.13`（MIT）提供 `$MFT` 数据流映射，传递依赖为相同版本的 `DiscUtils.Core` 与 `DiscUtils.Streams`。原始卷以只读、可定位流打开，不允许库修复或写入卷；碎片、非驻留数据与属性列表交由成熟库处理，不另外实现 runlist。按第二遍 FSCTL 枚举的 FRN 顺序读取对应 MFT 基础记录，逻辑缓存为 1 MiB，物理读取缓存为 64 KiB，只为多硬链接候选打开对象句柄。

基础记录解析只负责 USA 校验、代际、硬链接计数、标准信息修改时间及未命名 DATA 长度。FRN 保留完整 64 位（包括高 16 位代际），名称始终保留 UTF-16 代码单元。多硬链接文件使用 `NtQueryInformationFile(FileHardLinkInformation=46)` 补齐所有名称及父 ID；`FileNameLength` 单位为字符，条目偏移按原生 8 字节对齐。

实时读取同时记录日志 ID 与 USN，读取前验证 `FirstUsn`、`LowestValidUsn` 和 `NextUsn`。READ 请求优先使用原生 48 字节 V1 布局，兼容 44 字节 V1 与 40 字节 V0；V1 将最小/最大记录版本固定为 2，V0 返回非 V2 时明确报错。按批次截断时，将下一条未处理记录的 USN 作为返回水位，禁止跳过缓冲区尾部。

全量基线通过 `ScanWithJournal` 在枚举前启动磁盘捕获，后台持续读取日志以跨越卷日志保留窗口。返回的 `Position` 是扫描前水位；`Capture` 在扫描后继续运行，由发布流程完成基线保存、回放和追平后关闭。调用者先 `Stop`、再 `ReadCaptured` 排空，并从最终 `Position` 正常追读。捕获排除自身 spool 与工作目录 FRN，调用者仍需按数据根过滤其余索引文件事件。捕获异常导致该次基线不可发布。只有日志不存在（`ERROR_JOURNAL_NOT_ACTIVE`）时创建，绝不重设已有日志。

官方结构依据：

- [FILE_LINK_ENTRY_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_link_entry_information)
- [FILE_LINKS_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_links_information)
- [NtQueryInformationFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntqueryinformationfile)
- [READ_USN_JOURNAL_DATA_V1](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v1)

持续监控可向 `Capture` 传入已提交的起始位置；起点先验证日志ID和保留范围。仅在spool和监控卷的句柄VOLUME_NAME_GUID卷根相同时过滤其自身FRN，避免跨卷FRN相同或克隆卷序列号相同误删事件。捕获读取复用1 MiB缓冲，消费完磁盘帧后在生产/消费共用锁内截断并重置偏移，保持水位连续且释放磁盘空间。

`ReferenceForPath` 与 `CanonicalPathForPath` 复用句柄查询，支持扩展路径、UNC和目录junction。扫描的可选 `excludeDirectory` 先转实际目标路径，再按父路径与名称分量过滤目录及后代；多硬链接文件仅过滤内部链名，保留外部链名，不为每条记录额外分配完整路径。

排除边界目录移出时，`EnumerateSubtree` 使用框架单层流式目录枚举补采，按完整FRN去重，目录先于子项，reparse目录仅产生自身状态而不跟随；每项以卷序列号验证未离开指定卷。仅保留已见FRN集合与当前目录深度的迭代器，不物化整棵目录树，并复用同一个卷句柄读取完整对象状态。调用者负责将枚举结果分为有界持久事务。

Windows实机受控测试发现，删除最后一个硬链接后按旧FRN打开可能返回 `ERROR_INVALID_PARAMETER(87)`。该错误不能直接等价于删除：仅对87额外调用 `FSCTL_GET_NTFS_FILE_RECORD` 验证当前slot。该控制码按官方约定返回不大于请求slot的首个in-use记录；只有返回slot更早、原代际已变化、in-use清除或链接计数归零，才产生空Links删除态。若相同代际仍有链接，则保留87异常并报告完整FRN；额外读取或边界校验失败也不会静默吞掉错误。固定头字段均位于第一个扇区内，此确认不重新解析runlist或属性列表。

- [FSCTL_GET_NTFS_FILE_RECORD](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record)
- [NTFS_FILE_RECORD_OUTPUT_BUFFER](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_file_record_output_buffer)

管理员采集启动时使用 `AdjustTokenPrivileges` 仅启用当前进程已有的 `SeBackupPrivilege`，配合 `FILE_FLAG_BACKUP_SEMANTICS` 读取克隆系统卷的受ACL保护对象。返回TRUE但 `ERROR_NOT_ALL_ASSIGNED` 仍视为失败；不会授予新权限，不启用 `SeRestorePrivilege` 或写入/所有权权限。常规原始卷与捕获句柄为只读；仅确认日志缺失后短暂另开读写句柄创建USN日志。失败诊断包含原生阶段、完整FRN（必要时父FRN/slot）或日志ID/USN，禁止将读取失败直接当作空硬链接集合。

- [AdjustTokenPrivileges](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-adjusttokenprivileges)
- [Privilege Constants](https://learn.microsoft.com/en-us/windows/win32/secauthz/privilege-constants)

实机确认部分克隆系统卷的 `$Extend\$RmMetadata\$TxfLog\$TxfLogContainer...` 对class46返回87，普通文件也可能受分享锁限制。此类失败改用DiscUtils公开 `GetMasterFileTable()` 读取完整FILE_NAME属性集合及其ATTRIBUTE_LIST引用；校验扩展记录完整代际、基础记录身份和属性ID，过滤DOS短名称别名。名称直接从属性Content读取UTF-16，保留未配对代理项；父目录路径通过同一原始MFT迭代装图后复用现有DirectoryPathResolver，避免递归深度风险和再次触发父目录分享锁。

fallback只接受class46的87/50及按ID打开/父目录打开的访问或分享限制5/32/33。原始状态读取失败会报告原阶段与fallback错误，不会跳过整个对象或返回伪空链接。普通OpenById的87仍须经过既有MFT删除确认；普通GetInfo参数错误继续显式失败。基线复用已经打开的MFT reader，单文件增量仅在需要fallback时建立只读reader；子树补采按需建立一次reader并复用于该次枚举，在正常结束、取消或提前退出时统一释放。对于已标记零链接的基础记录，原始读取返回删除态，扩展记录的零链接计数不适用该判断。

候选判断不直接使用MFT header的LinkCount，因为该计数包含DOS短名称属性。基础记录遍历同时无分配计数非DOS FILE_NAME；只有多个长名称，或出现ATTRIBUTE_LIST可能把其他名称放在扩展记录时，才进入按对象补链。namespace与长度校验复用raw fallback既有验证函数，避免另造名称解析器。原始卷读取失败会附盘符、物理偏移和请求长度，MFT记录读取亦包装UnauthorizedAccessException并保留FRN和内部异常，避免与软件Data目录权限失败混淆。

0.5.0的旧快照转码使用框架`MemoryMappedFile`只读映射旧UTF16名称区，加载结束即释放；不映射原始卷或写入旧快照。常驻布局与排序压缩仅使用.NET内建API，无新增原生依赖；名称编码保留原始UTF16语义。
