# 在 Windows 上生成发布包：dist\Reap-<版本>-<架构>.zip（免安装）和 dist\Reap-Setup-<版本>-<架构>.exe（安装包）。
# 用法：pwsh scripts/publish.ps1 [-Version 0.1.0] [-Arch x64|arm64]
param(
    [string]$Version = "0.1.0",
    [ValidateSet("x64", "arm64")][string]$Arch = "x64"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$rid = "win-$Arch"
$platform = if ($Arch -eq "x64") { "x64" } else { "ARM64" }
$out = "dist\Reap-$Arch"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force dist | Out-Null

dotnet test tests\Reap.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "测试失败" }

dotnet publish src\Reap.App -c Release -r $rid -p:Platform=$platform -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

$zip = "dist\Reap-$Version-$Arch.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "已生成 $zip"

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    Write-Warning "没有找到 Inno Setup 6（ISCC.exe），跳过安装包。可用 winget install JRSoftware.InnoSetup 安装。"
    exit 0
}
& $iscc "/DAppVersion=$Version" "/DArch=$Arch" "/DSourceDir=$((Resolve-Path $out).Path)" "/O$((Resolve-Path dist).Path)" installer\Reap.iss
if ($LASTEXITCODE -ne 0) { throw "安装包生成失败" }
Write-Host "已生成 dist\Reap-Setup-$Version-$Arch.exe"
