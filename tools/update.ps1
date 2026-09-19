# 一键更新已安装的 AppLock：停服务 → 退出托盘 → 复制 publish\ → 启服务 → 启托盘 → 导出日志
# 直接双击运行或在 PowerShell 里执行，会自动请求管理员权限。
param(
    [string]$InstallDir = "C:\Program Files\AppLock\publish",
    [string]$PublishDir = "$PSScriptRoot\..\publish",
    [string]$LogExportDir = "$PSScriptRoot\..\logs"
)

# 自动提权
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$ErrorActionPreference = "Stop"
$PublishDir = (Resolve-Path $PublishDir).Path

Write-Host "==> 停止服务" -ForegroundColor Cyan
sc.exe stop AppLockService | Out-Null
for ($i = 0; $i -lt 40; $i++) {
    $state = (sc.exe query AppLockService | Select-String "STATE").ToString()
    if ($state -match "STOPPED") { break }
    Start-Sleep -Milliseconds 250
}
if ($state -notmatch "STOPPED") { throw "服务未能停止：$state" }

Write-Host "==> 退出托盘程序" -ForegroundColor Cyan
Get-Process AppLock -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "==> 复制文件  $PublishDir  ->  $InstallDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item "$PublishDir\*" $InstallDir -Recurse -Force

Write-Host "==> 重新安装服务（更新路径 / 计划任务 / 右键菜单）并启动" -ForegroundColor Cyan
& "$InstallDir\AppLock.Service.exe" install "$InstallDir\AppLock.exe"
if ($LASTEXITCODE -ne 0) { throw "服务安装失败，见 C:\ProgramData\AppLock\service.log" }
sc.exe query AppLockService | Select-String "STATE"

Write-Host "==> 启动托盘程序" -ForegroundColor Cyan
Start-Process "$InstallDir\AppLock.exe"

Write-Host "==> 导出日志到 $LogExportDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $LogExportDir | Out-Null
Copy-Item "C:\ProgramData\AppLock\*.log*" $LogExportDir -Force -ErrorAction SilentlyContinue
# 配置里去掉密码哈希后也导出一份，方便核对被锁程序列表
$cfg = Get-Content "C:\ProgramData\AppLock\config.json" -Raw | ConvertFrom-Json
$cfg.Password = $null; $cfg.Recovery = $null
$cfg | ConvertTo-Json -Depth 10 | Set-Content "$LogExportDir\config.redacted.json" -Encoding UTF8

Write-Host ""
Write-Host "更新完成。已安装文件时间：" -ForegroundColor Green
Get-Item "$InstallDir\AppLock.Core.dll", "$InstallDir\AppLock.Service.dll", "$InstallDir\AppLock.dll" |
    Select-Object Name, LastWriteTime | Format-Table -AutoSize
Read-Host "按回车关闭"
