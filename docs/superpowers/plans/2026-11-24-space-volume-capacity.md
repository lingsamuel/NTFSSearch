# 磁盘容量与设置标签页实施计划

**目标** 在空间分析的磁盘选择器显示操作系统报告的容量，并将桌面设置按现有功能分组拆成标签页。

**实现** 后端保持卷目录查询与容量刷新独立。Vue 通过现有桌面消息收取容量和已保存的偏好，渲染可用键盘操作的选项。设置页在同一个表单中保存各标签的草稿。

- [x] 在 `tests/desktop-smoke.py` 增加定向后端验证，覆盖旧配置、已发布卷的容量和设置写回。
- [x] 在 `DesktopSettings` 和 `DesktopSession` 复用现有配置写入队列，新增容量读取消息，沿用 `FileSizeDisplay` 格式化。
- [x] 在 `App.vue` 和 `SpaceAnalysis.vue` 接入容量更新，在 `SpaceVolumePicker.vue` 渲染选项及背景比例。
- [x] 修改 `Settings.vue` 以标签页呈现分组，保留全局保存语义。更新 `tests/desktop-window.mjs` 的设置及选盘交互，并增加容量模式及跨标签草稿的用例。
- [x] 更新 `manual/README.md` 和现有版本字段。定向验证后使用 `scripts/build-windows.ps1` 交付 Windows 便携版。
