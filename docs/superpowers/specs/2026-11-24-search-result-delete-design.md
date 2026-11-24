# 搜索结果的 Windows 原生删除

## 操作入口

搜索结果的右键菜单新增「删除」，与结果列表获得焦点时的 `Delete` 行为一致。右击已选中的结果时操作整个选区，右击未选中的结果时先选中该项。文件和文件夹可一起选中，整组选区交给 Windows 处理。`Shift+Delete` 在结果列表中发起永久删除。搜索框和其他编辑控件使用各自原有的按键行为。删除入口只出现在搜索结果中。

## Windows Shell 操作

右键「删除」、`Delete` 和 `Shift+Delete` 都把选区交给 Windows Shell，由 Shell 执行实际删除。桌面壳复用拖放已有的 UTF-16 路径键、STA/OLE 和绝对 Shell 条目解析，通过 Windows `IFileOperation` 将整组选区提交为一次批量操作，并将应用主窗口设为系统对话框的所有者。

| 搜索结果入口 | Windows Shell 删除模式 |
| --- | --- |
| 右键「删除」或 `Delete` | 原生普通删除。Windows 按回收站设置和实际可用性决定能否回收，并按系统设置显示删除确认。无法回收时，由 Windows 发出永久删除警告。 |
| `Shift+Delete` | 原生永久删除。Windows 显示永久删除确认，确认后绕过回收站。 |

三个入口都保留 Windows 的权限和文件占用检查，以及相应的确认、提权、进度和错误对话框。Shell 无法启动操作或解析选中条目时，沿用应用现有的文件操作错误提示。 [Windows 文件资源管理器快捷键](https://support.microsoft.com/en-gb/accessibility/windows/keyboard-shortcuts-in-windows) · [Windows Shell 文件操作标志](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)

Shell 返回后同时检查操作结果与中止状态。现有 NTFS 实时索引按实际文件系统变更刷新搜索结果。操作取消、失败或跳过时，仍存在的条目保留在结果中。 [Windows Shell 操作结果](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-performoperations)

## 验证与文档

在隔离的 Windows NTFS 测试目录中分别验证右键和 `Delete` 的原生回收与确认，核对 `Shift+Delete` 的原生确认、取消和永久删除。覆盖多选、系统拒绝或文件占用、搜索框的 `Delete` 和索引刷新。核对空间分析的菜单和快捷键，并在随包手册补充搜索删除的入口与行为。
