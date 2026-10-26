# 紧凑索引常驻内存修复

## 目标与取舍

用户明确要求实质压缩约5GiB常驻内存，并继续保持Everything级近瞬间排序切换，不接受放宽内存预算。现有3441万行的主要活对象：名称1596MiB、记录1050MiB、完整目录路径814MiB、目录图745MiB、四列排序525MiB、文件身份约298MiB。四列仅保存行号；改为只留一列最多省约394MiB。Windows同数据从头重排9卷的实际墙钟为名称5.992s、路径7.010s、大小6.397s、修改时间2.124s，因此保留预建排序，不牺牲瞬时切换来凑内存数字。

首轮压缩复用现有搜索、索引构建、身份查询和持久化逻辑：精确UTF16名称池，24字节记录，基线复用目录图。真实逐卷名称去重及8字节描述符估计省356MiB，记录32→24字节省263MiB，目录图目标去除约745MiB重复基线节点。此为结构预估，最终以相同输入Windows前台的工作集/私有内存/托管活对象及查询排序实测为准，不提前宣称达到Everything。

## 名称与记录（首轮方案，最终布局见后续补充）

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

## 验证（首轮，最终记录边界更新为20B）

先回归RED再实现：名称去重大小写/孤立代理项/跨块/长名、24B边界标志与完整long、V1-V4真实fixture、错引用/校验、四列双向排序和搜索同值；旧CP1与新CP2恢复、已压缩WAL尾读、不重扫、隐藏位图/段和目录图锚；混合旧路径、代际、父子同批移动删除。新图二分查找不得按目录产生临时对象。

固定真实9卷输入比较0.4.1与新版，记录名称唯一数、各数组/图驻留量、启动峰值、载入分配、查询/精确计数/首屏/四列切换/更新成本。只读生产数据，不停用户进程，不修改Everything库，不向生产卷注入大规模文件变动。结果未达目标就报告实际剩余成本，不用工作集修剪、强制GC或关闭功能伪造达标。

## 第二轮：基于真实量化的进一步压缩（候选探索）

首轮同真实输入实测核心活对象约3.62GiB，但Private约4.23GiB，其中LOH碎片约0.5GiB；不能将前者当作最终内存。继续在本轮未发布的V4协议中优化：

- 记录进一步20字节(Pack=4)：Modified64保持完整；SizeCode32低31位直接保存小文件大小，高位保存BigSizes数组索引，uint.MaxValue为未知。仅3045个真实大文件需要long表，预计净省131MiB。NameId和Directory仍各31位及各自标志；所有实际非负long大小和任意long修改值保持。逻辑大小统一通过SearchIndex.Size(row)解码。
- 本阶段候选（后由“名称编码最终候选”收敛）：文件名长度<=255且全部ASCII时允许一字节存储；任何目录名保留UTF16以继续借用基线名称内存。文件/目录同文字可分表示，真实额外成本小于0.5MiB，避免可变升级及隐式Span生命期。其余Unicode及孤立代理项原码UTF16保存。真实估计可再省523MiB。原型必须先验证原有Contains/StartsWith/OrdinalIgnoreCase和排序同值、延迟及CPU；名称读取使用显式caller scratch，不使用隐式线程ring或共享可变Span。
- 大型长寿命无引用数组与临时构建散列表分离分配：仅作为独立POH实验，采用框架zeroed AllocateArray(pinned:true)，小数组与临时growth普通分配。无GC.Collect/trim/GCpressure产品策略；仅实际Private/WS/碎片改善且生命周期测试通过才接入。不能以换堆导致的统计迁移冒充物理节省。

新旧指纹仍采用逻辑已知值规范化，SizeCode、ASCII物理编码和BigSizes索引均不参与canonical。旧V1-V3及CP1、已压缩WAL的恢复要求不变；V4/CP2尚未发布，开发中的schema调整必须同步真实fixture与损坏边界测试。

## 原生V4存储迁移与恢复

隔离同数据实测：20B+UTF16+POH的V3+CP1每次加载需49.77s/分配5.935GiB/自然Private3.926GiB，原生V4+CP2为37.38s/4.510GiB/3.663GiB，视图、旧指纹、seq/USN一致。为避免每次启动重新构建去重池，新版在成功恢复旧库及尾部后将纯基线后台转码为V4；不扫描MFT，不改变SnapshotFile GUID、基线逻辑行序、清单IndexedUtc/Journal或WAL目录。

