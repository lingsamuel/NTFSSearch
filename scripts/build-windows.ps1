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
foreach ($projectName in @('NtfsSearch.App', 'NtfsSearch.Cli')) {
    $project = Join-Path $root ('src/' + $projectName)
    & dotnet publish $project -c Release --artifacts-path $dotnetBuild -r win-x64 --self-contained $self -o $staging
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$projectName" }
}
Copy-Item (Join-Path $rustBuild "$target/release/ntfs-search.exe") (Join-Path $staging 'NtfsSearch.App.exe') -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $staging -Force
Copy-Item (Join-Path $PSScriptRoot '启动搜索.cmd') $staging -Force
Copy-Item (Join-Path $root 'manual') $staging -Recurse -Force
# 记录发布文件，切换自包含模式时也能清理上一版的运行时文件。
$manifestName = '.portable-files'
$published = @(Get-ChildItem -LiteralPath $staging -Force -Name | Where-Object { $_ -ne 'Data' }) + $manifestName
Set-Content -LiteralPath (Join-Path $staging $manifestName) -Value $published -Encoding UTF8
$replaced = $published
$previousManifest = Join-Path $OutputDirectory $manifestName
$legacyPaths = @()
if (Test-Path -LiteralPath $previousManifest -PathType Leaf) {
    $replaced += @(Get-Content -LiteralPath $previousManifest | Where-Object {
        $_ -and $_ -eq $_.TrimEnd([char[]]' .') -and $_ -notin @('Data', 'app', 'cli') -and [IO.Path]::GetFileName($_) -eq $_
    })
}
else {
    # 首次从旧布局升级时，用原程序入口识别旧发布目录。Data 留给用户自行处理。
    $legacyPrograms = @{ app = 'NtfsSearch.App.exe'; cli = 'NtfsSearch.Cli.exe' }
    foreach ($legacyDirectory in $legacyPrograms.Keys) {
        $legacyPath = Join-Path $OutputDirectory $legacyDirectory
        if (!(Test-Path -LiteralPath (Join-Path $legacyPath $legacyPrograms[$legacyDirectory]) -PathType Leaf)) { continue }
        $legacyPaths += $legacyPath
        $replaced += @(Get-ChildItem -LiteralPath $legacyPath -Force -Name | Where-Object { $_ -ne 'Data' } |
            ForEach-Object { Join-Path $legacyDirectory $_ })
    }
    if ((Test-Path (Join-Path $OutputDirectory 'app/NtfsSearch.App.dll')) -and
        (Test-Path (Join-Path $OutputDirectory 'docs/native-dependencies.md'))) {
        $replaced += @('docs', 'README.md')
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$previous = Join-Path $OutputDirectory ('.previous-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $previous | Out-Null
$moved = @()
$installed = @()
try {
    foreach ($item in ($replaced | Sort-Object -Unique)) {
        $path = Join-Path $OutputDirectory $item
        if (Test-Path -LiteralPath $path) {
            $backup = Join-Path $previous $item
            New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
            Move-Item -LiteralPath $path -Destination $backup
            $moved += $item
        }
    }
    foreach ($item in $published) {
        $installed += $item
        Copy-Item -LiteralPath (Join-Path $staging $item) -Destination $OutputDirectory -Force -Recurse
    }
}
catch {
    foreach ($item in $installed) {
        $path = Join-Path $OutputDirectory $item
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -Recurse }
    }
    foreach ($item in $moved) {
        Move-Item -LiteralPath (Join-Path $previous $item) -Destination (Join-Path $OutputDirectory $item)
    }
    Remove-Item -LiteralPath $previous
    throw
}
Remove-Item -LiteralPath $previous -Force -Recurse
Remove-Item -LiteralPath $staging -Force -Recurse
foreach ($legacyPath in $legacyPaths) {
    if (Get-ChildItem -LiteralPath $legacyPath -Force -ErrorAction SilentlyContinue) { continue }
    # 目录占用或清理期间新增文件时，保留目录，已完成的发布仍然有效。
    try { [IO.Directory]::Delete($legacyPath) }
    catch [IO.IOException], [UnauthorizedAccessException] { }
}
Write-Output "Windows 便携版已构建：$OutputDirectory"
