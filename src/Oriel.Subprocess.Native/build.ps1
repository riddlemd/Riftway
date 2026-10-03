# Builds the native subprocess for Windows against the pinned CEF.
# Usage: src/Oriel.Subprocess.Native/build.ps1 <output-exe> [cef-platform]
# Needs zig on PATH (or $env:ZIG): it brings its own clang, MinGW CRT and resource compiler, so no
# Visual Studio C++ workload or Windows SDK is required. CEF's MSVC import library links as is.
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$zig = $env:ZIG ? $env:ZIG : 'zig'
$target = $Platform -eq 'windowsarm64' ? 'aarch64-windows-gnu' : 'x86_64-windows-gnu'

$pin = Get-Content (Join-Path $repo 'eng\cef.json') -Raw | ConvertFrom-Json
$cache = $env:ORIEL_CEF_CACHE ? $env:ORIEL_CEF_CACHE : (Join-Path $HOME '.cache\oriel\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}

# The same API version the C# bindings were generated for.
$version = [regex]::Match((Get-Content (Join-Path $repo 'src\Oriel.Cef\Interop\CefApiVersion.cs') -Raw), 'Version = (\d+)').Groups[1].Value

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
& $zig cc -target $target -std=c11 -O2 -Wall -Werror -municode '-Wl,--subsystem,windows' `
    "-DCEF_API_VERSION=$version" "-I$cef" `
    (Join-Path $PSScriptRoot 'subprocess.c') (Join-Path $PSScriptRoot 'subprocess.rc') `
    (Join-Path $cef 'Release\libcef.lib') -o $Out
if ($LASTEXITCODE -ne 0) { throw "zig cc failed ($LASTEXITCODE)." }
$Out
