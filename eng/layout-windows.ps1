# Lays out a self-contained, published Oriel app for Windows beside CEF's libraries and resources.
#
# Default, sandboxed: CEF's bootstrap.exe becomes <ExeName>.exe and Oriel.Host.Native's DLL <ExeName>.dll. bootstrap
# holds Chromium's sandbox broker and runs every process; the DLL starts .NET (the app's managed Main) in the browser
# process and runs CEF natively in the others. The .NET apphost is removed, so <ExeName> must differ from the app's
# assembly name, whose .dll stays beside it.
#   eng/layout-windows.ps1 <app-publish-dir> <Oriel.Host.dll> <ExeName> [-Platform windows64]
#
# Unsandboxed fallback: the .NET apphost stays the exe and Oriel.Subprocess.exe runs the children.
#   eng/layout-windows.ps1 <app-publish-dir> -Subprocess <Oriel.Subprocess.exe> [-Platform windows64]
[CmdletBinding(DefaultParameterSetName = 'Bootstrap')]
param(
    [Parameter(Mandatory, Position = 0)] [string] $Publish,
    [Parameter(Mandatory, Position = 1, ParameterSetName = 'Bootstrap')] [string] $HostDll,
    [Parameter(Mandatory, Position = 2, ParameterSetName = 'Bootstrap')] [string] $ExeName,
    [Parameter(Mandatory, ParameterSetName = 'Apphost')] [string] $Subprocess,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

$pin = Get-Content (Join-Path $PSScriptRoot 'cef.json') -Raw | ConvertFrom-Json
$cache = $env:ORIEL_CEF_CACHE ? $env:ORIEL_CEF_CACHE : (Join-Path $HOME '.cache\oriel\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}
$Publish = (Resolve-Path $Publish).Path

if ($PSCmdlet.ParameterSetName -eq 'Apphost') {
    # Here too, not only in build.ps1: a subprocess exe can come from anywhere.
    & (Join-Path $PSScriptRoot 'check-windows-manifest.ps1') $Subprocess | Out-Host
    Copy-Item -Recurse -Force (Join-Path $cef 'Release\*') $Publish
    Copy-Item -Recurse -Force (Join-Path $cef 'Resources\*') $Publish
    Remove-Item -Force (Join-Path $Publish 'bootstrap.exe'), (Join-Path $Publish 'bootstrapc.exe') -ErrorAction SilentlyContinue
    Copy-Item -Force $Subprocess (Join-Path $Publish 'Oriel.Subprocess.exe')
    return $Publish
}

# The host DLL finds the managed app the same way: the one runtimeconfig a self-contained publish has.
$configs = @(Get-ChildItem $Publish -Filter '*.runtimeconfig.json')
if ($configs.Count -ne 1) { throw "Expected one *.runtimeconfig.json in $Publish, found $($configs.Count)." }
$managed = $configs[0].Name -replace '\.runtimeconfig\.json$', ''
if (-not (Test-Path (Join-Path $Publish 'hostfxr.dll'))) { throw "$Publish is not a self-contained publish (no hostfxr.dll)." }
if ($ExeName -ieq $managed) { throw "ExeName '$ExeName' is the managed assembly's name; <ExeName>.dll must be the host DLL." }

Copy-Item -Recurse -Force (Join-Path $cef 'Release\*') $Publish
Copy-Item -Recurse -Force (Join-Path $cef 'Resources\*') $Publish
$exe = Join-Path $Publish "$ExeName.exe"
Move-Item -Force (Join-Path $Publish 'bootstrap.exe') $exe
$apphost = Join-Path $Publish "$managed.exe"
if (Test-Path $apphost) { & (Join-Path $PSScriptRoot 'copy-windows-resources.ps1') $apphost $exe }
# The apphost would start the app unsandboxed, and Oriel.Subprocess.exe would take the sandbox away from children.
Remove-Item -Force (Join-Path $Publish 'bootstrapc.exe'), (Join-Path $Publish "$managed.exe"), (Join-Path $Publish 'Oriel.Subprocess.exe') -ErrorAction SilentlyContinue
Copy-Item -Force $HostDll (Join-Path $Publish "$ExeName.dll")
& (Join-Path $PSScriptRoot 'check-windows-manifest.ps1') $exe | Out-Host
$exe
