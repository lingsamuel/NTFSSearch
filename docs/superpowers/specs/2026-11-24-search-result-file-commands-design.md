# 搜索结果的 Windows 文件操作

## 操作和快捷键

新增入口与快捷键只作用于搜索结果。右击选区内的条目时保留选区，右击选区外的条目时选择该项。结果列表获得焦点时处理文件快捷键。搜索框及其他编辑控件继续使用文字剪贴板和各自的按键行为。

| 右键菜单 | 结果列表快捷键 | 行为 |
| --- | --- | --- |
| 打开 | `Enter` | 沿用活动项和双击的打开行为。文件由 Windows 默认关联打开，文件夹交给 Windows 打开，缺少文件关联时进入系统应用选择器。 |
| 打开方式… | 通过菜单选择 | 只对单个文件显示，沿用 Windows 的「打开方式」选择器。 |
| 剪切 | `Ctrl+X` | 将当前选区中的文件和文件夹放入系统剪贴板，供资源管理器粘贴时移动。 |
| 复制 | `Ctrl+C` | 将当前选区中的文件和文件夹放入系统剪贴板，供资源管理器粘贴时复制。 |
| 重命名 | `F2` | 单选时在搜索结果的名称单元格内编辑，同样适用于文件和文件夹。 |
| 复制完整路径 | `Ctrl+Shift+C` | 沿用现有路径文本格式，复制当前项的完整路径。 |

快捷键沿用 Windows 的文件命令。复制完整路径单独采用 Windows 11 资源管理器对应的快捷键。已有「打开所在目录」及 Windows 删除入口继续使用现行行为。 [Windows 快捷键](https://support.microsoft.com/en-gb/accessibility/windows/keyboard-shortcuts-in-windows) · [Windows 11 复制路径快捷键](https://blogs.windows.com/windows-insider/2022/01/27/announcing-windows-11-insider-preview-build-22543/)

## Windows 文件操作

打开和打开方式复用现有桌面 Shell 调用。剪切和复制复用拖放的无损 UTF-16 路径键及 Windows Shell 条目表示。一次写入包含整组选区的 Shell 剪贴板数据，文件和文件夹可来自不同目录、不同盘符。剪切时保留源文件，由 Windows 的粘贴结果决定移动及后续处理。需要源端完成的移动遵循 Shell 粘贴确认和现有源项身份核对。搜索结果根据实际文件系统变更刷新。 [Shell 剪贴板格式](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard) · [Shell 剪切与粘贴协议](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios)

重命名在搜索结果里输入。开始编辑文件时默认选中文件名主体，文件夹默认选中完整名称。`Enter` 提交，`Esc` 取消。桌面壳把新名称交给 Windows `IFileOperation::RenameItem`，允许系统显示需要的确认和错误，并保留 Shell 的权限与冲突处理。实时索引按实际变更刷新。 [Windows Shell 重命名](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-renameitem)

## 验证

定向验证菜单、结果列表的快捷键、搜索框文字复制、现有打开和删除行为，以及空间分析菜单。使用独立 Windows 虚拟机或专用测试登录会话验证默认程序与「打开方式」、资源管理器粘贴、剪切取消和实际改名。同一窗口站下的各桌面共用系统剪贴板，真实剪贴板用例需要独立的窗口站或测试会话。测试使用专属 NTFS 夹具，覆盖跨目录多选、跨盘移动、改名取消和 Windows 检查。同步更新随包手册。 [Windows 窗口站与剪贴板](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)
