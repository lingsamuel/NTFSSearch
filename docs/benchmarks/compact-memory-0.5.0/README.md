# 0.5.0 常驻布局与排序取舍验收

本轮用户要求实质修复约5GiB内存，保持Everything近瞬间排序。保留4列预建排列及所有元数据；没有通过关闭实时更新、重扫、截断大小/日期或修剪工作集凑内存。

## 只保留当前排序的实际代价

Windows .NET10、32逻辑处理器、固定九卷34,408,942条从身份行序重建；9卷同时并行，计时包含新order/scratch/初始化但不包含载入和事后完整校验。Name5.992s、Path7.010s、Size6.397s、Modified2.124s，均校验单调。每列int32约131.26MiB，舍弃3列仅省393.78MiB；Everything SDK四列fast sort均true。因此没有采用按点击重算。

## 结构压缩

精确UTF16名称去重及可逆ASCII case-run编码，20B行及大文件大小旁表，基线目录图复用原记录/名称，仅留锚与变化墓碑。所有Unicode、孤立代理项和原始显示大小写保留。大型长期无引用数组分离到POH，构建散列表/增长数组普通分配；无产品强制GC/GCpressure/工作集修剪。

旧V1–V3可读，V4保留旧来源指纹并采用CP2逻辑指纹。恢复旧CP1/已回收WAL之后后台原位转码，纯基线逻辑行序/GUID/manifest时间/Journal/WAL不变；健康旧编码备份保留，同代损坏先回退而非MFT重建，候选验证后才原子替换。取消、并发发布、退出和后台队列都有回归。

## 分阶段结果口径

静态第一阶段（24B+UTF16池、不含实时图）84次旧新查询Total与全部首200路径/顺序一致；活对象4318.93→3700.83MiB，但载入后Private可能高于旧版，不能拿活对象冒充进程占用。名称解引用使CPU有成本，完整JSON保留该结果。

独立20B+UTF16+POH三格式对照均恢复34,414,502行，旧源189文件未变：V3+CP1自然Private3.926GiB、载入49.77s；V4+CP1为3.674/38.93s；V4+CP2为3.663/37.38s。V4减少每次迁移去重的暂时分配，故产品加入后台编码保存。该实验尚未含最终ASCII编码，不冒充最终版数字。

## 真实界面与副本日志过期

最终测试使用独立旧库副本，不停止或改动用户D:\NTFSSearch运行实例/数据库，也不修改Everything。原固定副本搁置数小时，D卷旧USN超出系统保留范围，按规则需仅修复D；早期FullAudit仅等前台恢复9卷就会过早取消正在进行的D修复，本轮诊断增加当前manifest/已发布generation一致、无待修复和无active scan的门槛，不改变正常界面仍可查已有结果的行为。

repair-complete报告如实记录ScansStarted=[D:]，待该必要修复提交后，verified-restart-1/2均ScansStarted=[]，9卷、约3443万对象，CRUD及离线补读true。这两次自然Private约3.47/3.44GiB，WorkingSet约3.45/3.43GiB；首次合法修复期间暂存旧新基线会更高。仍比Everything此前约3.12GiB私有高，不能宣称内存、所有查询或亿级性能全部达标。

开发中的ASCII直接解码、单首字符候选都被性能实测淘汰；最终版本保留冷首次/热同key的独立记录，不用热值冒称首次。首名称条件memo仅一个last key/有界byte workspace，CAS冲突走原未缓存匹配，不记录查询历史。

## 最终排序压缩对照

同源仅关闭packed策略的int32控制与最终packed独立进程，各84次查询。精确Total和全部返回路径/顺序一致。排序存储525.04→393.78MiB；静态诊断GC活对象3169.90→3038.63MiB，Private3392.57→3329.81MiB，后两项不能冒称真实界面结果或理论节省量。候选Core SHA256为45EBB324ADA85F7B553BD41B03E82A019C128613B941324DB3FA6971399B4A8A。

四列空查询0.218–0.329ms；a命中34050022条，四列约91–99ms；路径C:\Windows\System32按Path约221ms。三次采样只反映本次条件，不能当稳定上界或声称所有查询加速；例如.dll按Modified的中位数81.013→96.778ms。24个非空组合CPU中位数之和8500→8531ms，约+0.37%。见packed-sort-comparison.json及原始final4b/finalpacked日志。

名称编码最终原型独立日志保留在ascii-last-utf16/shape-results.jsonl；同一进程首个查询和后续重复查询分开，不拿memo命中的重复值冒称首次。0.4.1对照中readme首次约254ms，对照旧版中位约246ms，重复约195–206ms；无命中首次约150ms，旧版中位约105ms，尚有冷查询成本。ASCII直接逐字符解码与首字符候选等已淘汰策略不属于最终交付。

## 自动测试与诊断复用

最终Linux/Windows各212项通过，App/CLI Release发布成功；覆盖旧库、检查点、full FRN/Unicode、大小/时间范围、双向排序、24位最大ID/int32回退、65536块边界、损坏原始ID、并发和取消。

NativeLiveVerification每次需新的专用父目录：它会重建base但保留旧变化日志，重复使用原测试VerificationData会明确报“变化日志基线身份不符”。最终验证使用packed-native-fresh新目录，通过真实硬链接/目录移动/检查点/停机回放。此诊断夹具限制与正常Data按generation保存不相同，不清除或重置用户数据库。

## 最终发布二次实际界面重启

最终packed App（Core SHA同对照）自然进程占用，无强制GC；每次全部九卷，CRUD与离线补读均通过。均由缓存恢复并接续USN，没有MFT扫描；磁盘对象变化因此两次总数不必相同。

| 报告 | 对象数 | WS GiB | Private GiB | GC堆读数 GiB | 扫描卷 |
| --- | ---: | ---: | ---: | ---: | --- |
| packed-restart-1.json | 34,431,998 | 3.360 | 3.379 | 3.216 | 无 |
| packed-restart-2.json | 34,432,020 | 3.302 | 3.316 | 3.148 | 无 |

这里GC堆读数来自GC.GetTotalMemory(false)，没有收集垃圾，不能当精确活对象。0.4.1实际界面Private5.352GiB，当前约3.38GiB，下降约37%；Everything早先约3.12GiB仍更低。0.5.0尚未完成亿级同机验收，不宣称所有查询/排序/大规模更新达标。
