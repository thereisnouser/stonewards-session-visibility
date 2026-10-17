[CmdletBinding()]
param(
    [string]$GameDir = 'F:\Steam\steamapps\common\Stonewards',
    [Parameter(Mandatory=$true)][string]$ProfileDir
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectRoot 'StonewardsSessionVisibility.csproj'
$managed = Join-Path $GameDir 'Stonewards_Data\Managed'
if (-not (Test-Path -LiteralPath $ProfileDir -PathType Container)) { throw "r2modman profile not found: $ProfileDir. Create a profile named stonewards-session-visibility first." }
$profileBepin = Join-Path $ProfileDir 'BepInEx'
$bepInExRoot = if (Test-Path (Join-Path $profileBepin 'core\BepInEx.dll')) { $profileBepin } elseif (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll')) { Join-Path $GameDir 'BepInEx' } else { $profileBepin }

foreach ($file in @('Assembly-CSharp.dll','Mirror.dll','UnityEngine.CoreModule.dll','UnityEngine.UIElementsModule.dll','Unity.InputSystem.dll','com.rlabrecque.steamworks.net.dll')) {
    if (-not (Test-Path (Join-Path $managed $file))) { throw "Game reference missing: $file in $managed" }
}
if (-not (Test-Path (Join-Path $bepInExRoot 'core\BepInEx.dll'))) { throw "BepInEx 5 core missing: $bepInExRoot" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet SDK is not installed or not on PATH.' }
Write-Host 'Building Stonewards Session Visibility...' -ForegroundColor Cyan
& dotnet build $project -c Release "-p:GameDir=$GameDir" "-p:BepInExDir=$bepInExRoot"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
$dll = Join-Path $projectRoot 'bin\Release\netstandard2.1\StonewardsSessionVisibility.dll'
if (-not (Test-Path $dll)) { throw "Built plugin missing: $dll" }
$plugins = Join-Path $profileBepin 'plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
$dest = Join-Path $plugins 'StonewardsSessionVisibility.dll'
Copy-Item -LiteralPath $dll -Destination $dest -Force
Write-Host "Installed: $dest" -ForegroundColor Green
Write-Host 'Launch Stonewards through this r2modman profile; host a lobby; open Esc -> Session Settings.'
Write-Host 'F7 toggles between Public and Private.'
