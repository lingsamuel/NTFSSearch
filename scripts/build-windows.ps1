param([string]$OutputDirectory = "", [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/windows' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$self = $SelfContained.IsPresent.ToString().ToLowerInvariant()
$desktop = Join-Path $root 'src/NtfsSearch.Desktop'
$target = 'x86_64-pc-windows-msvc'
$rustBuild = Join-Path $desktop 'src-tauri/target'
$dotnetBuild = Join-Path $root 'artifacts/dotnet-win'
$staging = Join-Path $dotnetBuild 'portable'

Push-Location $desktop
try {
    & npm.cmd ci --no-audit --no-fund --cache (Join-Path $root 'artifacts/npm-cache')
    if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败。' }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
    & cargo build --locked --release --target $target --target-dir $rustBuild --manifest-path 'src-tauri/Cargo.toml'
    if ($LASTEXITCODE -ne 0) { throw 'Tauri 桌面程序构建失败。' }
}
finally { Pop-Location }

# 先独立产出完整包，避免增量发布混入旧 WPF 文件或自包含运行时。
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
foreach ($item in @(@('NtfsSearch.App', 'app'), @('NtfsSearch.Cli', 'cli'))) {
    $project = Join-Path $root ('src/' + $item[0])
    & dotnet publish $project -c Release --artifacts-path $dotnetBuild -r win-x64 --self-contained $self -o (Join-Path $staging $item[1])
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$($item[0])" }
}
Copy-Item (Join-Path $rustBuild "$target/release/ntfs-search.exe") (Join-Path $staging 'app/NtfsSearch.App.exe') -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $staging -Force
Copy-Item (Join-Path $PSScriptRoot '启动搜索.cmd') $staging -Force
Copy-Item (Join-Path $root 'manual') $staging -Recurse -Force
$published = @('app', 'cli', 'manual', 'THIRD-PARTY-NOTICES.txt', '启动搜索.cmd')
$replaced = $published
if ((Test-Path (Join-Path $OutputDirectory 'app/NtfsSearch.App.dll')) -and
    (Test-Path (Join-Path $OutputDirectory 'docs/native-dependencies.md'))) {
    $replaced += @('docs', 'README.md')
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$previous = Join-Path $OutputDirectory ('.previous-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $previous | Out-Null
$moved = @()
$installed = @()
try {
    foreach ($item in $replaced) {
        $path = Join-Path $OutputDirectory $item
        if (Test-Path -LiteralPath $path) {
            Move-Item -LiteralPath $path -Destination $previous
            $moved += $item
        }
    }
    foreach ($item in $published) {
        $installed += $item
        Copy-Item -LiteralPath (Join-Path $staging $item) -Destination $OutputDirectory -Force -Recurse
    }
    $data = Join-Path $previous 'app/Data'
    if (Test-Path -LiteralPath $data) {
        Move-Item -LiteralPath $data -Destination (Join-Path $OutputDirectory 'app')
    }
}
catch {
    foreach ($item in $installed) {
        $path = Join-Path $OutputDirectory $item
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -Recurse }
    }
    foreach ($item in $moved) {
        Move-Item -LiteralPath (Join-Path $previous $item) -Destination $OutputDirectory
    }
    Remove-Item -LiteralPath $previous
    throw
}
Remove-Item -LiteralPath $previous -Force -Recurse
Remove-Item -LiteralPath $staging -Force -Recurse
Write-Output "Windows 便携版已构建：$OutputDirectory"
