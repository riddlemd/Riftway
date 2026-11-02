# Lays out a self-contained, published Riftway app for Windows beside CEF's libraries and resources.
#
# Default, sandboxed: CEF's bootstrap.exe becomes <ExeName>.exe and Riftway.Host.Native's DLL <ExeName>.dll. bootstrap
# holds Chromium's sandbox broker and runs every process; the DLL starts .NET (the app's managed Main) in the browser
# process and runs CEF natively in the others. The .NET apphost is removed, so <ExeName> must differ from the app's
# assembly name, whose .dll stays beside it.
#   eng/layout-windows.ps1 <app-publish-dir> <Riftway.Host.dll> <ExeName> [-App <AssemblyName>] [-Platform windows64]
# -App names the app's assembly when other .NET apps (a utility process's child) are published beside it; they keep
# their apphosts, and the host DLL starts the one assembly whose apphost was removed.
#
# Unsandboxed fallback: the .NET apphost stays the exe and Riftway.Subprocess.exe runs the children.
#   eng/layout-windows.ps1 <app-publish-dir> -Subprocess <Riftway.Subprocess.exe> [-Platform windows64]
[CmdletBinding(DefaultParameterSetName = 'Bootstrap')]
param(
    [Parameter(Mandatory, Position = 0)] [string] $Publish,
    [Parameter(Mandatory, Position = 1, ParameterSetName = 'Bootstrap')] [string] $HostDll,
    [Parameter(Mandatory, Position = 2, ParameterSetName = 'Bootstrap')] [string] $ExeName,
    [Parameter(Mandatory, ParameterSetName = 'Apphost')] [string] $Subprocess,
    [Parameter(ParameterSetName = 'Bootstrap')] [string] $App,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

# A binary of the wrong architecture would only fail at runtime, when CEF first loads or spawns it.
$machines = @{ windows64 = 0x8664; windowsarm64 = 0xAA64 }
if (-not $machines.ContainsKey($Platform)) { throw "Unknown Windows CEF platform '$Platform' (windows64 or windowsarm64)." }
function Assert-Machine([string] $Path) {
    $pe = [IO.File]::ReadAllBytes((Resolve-Path $Path).Path)
    $machine = [BitConverter]::ToUInt16($pe, [BitConverter]::ToInt32($pe, 0x3C) + 4)
    if ($machine -ne $machines[$Platform]) {
        throw ('{0} is PE machine 0x{1:X4}, but {2} needs 0x{3:X4}.' -f $Path, $machine, $Platform, $machines[$Platform])
    }
}

$pin = Get-Content (Join-Path $PSScriptRoot 'cef.json') -Raw | ConvertFrom-Json
$cache = $env:RIFTWAY_CEF_CACHE ? $env:RIFTWAY_CEF_CACHE : (Join-Path $HOME '.cache\riftway\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}
$Publish = (Resolve-Path $Publish).Path

if ($PSCmdlet.ParameterSetName -eq 'Apphost') {
    # Here too, not only in build.ps1: a subprocess exe can come from anywhere.
    & (Join-Path $PSScriptRoot 'check-windows-manifest.ps1') $Subprocess | Out-Host
    Assert-Machine $Subprocess
    Copy-Item -Recurse -Force (Join-Path $cef 'Release\*') $Publish
    Copy-Item -Recurse -Force (Join-Path $cef 'Resources\*') $Publish
    Remove-Item -Force (Join-Path $Publish 'bootstrap.exe'), (Join-Path $Publish 'bootstrapc.exe') -ErrorAction SilentlyContinue
    Copy-Item -Force $Subprocess (Join-Path $Publish 'Riftway.Subprocess.exe')
    return $Publish
}

Assert-Machine $HostDll
# The host DLL finds the managed app as the one runtimeconfig whose apphost is gone once this script removes it.
$configs = @(Get-ChildItem $Publish -Filter '*.runtimeconfig.json' | ForEach-Object { $_.Name -replace '\.runtimeconfig\.json$', '' })
if ($App) {
    if ($configs -notcontains $App) { throw "No $App.runtimeconfig.json in $Publish." }
    $managed = $App
}
elseif ($configs.Count -eq 1) { $managed = $configs[0] }
else { throw "Expected one *.runtimeconfig.json in $Publish, found $($configs.Count): name the app with -App." }
if (-not (Test-Path (Join-Path $Publish 'hostfxr.dll'))) { throw "$Publish is not a self-contained publish (no hostfxr.dll)." }
if ($ExeName -ieq $managed) { throw "ExeName '$ExeName' is the managed assembly's name; <ExeName>.dll must be the host DLL." }

Copy-Item -Recurse -Force (Join-Path $cef 'Release\*') $Publish
Copy-Item -Recurse -Force (Join-Path $cef 'Resources\*') $Publish
$exe = Join-Path $Publish "$ExeName.exe"
Move-Item -Force (Join-Path $Publish 'bootstrap.exe') $exe
$apphost = Join-Path $Publish "$managed.exe"
if (Test-Path $apphost) { & (Join-Path $PSScriptRoot 'copy-windows-resources.ps1') $apphost $exe }
# The apphost would start the app unsandboxed, and Riftway.Subprocess.exe would take the sandbox away from children.
Remove-Item -Force (Join-Path $Publish 'bootstrapc.exe'), (Join-Path $Publish "$managed.exe"), (Join-Path $Publish 'Riftway.Subprocess.exe') -ErrorAction SilentlyContinue
Copy-Item -Force $HostDll (Join-Path $Publish "$ExeName.dll")
& (Join-Path $PSScriptRoot 'check-windows-manifest.ps1') $exe | Out-Host
$exe
