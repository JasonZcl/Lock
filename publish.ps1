# 一键打包：把服务和托盘程序发布到 publish\ 目录
# 用法：
#   .\publish.ps1                 # 框架依赖版（目标机器需已安装 .NET 10 桌面运行时，体积小）
#   .\publish.ps1 -SelfContained  # 自包含版（目标机器不需要装 .NET，体积约 150 MB）
param(
    [switch]$SelfContained,
    [string]$OutDir = "$PSScriptRoot\publish"
)

$ErrorActionPreference = "Stop"
$sc = if ($SelfContained) { "--self-contained true" } else { "--no-self-contained" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

foreach ($proj in "Lock.Service\Lock.Service.csproj", "Lock\Lock.csproj") {
    Write-Host "==> 发布 $proj" -ForegroundColor Cyan
    Invoke-Expression "dotnet publish `"$PSScriptRoot\$proj`" -c Release -r win-x64 $sc -o `"$OutDir`" -p:PublishSingleFile=false"
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$proj" }
}

# 发布输出不需要的调试符号
Remove-Item "$OutDir\*.pdb" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "打包完成：$OutDir" -ForegroundColor Green
Write-Host "  AppLock.exe          托盘程序（右键 -> 以管理员身份运行，首次运行在“服务”页安装服务）"
Write-Host "  SysGuardSvc.exe      服务程序（也可命令行：SysGuardSvc.exe install / uninstall）"
