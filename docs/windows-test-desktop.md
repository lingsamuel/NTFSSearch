# 在独立的 Windows 桌面运行窗口测试

用 `CreateDesktopW` 创建同一 Windows 会话中的独立桌面，再用 `CreateProcessW` 启动测试进程，显式设置 `STARTUPINFO.lpDesktop` 为 `WinSta0\NtfsSearchTest-<随机 ID>`。测试的 Node.js、Tauri、WebView2 和辅助 PowerShell 在这个桌面运行。保持当前输入桌面，`CREATE_NO_WINDOW` 只用于隐藏测试进程的控制台。 [创建桌面](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopw) · [线程连接到桌面的规则](https://learn.microsoft.com/en-us/windows/win32/winstation/thread-connection-to-a-desktop) · [STARTUPINFOW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow)

## 适用的测试

- `tests/desktop-window.mjs` 通过 WebView2 CDP 向测试窗口发送键鼠事件。已在这个桌面验证 `--desktop-interaction`、`--space-interaction` 和 `--search-delete`。需要确认系统对话框时，在同一测试桌面里查找测试进程拥有的窗口，再向对应的窗口句柄发消息。
- `--native-file-drag` 会移动系统鼠标并与 Edge 交互。使用独立 Windows 虚拟机或专用测试会话的输入桌面运行该用例。
- 同一窗口站的多个桌面共用剪贴板。真实剪切、复制和粘贴用例使用独立窗口站、专用 Windows 登录会话或虚拟机。 [Windows 窗口站](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)
- 桌面隔离仍使用当前 Windows 用户的文件系统、权限和回收站。测试指向演示索引和专属测试文件。回收站、`File.Replace` 等用例将报告和夹具放在 Windows 原生 NTFS 上，例如项目 Windows 镜像的 `artifacts`。WSL 映射盘可用于无需这些文件系统行为的用例。

## 启动

1. 在 Windows 项目目录准备一套临时测试便携版，例如 `artifacts\windows-test`。从正式构建输出按 `.portable-files` 复制程序文件，测试数据由用例创建。便携版按程序目录判定单实例，独立目录可避免测试连接到已打开的实例。
2. 将下方示例保存为项目 `artifacts\run-test-desktop.ps1`，从 Windows PowerShell 在项目根目录运行。每次选择本次改动相关的测试模式。
3. 检查脚本退出码和报告目录的 `desktop-window.log`。验证后清理临时测试便携版、报告和夹具。回收测试只还原自己创建的条目，再清理测试目录。

```powershell
powershell.exe -NoProfile -NonInteractive -File .\artifacts\run-test-desktop.ps1 `
  -Executable .\artifacts\windows-test\NtfsSearch.App.exe `
  -ReportDirectory .\artifacts\space-interaction-check -Mode --space-interaction
```

从 WSL 调用时，使用 `powershell.exe` 和 Windows 能访问的路径。先把 PowerShell 当前目录切到项目 Windows 镜像或映射盘，再运行同一命令。可用 `wslpath -w` 转换路径，也可使用已有的 WSL 驱动器映射。

### PowerShell 启动器示例

```powershell
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidatePattern('^--[a-z-]+$')][string]$Mode = '--desktop-test'
)

$ErrorActionPreference = 'Stop'
if ($Mode -eq '--native-file-drag') {
    throw 'Run the real pointer test in a dedicated Windows input session.'
}

$root = (Get-Location).ProviderPath
$test = (Resolve-Path -LiteralPath 'tests/desktop-window.mjs').ProviderPath
$app = (Resolve-Path -LiteralPath $Executable).ProviderPath
$node = (Get-Command node.exe).Source
$output = (New-Item -ItemType Directory -Path $ReportDirectory -Force).FullName
$log = Join-Path $output 'desktop-window.log'
$env:TEMP = $output
$env:TMP = $output

Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class IsolatedDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo {
        public uint size;
        public string reserved, desktop, title;
        public uint x, y, width, height, charWidth, charHeight, fill, flags;
        public ushort show, reservedSize;
        public IntPtr reservedData, stdin, stdout, stderr;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo {
        public IntPtr process, thread;
        public uint processId, threadId;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll")]
    static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string exe, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    public static int Run(string exe, string command, string directory) {
        string name = "NtfsSearchTest-" + Guid.NewGuid().ToString("N");
        var desktop = CreateDesktop(name, null, IntPtr.Zero, 0, 0x1FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var startup = new StartupInfo { size = (uint) Marshal.SizeOf(typeof(StartupInfo)), desktop = "WinSta0\\" + name };
            ProcessInfo process;
            if (!CreateProcess(exe, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false,
                0x08000000, IntPtr.Zero, directory, ref startup, out process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                Console.WriteLine("Test desktop " + name + ", process " + process.processId);
                uint wait = WaitForSingleObject(process.process, 120000);
                if (wait == 258) throw new TimeoutException("Isolated test timed out. PID " + process.processId);
                if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                uint code;
                if (!GetExitCodeProcess(process.process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return (int) code;
            } finally {
                CloseHandle(process.thread);
                CloseHandle(process.process);
            }
        } finally {
            CloseDesktop(desktop);
        }
    }
}
'@

# cmd 仅负责日志重定向。示例使用不含 % 的项目路径。
$command = 'cmd.exe /d /v:off /s /c ""{0}" "{1}" "{2}" "{3}" {4} > "{5}" 2>&1"' -f $node,$test,$app,$output,$Mode,$log
$result = [IsolatedDesktop]::Run($env:ComSpec, $command, $root)
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 }
exit $result
```

## 排查

- 看不到窗口很正常。外层脚本会记录桌面名和测试父进程 PID。用同一桌面中的辅助进程枚举 HWND，并检查 PID 或所有者。WebView2 的临时配置目录和调试端口由现有测试脚本设置。
- Windows 永久删除确认在实测中为 `#32770` 窗口，「是」的控件 ID 为 6，「否」为 2。文件占用提示可出现同进程的 `OperationStatusWindow`，它可以没有主窗口作为所有者。优先复用 `--search-delete` 的查找和应答逻辑。
- 投递关闭或按钮消息后，等待上一笔 Tauri IPC 响应再发起下一笔删除。仅收到 `PostMessage` 成功表示消息已排队。
- 启动失败时排查当前 Windows 会话、目录和参数。超时时检查日志和记录的 PID，只清理该测试进程树。回到用户当前桌面运行会失去窗口和输入隔离。

## 核对原生文件剪贴板

在专用测试登录会话或虚拟机可通过资源管理器粘贴。在同一登录会话另建窗口站时，从新 PowerShell 进程调用 `CreateWindowStationW(NULL, 0, MAXIMUM_ALLOWED, NULL)`，取得当前登录会话的系统命名窗口站。非管理员使用系统分配的名称。用 `GetUserObjectInformationW(UOI_NAME)` 读回名称，确认它为测试使用的非 `WinSta0` 窗口站，再切换该启动器进程的窗口站、创建桌面并以 `<窗口站>\Default` 启动子进程。实际读写剪贴板前，在子进程内再核对一次窗口站。 [创建窗口站](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw) · [设置进程窗口站](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocesswindowstation)

用实际文件和非空目录检查 `FileDrop` 与 `Preferred DropEffect`。Shell 的复制数据可能同时允许创建链接，所以按复制与移动位判断。可用 `OleGetClipboard` 读取实际数据对象，再交给测试目标目录的 Shell `IDropTarget`。优化移动可能返回 `DROPEFFECT_NONE`，此时按源与目标的文件是否存在、内容是否完整判断结果。 [Shell 剪贴板格式](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard) · [优化移动](https://learn.microsoft.com/en-us/windows/win32/shell/datascenarios)