转码在现有每卷publish独占租约下执行，再确认当前manifest仍引用相同基线。用新临时文件写完整V4并刷盘、验证SHA后才原子替换，保留原快照.bak，旧来源指纹写入V4。加载时若主快照损坏而同代.bak有效，先用同代恢复和原WAL/检查点衔接，再修复编码；此类编码回退不应误触发MFT重建。恢复写入不能覆盖唯一健康备份。普通磁盘满/锁冲突/取消仅保留原有效结果并重试或下次转换，不连带其他卷、不把存储优化失败当日志失效。

当前和前代快照备份随现有代际保护保留；退役基线回收时一并清理其格式备份。新增回归须验证源版/V4版+CP1及已压缩WAL相同、manifest及revision不变、原子替换中断、同代回退、过期generation不写、取消和并发publish。实际生产Data在开发中不动，全部验证在独立副本；新版正常使用时才会在软件Data中维护该编码。

## 名称编码最终候选及查询复用

全ASCII直接解码和单首字符候选匹配均曾实测CPU/延迟退化，未接入。最终仅压缩<=255 ASCII文件名且忽略数字标点后字母大小写最多一次转折：池中存小写字节，描述符记录len8/cut8、prefixUpper或suffixUpper及偏移；显示和逻辑指纹使用框架Ascii.ToUtf16/ToUpperInPlace准确还原原case。多转折camelCase、Unicode、孤立代理项以及所有目录名继续UTF16，目录名先前以文件出现时可原NameId升级UTF16。UTF16偏移47位、ASCII偏移46位，必须验证编码长度/cut/小写原码/边界。

ASCII查询先按框架转小写，直接复用byte IndexOf/StartsWith整段SIMD；非ASCII查询/源保留原UTF16 OrdinalIgnoreCase路径。名称排序仍用框架字符比较，绝不能将小写字节lex顺序等同OrdinalIgnoreCase（标点顺序不同）。所有借用解码由caller提供scratch，不使用隐式thread ring。

去重池允许一份按NameId懒memoize的首名称条件byte工作区：0未知、1不匹配、2匹配，只有目录状态需要查名时计算。CAS租用失败的并发查询走无缓存原流程，不分配额外全量工作区；不同predicate key清零，同一个唯一last key(文本+prefix/contains)可在排序/后台刷新间复用。名称池发布后不变，因此取消留下的部分结果对同key仍有效。只保留一个key，不留查询历史；workspace须计入内存。

最终基准必须分别记录冷首次与同key热查询，并与同策略UTF16控制公平对照；不能把warm值冒称冷延迟。每次descriptor单次解包，避免Length/AsciiName重复访问造成C盘匹配CPU增加。编码及memo回归覆盖随机ASCII可逆、全部UTF16/特殊case字符、四列双向、不同key并发与partial cancel复用。

## 全量诊断完成条件

FullAudit仅在所有发现卷的当前清单含有效Journal、当前generation已通过OnPublished进入前台、采集指标已到达且ActiveRoots为空、没有晚于当前IndexedUtc的rebuild请求时进入CRUD验证。就绪后等待2.5秒复核，查询结束后再次复核；期间出现修复则继续等待新代提交并发布，避免诊断退出取消尚未完成的修复。该限制仅影响诊断，正式界面仍即时展示已有可用缓存。

旧测试副本的NTFS日志自然过期时，首次启动允许并如实记录必要的单卷修复；修复完成后再验证后续启动不重扫，不删除请求标记或隐藏ScansStarted来制造零扫描结果。

## 排序常驻编码补充

四份预建排序均保留，不在用户切换时重算。每卷Count<=2^24时，行号可在内存中用3字节表示，末尾额外哨兵保证受控4字节读取不越界；超过范围自动使用32位，仍支持单卷/总量亿级。小段可保留普通int数组。当前9卷全部适用，预计再省约131MiB，必须以实际Private/WS验证。

磁盘V4及旧V1–V3仍写/读相同int32排序流；保存和加载用固定块流式转换，不增加新磁盘schema，也不能为保存重新物化四份全量int[]。排序构建继续复用SortIndex/Array.Sort，临时int和scratch普通分配，只有完成的紧凑向量为长寿命数组。保留alias引用、known边界、倒序、目录修正和CRC/排列/单调性校验，O(1)位置读取。回归必须覆盖24位边界及32位fallback、最后一个元素、所有四列/双向、跨目录改名、全部格式roundtrip。实际瞬间切换及大命中查询不得因编码明显退化。
