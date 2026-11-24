# 前台聚焦、结果多选与文件拖出

## 前台聚焦

主窗口从后台进入前台时，搜索框获得键盘焦点，保留搜索词和结果选中状态。托盘打开、Alt+Tab、任务栏切换和最小化恢复共用这一行为。桌面端以 Windows 主窗口激活事件为依据，Vue 在搜索框可交互后聚焦。窗口已在前台时，点击结果、键盘导航等操作按当前视图处理。 [Windows 窗口激活](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-activate)

从空间分析恢复前台时返回搜索页。从设置页恢复前台时也返回搜索页，并保留未保存的草稿，重新打开设置时可继续编辑。显式通过托盘「设置」唤起时直接打开设置页。退出保存期间由保存界面接收输入。

## 结果多选与框选

- 普通单击选择一项，Ctrl+单击切换该项，Shift+单击按当前列表顺序选择范围。结果列表拥有焦点时，Ctrl+A 选中当前页。用现有结果路径标识维持选中状态，并在后台刷新后保留当前页仍存在的选中项。
- 从结果区空白处或路径、大小、修改时间等辅助列按住左键移动，超过拖动阈值后显示选框。当前页与选框相交的行实时选中，拖到列表上下边缘时自动滚动。松开完成选取，按 Esc 恢复画框前的选择。普通点击和双击沿用原有手势。 [Everything 框选说明](https://www.voidtools.com/forum/viewtopic.php?t=10299)
- 从文件名或图标起拖时进入文件拖放。起点在选中项上时拖出整组，起点在未选中项上时选择并拖出该项。文件拖放在超过拖动阈值后开始。列表的活动行用于键盘导航，双击打开和现有右键操作继续作用于对应条目。

## Windows Shell 文件拖放

Vue 在拖动开始时将这一批条目的路径标识传给桌面端。原生端从已有的无损路径标识恢复 UTF-16 路径，准备 Windows Shell 数据对象，提供 `CF_HDROP` 承载来自不同目录或盘符的绝对路径。桌面进程以普通权限在支持 OLE 的线程上启动 `SHDoDragDrop`，调度时让 WebView2 回调先返回。 [Shell 数据格式](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard#cf_hdrop) · [SHDoDragDrop](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shdodragdrop) · [WebView2 线程模型](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/threading-model#reentrancy)

浏览器上传区按网页规则接收实际文件。拖到资源管理器时允许由系统和目标协商复制或移动，包括同盘、跨盘和修饰键行为。源端根据 Shell 的完成反馈判断移动由哪一端完成。索引通过现有文件系统变更链路更新结果。拖动取消可直接继续选择，无法准备拖放数据时沿用窗口的错误反馈。 [Shell 移动协议](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios#handling-optimized-move-operations)

## Windows 验收

- 将焦点移到结果表后，分别从托盘、Alt+Tab、任务栏和最小化状态恢复窗口，直接键入并核对文字进了搜索框。验证设置草稿恢复、托盘「设置」入口、空间分析返回搜索，以及退出保存界面仍可操作。
- 验证 Ctrl 和 Shift 多选、当前页全选、满屏结果下从辅助列框选、滚动中的框选、Esc 取消，以及结果刷新后保留仍存在的选中项。
- 使用测试文件分别验证单项和跨目录多选拖到浏览器上传区，核对文件名及内容。用独立测试目录验证资源管理器中的同盘移动、跨盘复制、修饰键、取消和已删除条目的反馈，核对源目录及目标目录的实际内容。
