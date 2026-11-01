# Builds libsms's DLL and the MarioTest plugin, and stages both under the workspace's
# target/BepInEx/plugins/MarioTest/ folder (beside the game copy's ..\..) along with a pointer to
# libsms's extracted assets. Copy that folder into the game yourself.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-RoR2Dir <game to compile against>]
#       [-InstallDir <folder to stage under; default ..\target, never the game>] [-NoInstall]
#
param(
    [string]$RoR2Dir = (Join-Path $PSScriptRoot "..\2026 Mod Stuff\RiskOfRain2_DLC4"),
    [string]$InstallDir = "",
    [string]$LibSms = (Join-Path $PSScriptRoot "..\libsms"),
    [string]$LibSsbm = (Join-Path $PSScriptRoot "..\libssbm"),
    [switch]$NoInstall
)
$ErrorActionPreference = "Stop"
if (-not $NoInstall) {
    & (Join-Path $PSScriptRoot '../ror2-common/tests/CheckReleaseBranches.ps1') -HostDir $PSScriptRoot -NativeDir $LibSms
}
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = [System.IO.Path]::GetFullPath((Join-Path $RoR2Dir "..\..\target"))
}

$cargo = Join-Path $env:USERPROFILE ".cargo\bin\cargo.exe"
if (-not (Test-Path $cargo)) { $cargo = "cargo" }

Write-Host "== libsms-ffi (sms.dll)"
Push-Location $LibSms
try { & $cargo build -p libsms-ffi --release; if ($LASTEXITCODE) { throw "cargo failed" } }
finally { Pop-Location }

Write-Host "== MarioTest.dll"
dotnet build (Join-Path $PSScriptRoot "MarioTest\MarioTest.csproj") -c Release "-p:RoR2Dir=$RoR2Dir"
if ($LASTEXITCODE) { throw "dotnet build failed" }

$dist = Join-Path $PSScriptRoot "dist\MarioTest"
New-Item -ItemType Directory -Force $dist | Out-Null
# Do not carry stale files (old stings, an old bundled ssbm.dll) into the plugin folder.
Get-ChildItem $dist -File -ErrorAction SilentlyContinue | Remove-Item -Force
# The first-run worker renders the arrival/death stings into the user's asset cache.
# A clean build needs no extracted Nintendo files.
Copy-Item (Join-Path $PSScriptRoot "MarioTest\bin\Release\netstandard2.1\MarioTest.dll") $dist -Force
Copy-Item (Join-Path $LibSms "target\release\sms.dll") $dist -Force
$assets = [System.IO.Path]::GetFullPath((Join-Path $LibSms "assets"))
[System.IO.File]::WriteAllText((Join-Path $dist "assets_dir.txt"), $assets)
# The Melee Mario A attacks find PlMr.dat through the installed Melee plugin (its cache, which
# it extracts Mario into on first run), so nothing Melee-related is staged here.
Write-Host "built $dist (assets: $assets)"

if (-not $NoInstall) {
    $plugins = Join-Path $InstallDir "BepInEx\plugins\MarioTest"
    New-Item -ItemType Directory -Force $plugins | Out-Null
    # An older build staged a separate Melee cache pointer here; it is no longer read.
    Remove-Item (Join-Path $plugins "melee_assets_dir.txt") -ErrorAction SilentlyContinue
    $files = @("sms.dll","MarioTest.dll","assets_dir.txt")
    Copy-Item ($files | ForEach-Object { Join-Path $dist $_ }) $plugins -Force
    Write-Host "installed into $plugins"
}
