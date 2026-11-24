# 性能测试

## 真实磁盘基线

| 版本 | 场景 | 索引条目 | 桌面进程私有内存 |
| --- | --- | ---: | ---: |
| [0.4.1](query-memory-0.4.1/README.md) | 真实用户界面 | 约 3441 万 | 5.35 GiB |
| [0.5.0](compact-memory-0.5.0/README.md#最终发布二次实际界面重启) | 同机九卷重启恢复，无全卷重扫 | 约 3443 万 | 约 3.38 GiB |

以上是历史版本实测。对比时分别记录自然进程占用、诊断 GC 后的活对象、静态查询和实时桌面表现。与 Everything 的对照需要同机、同数据、同索引字段和相同查询及排序条件，并核对精确计数。硬链接补全后仍有条目差异待逐文件核对。部分查询仍需遍历数千万行，初次完整索引、大规模变化及亿级即时搜索仍待同机验收。

## 测量记录

- [合成数据容量基线](prototype-0.1.0/README.md)
- [Everything SDK 对照](2026-10-26-everything-sdk-probe.md)
- [真实卷采集与重启恢复](live-index-0.4.0/README.md)
- [名称、记录和排序布局](compact-memory-0.5.0/README.md)
- [桌面界面开销](desktop-ui-0.6.0/README.md)
- [退出快照](exit-snapshot-0.7.0/README.md)和 [Zstd 压缩](exit-zstd-0.10.0/README.md)
- [空间分析](space-analysis-0.8.0/README.md)和 [目录大小统计](search-folder-sizes-0.15.0/README.md)
