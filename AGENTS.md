# 仓库协作约定

## Windows 便携版交付

- 使用 `scripts/build-windows.ps1` 构建 Windows 便携版。最终产物放在用户当前项目工作区的 `artifacts/windows`，该目录保留一套可直接运行的版本。用户指定交付目录时，以用户指定为准。
- 在 Windows 构建镜像或临时 Git worktree 中构建时，将 `-OutputDirectory` 指向交付目录。如果构建环境无法直接写入交付目录，构建完成后将整套便携版转入交付目录。替换已有便携版时保留 `app/Data`。
- 汇报交付前，在交付目录确认启动脚本、桌面程序、后端、CLI 和随包手册齐全，并核对版本。交付链接指向交付目录。
