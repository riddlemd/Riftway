# Stages Riftway.Runtime.<rid> for Windows: CEF's Release and Resources, Riftway's native host DLL (renamed to <Exe>.dll
# by the layout) and the unsandboxed fallback Riftway.Subprocess.exe, under <out>\native; CEF's licence as
# <out>\LICENSE.txt. eng/runtime/Riftway.Runtime.csproj packs it.
# Usage: eng/runtime/stage-windows.ps1 <out-dir> [-Platform windows64]   (needs MSVC, as the native builds do)
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$pin = Get-Content (Join-Path $repo 'eng\cef.json') -Raw | ConvertFrom-Json
$cache = $env:RIFTWAY_CEF_CACHE ? $env:RIFTWAY_CEF_CACHE : (Join-Path $HOME '.cache\riftway\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}

$Out = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
$native = Join-Path $Out 'native'
New-Item -ItemType Directory -Force $native | Out-Null
Copy-Item -Recurse -Force (Join-Path $cef 'Release\*') $native
Copy-Item -Recurse -Force (Join-Path $cef 'Resources\*') $native
Copy-Item -Force (Join-Path $cef 'LICENSE.txt') (Join-Path $Out 'LICENSE.txt')
# Import libraries are for linking the native host, not for apps.
Remove-Item -Force (Join-Path $native '*.lib') -ErrorAction SilentlyContinue

# The native builds leave obj\ beside their output, so they build outside the staged folder.
$build = Join-Path $Out 'build'
& (Join-Path $repo 'src\Riftway.Host.Native\build.ps1') (Join-Path $build 'Riftway.Host.dll') $Platform | Out-Host
& (Join-Path $repo 'src\Riftway.Subprocess.Native\build.ps1') (Join-Path $build 'Riftway.Subprocess.exe') $Platform | Out-Host
Copy-Item -Force (Join-Path $build 'Riftway.Host.dll'), (Join-Path $build 'Riftway.Subprocess.exe') $native
Remove-Item -Recurse -Force $build

# Every app's exe is this bootstrap.exe renamed (its version resources and icons replaced, its manifest kept), and the
# unsandboxed fallback runs as Riftway.Subprocess.exe: without Windows 8+ in supportedOS, Windows refuses the GPU
# process's layered child window and Chromium falls back to software compositing. Checked here so a CEF bump fails
# the package rather than every app that publishes with it.
foreach ($exe in 'bootstrap.exe', 'Riftway.Subprocess.exe') {
    & (Join-Path $repo 'eng\check-windows-manifest.ps1') (Join-Path $native $exe) | Out-Host
}

Get-ChildItem $native -File | Measure-Object Length -Sum | ForEach-Object { 'native: {0} files, {1:N0} MB' -f $_.Count, ($_.Sum / 1MB) }
