# Lays out a published Oriel app for Windows: CEF's libraries and resources, and the subprocess exe,
# all beside the app's own executable (where CefLayout looks for them).
# Usage: eng/layout-windows.ps1 <app-publish-dir> <subprocess-exe> [cef-platform]
param(
    [Parameter(Mandatory)] [string] $Publish,
    [Parameter(Mandatory)] [string] $Subprocess,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

# Here too, not only in build.ps1: a subprocess exe can come from anywhere.
& (Join-Path $PSScriptRoot 'check-windows-manifest.ps1') $Subprocess | Out-Host

# An exe of the wrong architecture would only fail at runtime, when CEF first spawns a subprocess.
$machines = @{ windows64 = 0x8664; windowsarm64 = 0xAA64 }
if (-not $machines.ContainsKey($Platform)) { throw "Unknown Windows CEF platform '$Platform' (windows64 or windowsarm64)." }
$pe = [IO.File]::ReadAllBytes((Resolve-Path $Subprocess).Path)
$machine = [BitConverter]::ToUInt16($pe, [BitConverter]::ToInt32($pe, 0x3C) + 4)
if ($machine -ne $machines[$Platform]) {
    throw ('{0} is PE machine 0x{1:X4}, but {2} needs 0x{3:X4}.' -f $Subprocess, $machine, $Platform, $machines[$Platform])
}

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
