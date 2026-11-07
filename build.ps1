# Builds libsms's DLL and the MarioTest plugin, and stages both under the workspace's
# target/BepInEx/plugins/wesleyasaurus-Mario/ folder (beside the game copy's ..\..), the same folder name
# r2modman gives the Thunderstore package. Copy that folder into the game yourself.
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
# Discs and extracted files live in the wesleyasaurus-SharedFiles package beside the plugins (ror2-common
# SharedFiles); nothing path-related is staged here. The Melee A attacks load the Melee plugin's own ssbm.dll.
Write-Host "built $dist"

if (-not $NoInstall) {
    # The folder r2modman names the Thunderstore package; the old staged name is removed so the plugin isn't loaded twice.
    $plugins = Join-Path $InstallDir "BepInEx\plugins\wesleyasaurus-Mario"
    $legacy = Join-Path $InstallDir "BepInEx\plugins\MarioTest"
    if (Test-Path $legacy) { Remove-Item $legacy -Recurse -Force; Write-Host "removed the old staged folder $legacy" }
    New-Item -ItemType Directory -Force $plugins | Out-Null
    # Older builds staged path pointers here; they are no longer read.
    foreach ($old in @("melee_assets_dir.txt", "assets_dir.txt")) { Remove-Item (Join-Path $plugins $old) -ErrorAction SilentlyContinue }
    $files = @("sms.dll","MarioTest.dll")
    Copy-Item ($files | ForEach-Object { Join-Path $dist $_ }) $plugins -Force
    & (Join-Path $PSScriptRoot '../ror2-common/sharedfiles/Stage.ps1') -PluginsDir (Split-Path $plugins)
    Write-Host "installed into $plugins"
}
