# Publishes the app for Windows and builds the installer into installer\dist.
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Drop ffprobe.exe (and ffmpeg's licence text) into installer\tools to bundle it; otherwise the app looks for ffprobe on the PATH.
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\TickleTV.Scheduler.Web\TickleTV.Scheduler.Web.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { $Version = '0.1.0' }

$publish = Join-Path $PSScriptRoot 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing version $Version..."
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$Version" -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$tools = Join-Path $PSScriptRoot 'tools'
if (Test-Path (Join-Path $tools 'ffprobe.exe')) {
    Write-Host 'Bundling ffprobe from installer\tools.'
    New-Item -ItemType Directory -Force (Join-Path $publish 'tools') | Out-Null
    Copy-Item (Join-Path $tools '*') (Join-Path $publish 'tools') -Recurse -Force
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup' }

Write-Host 'Building the installer...'
& $iscc "/DVersion=$Version" (Join-Path $PSScriptRoot 'TickleTV.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

Get-ChildItem (Join-Path $PSScriptRoot 'dist') | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
