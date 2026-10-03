# Lays out a published Oriel app for Windows: CEF's libraries and resources, and the subprocess exe,
# all beside the app's own executable (where CefLayout looks for them).
# Usage: eng/layout-windows.ps1 <app-publish-dir> <subprocess-exe> [cef-platform]
param(
    [Parameter(Mandatory)] [string] $Publish,
    [Parameter(Mandatory)] [string] $Subprocess,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

$pin = Get-Content (Join-Path $PSScriptRoot 'cef.json') -Raw | ConvertFrom-Json
$cache = $env:ORIEL_CEF_CACHE ? $env:ORIEL_CEF_CACHE : (Join-Path $HOME '.cache\oriel\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}

Copy-Item -Recurse -Force (Join-Path $cef 'Release\*') $Publish
Copy-Item -Recurse -Force (Join-Path $cef 'Resources\*') $Publish
Copy-Item -Force $Subprocess (Join-Path $Publish 'Oriel.Subprocess.exe')
$Publish
