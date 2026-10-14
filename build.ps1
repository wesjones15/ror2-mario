# Builds libsms's DLL and the MarioTest plugin, and installs both into the game's
# BepInEx/plugins/MarioTest/ folder along with a pointer to libsms's extracted assets.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-RoR2Dir <game to compile against>]
#       [-InstallDir <game to install into>] [-NoInstall]
#
# Compiles against the 1.2.4.4 copy next to this repo and installs into the Steam install the
# user plays (falling back to the copy).
param(
    [string]$RoR2Dir = (Join-Path $PSScriptRoot "..\Risk Of Rain 2 2023"),
    [string]$InstallDir = "D:\SteamLibrary\steamapps\common\Risk of Rain 2",
    [string]$LibSms = (Join-Path $PSScriptRoot "..\libsms"),
    [switch]$NoInstall
)
if (-not (Test-Path (Join-Path $InstallDir "BepInEx"))) { $InstallDir = $RoR2Dir }
$ErrorActionPreference = "Stop"

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
# The first-run worker renders the arrival/death stings into the user's asset cache.
# A clean build needs no extracted Nintendo files.
Copy-Item (Join-Path $PSScriptRoot "MarioTest\bin\Release\netstandard2.0\MarioTest.dll") $dist -Force
Copy-Item (Join-Path $LibSms "target\release\sms.dll") $dist -Force
$assets = [System.IO.Path]::GetFullPath((Join-Path $LibSms "assets"))
[System.IO.File]::WriteAllText((Join-Path $dist "assets_dir.txt"), $assets)
Write-Host "built $dist (assets: $assets)"

if (-not $NoInstall) {
    $plugins = Join-Path $InstallDir "BepInEx\plugins\MarioTest"
    New-Item -ItemType Directory -Force $plugins | Out-Null
    Copy-Item (Join-Path $dist "sms.dll"),(Join-Path $dist "MarioTest.dll"),(Join-Path $dist "assets_dir.txt") $plugins -Force
    Write-Host "installed into $plugins"
}
