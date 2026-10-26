param([string]$OutputDirectory = "", [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/windows' }
$self = $SelfContained.IsPresent.ToString().ToLowerInvariant()
foreach ($item in @(@('NtfsSearch.App', 'app'), @('NtfsSearch.Cli', 'cli'))) {
    $project = Join-Path $root ('src/' + $item[0])
    & dotnet publish $project -c Release -r win-x64 --self-contained $self -o (Join-Path $OutputDirectory $item[1])
    if ($LASTEXITCODE -ne 0) { throw "构建失败：$($item[0])" }
}
Copy-Item (Join-Path $root 'README.md') (Join-Path $OutputDirectory 'README.md') -Force
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') (Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt') -Force
Copy-Item (Join-Path $PSScriptRoot '启动搜索.cmd') (Join-Path $OutputDirectory '启动搜索.cmd') -Force
$docs = Join-Path $OutputDirectory 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item (Join-Path $root 'docs/benchmarks') $docs -Recurse -Force
Copy-Item (Join-Path $root 'docs/native-dependencies.md') $docs -Force
Write-Output "Windows 原型已构建：$OutputDirectory"
