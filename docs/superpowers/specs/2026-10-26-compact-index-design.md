# 紧凑索引常驻内存修复

## 目标与取舍

用户明确要求实质压缩约5GiB常驻内存，并继续保持Everything级近瞬间排序切换，不接受放宽内存预算。现有3441万行的主要活对象：名称1596MiB、记录1050MiB、完整目录路径814MiB、目录图745MiB、四列排序525MiB、文件身份约298MiB。四列仅保存行号；改为只留一列最多省约394MiB。Windows同数据从头重排9卷的实际墙钟为名称5.992s、路径7.010s、大小6.397s、修改时间2.124s，因此保留预建排序，不牺牲瞬时切换来凑内存数字。

首轮压缩复用现有搜索、索引构建、身份查询和持久化逻辑：精确UTF16名称池，24字节记录，基线复用目录图。真实逐卷名称去重及8字节描述符估计省356MiB，记录32→24字节省263MiB，目录图目标去除约745MiB重复基线节点。此为结构预估，最终以相同输入Windows前台的工作集/私有内存/托管活对象及查询排序实测为准，不提前宣称达到Everything。

## 名称与记录

NameArena对完全相同、区分大小写的UTF16名称去重，不规范化孤立代理项或Unicode。以31位NameId引用名称，唯一名称的ulong描述符存逻辑字符偏移及16位长度；构建期复用.NET的HashSet alternate Span查询，Seal释放散列表。沿用ChunkChars逻辑块与完整非末块约定。

IndexRecord固定24字节：Size和Modified各64位，NameId和Directory各32位。NameId高位保存HasModified，Directory高位保存IsDirectory；Size=-1表示未知且已知大小仍完整支持所有非负long，Modified保留完整long及独立已知标志。名称数、目录数受已有int索引上限约束。域值与排序、搜索、显示语义保持不变。

V4快照保存去重池描述符、名称块和24字节记录。V1-V3可读并在加载时流式转换，旧名称区用有界只读映射提供Span，不再同时创建旧、新两套全名称堆数组。原文件校验、索引引用/排序排列/单调性验证不能省略。加载旧库不触发MFT重扫；新库仍存exe旁Data。

## 目录图复用

DirectoryGraph基线直接引用原始SearchIndex，以现有完整FRN二分查找和无分配FindFileRange获取目录节点，名称借NameMemory。只单独保存缺失父目录的RootPath锚与真实增量的不可变覆盖/删除墓碑，不为所有基线目录创建对象或字符串。DirectoryNode采用值类型与ReadOnlyMemory<char>名称，按内容比较。

保留旧图完整祖先身份筛选，不以路径前缀假定两遍扫描/硬链接路径一致。覆盖完整FRN代际、父移动/子移出、目录删除、循环与取消回滚。不得从更新后层的隐藏行或覆盖路径推断原基线节点。构造、恢复和祖先查询的二分成本必须单独实测。

## 检查点及兼容

旧NTSLIVE1使用原物理表示指纹，名称压缩改变NameOffset后不能直接重新计算并拒绝旧检查点。旧快照加载须按旧算法分65536行交错hash原始32字节记录与原始FRN，追加原父ID和原名称块，缓存到SearchIndex.CheckpointFingerprint；V4可携带来源旧指纹以支持同逻辑快照另存后的旧检查点。

新增CanonicalCheckpointFingerprint及IndexFingerprint.Canonical，以显式长度的目录文本/父ID和逻辑行序的FRN、Directory、已知标志、大小、修改时间及原始UTF16名称作为输入；不包含物理NameOffset/NameId/StorageId，未知值统一规范化。使用有界缓冲追加SHA，不能逐行产生字符串或哈希对象。

NTSLIVE2保存逻辑指纹、层视图与紧凑目录覆盖。NTSLIVE1仍可逐节点读取并和基线比较；同值不分配字符串、不留覆盖，旧锚、新增/更名/删除必须完整保留。重复及缺失关键图项不能因基线复用而静默掩盖。兼容WAL已压缩至检查点之后的场景，不允许因指纹或名称布局改变而强制全盘重扫。

## 模块契约

名称/记录模块负责NameArena、IndexRecord、IndexBuilder、SnapshotStore、SearchIndex及名称访问调用点。提供internal FindFileRange(ulong)->(Start,Count)、NameMemory(int)->ReadOnlyMemory<char>、CanonicalCheckpointFingerprint字段；现有CheckpointFingerprint明确保留旧物理源指纹。

目录/检查点模块负责DirectoryGraph、LiveVolumeIndex、DirectoryAncestryFilter、LiveViewStore、IndexFingerprint。旧raw指纹算法集中到共享helper供旧快照加载使用，不分裂复制同一实现。两模块以相同逻辑名称/元数据/身份契约交接。

## 验证

先回归RED再实现：名称去重大小写/孤立代理项/跨块/长名、24B边界标志与完整long、V1-V4真实fixture、错引用/校验、四列双向排序和搜索同值；旧CP1与新CP2恢复、已压缩WAL尾读、不重扫、隐藏位图/段和目录图锚；混合旧路径、代际、父子同批移动删除。新图二分查找不得按目录产生临时对象。

固定真实9卷输入比较0.4.1与新版，记录名称唯一数、各数组/图驻留量、启动峰值、载入分配、查询/精确计数/首屏/四列切换/更新成本。只读生产数据，不停用户进程，不修改Everything库，不向生产卷注入大规模文件变动。结果未达目标就报告实际剩余成本，不用工作集修剪、强制GC或关闭功能伪造达标。
