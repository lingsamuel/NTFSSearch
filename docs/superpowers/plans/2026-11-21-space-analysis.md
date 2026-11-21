# 空间分析实现计划

**目标** 依据现有 NTFS 索引显示可逐级下钻、随索引更新的逻辑大小树图。

**技术** .NET 10 原有索引与桌面后端，Tauri 消息通道，Vue 和 TypeScript。

## 目录汇总

- [x] 在 `tests/NtfsSearch.Tests/SpaceUsageTests.cs` 用真正的 `IndexBuilder` 和 `LiveVolumeIndex` 构建文件、目录及硬链接。核对整卷及下钻字节数，覆盖缺失大小、长整型容量、目录移动、删除、基础层合并和输出上限。
- [x] 用 `dotnet test NtfsSearch.slnx -c Release --filter FullyQualifiedName~SpaceUsageTests` 确认测试暴露缺失的汇总入口。
- [x] 在 `src/NtfsSearch.Core/SpaceUsageIndex.cs` 从可见索引行提取完整父子身份。按目录保存文件计数、缺失值和精确字节数，从树叶向卷根累加。文件条目按所在层和父目录保留紧凑行号，浏览时按最大字节权重截取，其余合并。
- [x] 在 `src/NtfsSearch.Core/SpaceUsageIndex.Changes.cs` 比较两个已发布的索引视图。共享记录的层比较可见位图，新旧增量段分别加减；父关系变化在一次更新中先移除旧关系，再挂到新父节点。基础层被整体替换时重新汇总。

## 桌面数据流

- [x] 在 `tests/desktop-window.mjs` 通过原有演示窗口驱动「空间分析」、目录下钻和返回搜索，以演示索引的准确大小作为断言。
- [x] 在 `SearchCatalog.cs` 读取发布卷视图，后端使用卷根列表和索引键匹配已就绪卷。在 `SpaceAnalysisSession.cs` 合并变更和交互请求、调度一个后台工作任务，取消旧卷或关闭视图后丢弃旧结果。`DesktopSession.cs` 连接已有 JSON 消息通道。
- [x] 在 `desktop.ts` 定义分析请求和结果。`App.vue` 接入「空间分析」并保留搜索结果位置。`SpaceAnalysis.vue` 显示卷、面包屑、状态和当前层矩形布局，复用文件定位操作。

## 验证与交付

- [x] 同步 `README.md` 和 `manual/README.md`，更新本次功能版本。
- [x] 运行解决方案测试及仓库 Windows 便携构建，然后运行现有桌面验证脚本。使用隔离的现有实盘索引压测按卷打开和下钻，记录首图、增量延迟和内存，检查采集状态。
- [x] 审查代码和测试行为，确保正常搜索、USN、退出时保存快照可回归。将规格、实现、测试和使用说明一起提交。
