# 搜索结果原生删除实施计划

**目标** 搜索结果的右键「删除」、`Delete` 和 `Shift+Delete` 交给 Windows Shell。

**实现** Vue 传入当前选区的路径键和删除模式。Tauri 复用拖放的路径键、OLE、Shell 条目和批量删除调用。原生确认与错误提示由 Windows Shell 所有，搜索继续根据实时索引刷新。

- [x] 在 `tests/desktop-window.mjs` 增加 `--search-delete`，先用 0.23.0 验证右键菜单缺少入口，再验证三个入口的实际操作结果。
- [x] 在 `src/NtfsSearch.Desktop/src/SearchResults.vue` 按当前选区发出删除事件，在 `App.vue` 与 `desktop.ts` 接入 Tauri 命令，使用共享语言文件提供提示。
- [x] 在 `src/NtfsSearch.Desktop/src-tauri/src` 复用拖放的 Shell 路径和批量操作。在隔离 Windows 测试桌面的 NTFS 临时目录验证普通删除、永久删除、取消和占用提示。
- [x] 更新随包手册和版本，用 `scripts/build-windows.ps1` 构建便携版。验证空间分析右键菜单、结果多选与拖放入口、Shell 拖放单元测试及索引删除与刷新用例，并核对交付文件和版本。
