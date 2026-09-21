# 一键生成安装包：dotnet publish → Inno Setup 编译 → dist\AppLock-Setup-<版本>.exe
# 用法：
#   .\build-installer.ps1                       # 版本号取自 Directory.Build.props 的 <Version>，默认 1.0.0
#   .\build-installer.ps1 -Version 1.2.0
#   .\build-installer.ps1 -SelfContained        # 自带 .NET 运行时（约 150 MB），目标机器无需装 .NET
param(
    [string]$Version,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not $Version) {
    $props = [xml](Get-Content "$root\Directory.Build.props" -Raw -Encoding UTF8)
    $Version = $props.Project.PropertyGroup.Version
    if (-not $Version) { $Version = "1.0.0" }
}

# 找 ISCC.exe（winget 用户级安装 / 系统级安装 / PATH）
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) {
    throw "未找到 Inno Setup 6。安装：winget install --id JRSoftware.InnoSetup -e --scope user"
}

Write-Host "==> 发布程序（版本 $Version）" -ForegroundColor Cyan
$publishArgs = @()
if ($SelfContained) { $publishArgs += "-SelfContained" }
& "$root\publish.ps1" @publishArgs
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

Write-Host "==> 编译安装包" -ForegroundColor Cyan
$dist = "$root\dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $iscc "/DAppVersion=$Version" "/DSourceDir=$root\publish" "/DOutputDir=$dist" "/Qp" "$root\installer\AppLock.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败" }

$setup = Get-Item "$dist\AppLock-Setup-$Version.exe"
Write-Host ""
Write-Host ("安装包已生成：{0}  ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
