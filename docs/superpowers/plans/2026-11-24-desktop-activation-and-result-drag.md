# 前台聚焦与文件拖出实施计划

按已确认的设计实现三项桌面功能。Vue 管理结果选择及手势，Tauri 接收 Windows 激活事件并发起 Shell 拖放。继续使用现有的搜索结果路径格式和索引更新链路。

## 1. 专项回归

- [x] 在 `tests/desktop-window.mjs` 增加 `--desktop-interaction`，复用演示数据和 WebView2 CDP。用鼠标事件依次验证 Ctrl、Shift、全选、辅助列起框及 Esc。按 Windows 激活路径验证焦点从结果表切回搜索框，并检查设置草稿。
- [x] 用旧 Windows 便携版运行新专项，确认 Ctrl 多选断言捕获原来的单选行为。
- [ ] 在独立 Windows 输入环境用 `--native-file-drag` 验证浏览器收到的文件名和内容，并用测试目录验证资源管理器复制与移动。

## 2. 结果交互

- [x] 在 `SearchResults.vue` 用路径键维护当前页的选中项、活动行与 Shift 锚点。通过 `clearSelection()` 从 `App.vue` 响应查询和翻页，通过 `drag` 事件传递一批路径键。
- [x] 用结果滚动容器接收主鼠标手势，按名称或图标与空白区域区分文件拖放和框选。框选使用滚动内容坐标及当前行边界，拖到上下边缘时持续重算并滚动。`styles.css` 绘制选框，Esc 恢复初始选择。已开始的拖动阻止同次点击。

## 3. Windows 激活与文件拖放

- [x] 用主窗口 HWND 的 `WM_ACTIVATE` 触发 `activate-search`，Vue 监听并在视图切换后聚焦。沿用托盘显式设置入口，自动回搜索时保留设置组件的表单状态。
- [x] 新增 `file_drag.rs` 并注册 `drag_files` 命令。路径键还原为原生宽字符，复用系统 Shell 数据对象，构造跨目录 `CF_HDROP`。在回调返回后调度 OLE 操作，区分取消和实际错误，并处理复制、移动及完成反馈。

## 4. 交付

- [x] 在 `manual/README.md` 补充前台聚焦和鼠标交互，按现有版本规则同步功能版本。
- [x] 通过 `scripts/build-windows.ps1` 构建便携版，完成 Windows 隔离桌面的 `--desktop-interaction`，核查交付目录和版本。Windows Shell 数据格式、跨父目录对象和移动完成反馈的三个专项通过。
