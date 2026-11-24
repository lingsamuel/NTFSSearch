param([string]$OutputDirectory = "", [switch]$SelfContained, [switch]$WithoutHttp, [switch]$PackageAll)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root $(if ($PackageAll) { 'artifacts/windows/packages' } else { 'artifacts/windows' })
}
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
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

function Publish-Portable([bool]$IncludeRuntime, [bool]$IncludeHttp) {
    # 先独立产出完整包，避免增量发布混入其他版本的框架和文件。
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    $self = $IncludeRuntime.ToString().ToLowerInvariant()
    $http = $IncludeHttp.ToString().ToLowerInvariant()
    $build = Join-Path $dotnetBuild "runtime-$self-http-$http"
    foreach ($projectName in @('NtfsSearch.App', 'NtfsSearch.Cli')) {
        $project = Join-Path $root ('src/' + $projectName)
        & dotnet publish $project -c Release --artifacts-path $build -r win-x64 --self-contained $self `
            "-p:IncludeHttpSearchService=$http" -p:DebugType=None -p:DebugSymbols=false -o $staging | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "构建失败：$projectName" }
    }
    Copy-Item (Join-Path $rustBuild "$target/release/ntfs-search.exe") (Join-Path $staging 'NtfsSearch.App.exe') -Force
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $staging -Force
    Copy-Item (Join-Path $root 'manual') $staging -Recurse -Force
    $debugFiles = @(Get-ChildItem -LiteralPath $staging -Recurse -Force | Where-Object {
        $_.Extension -in @('.pdb', '.dbg', '.dwarf', '.dSYM', '.map')
    })
    if ($debugFiles.Count) { throw "便携包包含调试文件：$($debugFiles.Name -join ', ')" }

    # 记录发布文件，切换打包模式时清理上一版的依赖。
    $manifestName = '.portable-files'
    $files = @(Get-ChildItem -LiteralPath $staging -Force -Name) + $manifestName
    Set-Content -LiteralPath (Join-Path $staging $manifestName) -Value $files -Encoding UTF8
    return $files
}

function Install-Files([string]$Source, [string[]]$Published, [string[]]$Replaced) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $previous = Join-Path $OutputDirectory ('.previous-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $previous | Out-Null
    $moved = @()
    $installed = @()
    try {
        foreach ($item in ($Replaced | Sort-Object -Unique)) {
            $path = Join-Path $OutputDirectory $item
            if (Test-Path -LiteralPath $path) {
                $backup = Join-Path $previous $item
                New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
                Move-Item -LiteralPath $path -Destination $backup
                $moved += $item
            }
        }
        foreach ($item in $Published) {
            $installed += $item
            Copy-Item -LiteralPath (Join-Path $Source $item) -Destination $OutputDirectory -Force -Recurse
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
}

if ($PackageAll) {
    $archives = Join-Path $dotnetBuild 'archives'
    if (Test-Path -LiteralPath $archives) { Remove-Item -LiteralPath $archives -Recurse -Force }
    New-Item -ItemType Directory -Path $archives -Force | Out-Null
    $packages = @(
        @{ Name = 'framework-dependent'; IncludeRuntime = $false; IncludeHttp = $true }
        @{ Name = 'framework-dependent-no-http'; IncludeRuntime = $false; IncludeHttp = $false }
        @{ Name = 'self-contained'; IncludeRuntime = $true; IncludeHttp = $true }
        @{ Name = 'self-contained-no-http'; IncludeRuntime = $true; IncludeHttp = $false }
    )
    try {
        $names = foreach ($package in $packages) {
            Publish-Portable $package.IncludeRuntime $package.IncludeHttp | Out-Null
            $name = "NTFS-Search-win-x64-$($package.Name).zip"
            [IO.Compression.ZipFile]::CreateFromDirectory($staging, (Join-Path $archives $name),
                [IO.Compression.CompressionLevel]::Optimal, $false)
            $name
        }
        Install-Files $archives $names $names
    }
    finally {
        foreach ($directory in @($staging, $archives)) {
            if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
        }
    }
    Write-Output "已构建 $($names.Count) 个 Windows 便携 ZIP：$OutputDirectory"
    return
}

$manifestName = '.portable-files'
$published = @(Publish-Portable $SelfContained.IsPresent (!$WithoutHttp.IsPresent))
$replaced = $published
$previousManifest = Join-Path $OutputDirectory $manifestName
$legacyPaths = @()
if (Test-Path -LiteralPath $previousManifest -PathType Leaf) {
    $replaced += @(Get-Content -LiteralPath $previousManifest | Where-Object {
        $_ -and $_ -eq $_.TrimEnd([char[]]' .') -and $_ -notin @('Data', 'app', 'cli', 'index.ntsnap', 'settings.json') `
            -and [IO.Path]::GetFileName($_) -eq $_
    })
}
else {
    # 首次从旧布局升级时，用原程序入口识别旧发布目录。Data 留给用户自行处理。
    $legacyPrograms = @{ app = 'NtfsSearch.App.exe'; cli = 'NtfsSearch.Cli.exe' }
    foreach ($legacyDirectory in $legacyPrograms.Keys) {
        $legacyPath = Join-Path $OutputDirectory $legacyDirectory
        if (!(Test-Path -LiteralPath (Join-Path $legacyPath $legacyPrograms[$legacyDirectory]) -PathType Leaf)) { continue }
        $legacyPaths += $legacyPath
        # 旧 app 布局的启动脚本在顶层，随旧程序一同参与替换和回滚。
        if ($legacyDirectory -eq 'app') { $replaced += '启动搜索.cmd' }
        $replaced += @(Get-ChildItem -LiteralPath $legacyPath -Force -Name | Where-Object { $_ -ne 'Data' } |
            ForEach-Object { Join-Path $legacyDirectory $_ })
    }
    if ((Test-Path (Join-Path $OutputDirectory 'app/NtfsSearch.App.dll')) -and
        (Test-Path (Join-Path $OutputDirectory 'docs/native-dependencies.md'))) {
        $replaced += @('docs', 'README.md')
    }
}
Install-Files $staging $published $replaced
Remove-Item -LiteralPath $staging -Force -Recurse
foreach ($legacyPath in $legacyPaths) {
    if (Get-ChildItem -LiteralPath $legacyPath -Force -ErrorAction SilentlyContinue) { continue }
    # 目录占用或清理期间新增文件时，保留目录，已完成的发布仍然有效。
    try { [IO.Directory]::Delete($legacyPath) }
    catch [IO.IOException], [UnauthorizedAccessException] { }
}
Write-Output "Windows 便携版已构建：$OutputDirectory"
