# Windows 便携包、自动发布与索引位置

状态：已确认。

## 构建与下载

使用现有 Windows 构建入口生成四种 Windows x64 便携 ZIP。

| 包 | HTTP 搜索服务 | .NET 运行时 |
| --- | --- | --- |
| `framework-dependent` | 有 | 使用系统安装的 .NET Runtime 10 和 ASP.NET Core Runtime 10，均为 x64 |
| `framework-dependent-no-http` | 无 | 使用系统安装的 x64 .NET Runtime 10 |
| `self-contained` | 有 | 随包提供 .NET Runtime 10 和 ASP.NET Core Runtime 10 |
| `self-contained-no-http` | 无 | 随包提供 .NET Runtime 10 |

构建开关默认包含 HTTP 服务。关闭开关时，从后端编译和发布中移除 HTTP 服务实现与 ASP.NET Core 框架引用。桌面提供的索引、搜索、空间分析和托盘继续工作。设置页隐藏搜索服务设置，CLI 保留静态查询和诊断。调用 CLI 的实时 `search` 或 `status` 时提示使用带查询服务的版本。切换版本时保留现有 HTTP 设置，以便恢复使用完整版。

四种包均使用系统 WebView2 Runtime，包含启动脚本、桌面程序、后端、CLI、许可和使用手册。发布构建关闭调试符号生成，归档前检查调试文件。归档只取本次生成的便携文件。

## GitHub Actions

每次分支推送构建四种包，供用户从对应的 Actions 运行下载。在仓库的 Actions 产物有效期内，保留最近四次成功产物，每次包含同一提交的四种包。

推送 tag 时，从 tag 指向的提交重新构建，并将四种 ZIP 发布为该 tag 对应的 GitHub Release 附件。Nightly 和 Release 调用同一个 Windows 构建脚本。

## 便携数据

默认把 `index.ntsnap` 和 `settings.json` 写在程序同目录。新安装不创建 `Data/`。显式指定的诊断或测试目录继续隔离。快照沿用原有格式和原子保存流程，正常退出时仍可跳过本次保存。

新版仅读取顶层存档和设置。升级时，用户退出旧版后可手动将旧 `Data/index.ntsnap` 和 `Data/settings.json` 移到程序同目录。便携包构建和替换保留这些数据文件以及旧 `Data`、`app/Data` 的原位置。

初始扫描及 USN 更新仅排除程序自身生成的数据和工作文件。程序目录里的其他普通文件仍可索引，程序放在卷根目录时也按相同规则处理。

## 验收

- 在 Windows 构建并检查四种包，核对运行时、版本、启动文件和调试文件情况。
- 验证完整版的 HTTP 与 CLI 查询。验证无查询服务版的桌面搜索、设置保存、端口不监听及 CLI 提示。
- 验证新目录的索引保存与恢复、旧数据布局的升级行为，以及程序目录中普通文件的初始扫描和增量更新。
- 检查 Actions 触发条件、Nightly 保留范围和 Release 附件。开发时只运行本次直接相关的测试。
