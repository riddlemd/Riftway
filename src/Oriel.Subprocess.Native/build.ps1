# Builds the native subprocess for Windows against the pinned CEF, with MSVC.
# Usage: src/Oriel.Subprocess.Native/build.ps1 <output-exe> [cef-platform]
# Needs Visual Studio (or Build Tools) with the C++ workload and a Windows SDK.
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$arch = $Platform -eq 'windowsarm64' ? 'arm64' : 'amd64'

$pin = Get-Content (Join-Path $repo 'eng\cef.json') -Raw | ConvertFrom-Json
$cache = $env:ORIEL_CEF_CACHE ? $env:ORIEL_CEF_CACHE : (Join-Path $HOME '.cache\oriel\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}

# The same API version the C# bindings were generated for.
$version = [regex]::Match((Get-Content (Join-Path $repo 'src\Oriel.Cef\Interop\CefApiVersion.cs') -Raw), 'Version = (\d+)').Groups[1].Value

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the C++ workload found.' }
# VsDevCmd calls vswhere by name and prints an error when the Installer folder isn't on PATH.
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -Arch $arch -HostArch amd64 -SkipAutomaticLocation | Out-Null

$Out = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
$obj = Join-Path ([IO.Path]::GetDirectoryName($Out)) 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null

# /MT: the static CRT, as libcef itself uses, so the exe needs no VC++ redistributable.
# /MANIFESTINPUT: without its supportedOS entry Chromium's GPU process crashes (see the manifest).
cl /nologo /std:c11 /O2 /W4 /WX /MT /DUNICODE /D_UNICODE "/DCEF_API_VERSION=$version" "/I$cef" `
    (Join-Path $PSScriptRoot 'subprocess.c') "/Fo$obj\" "/Fe$Out" `
    /link (Join-Path $cef 'Release\libcef.lib') /SUBSYSTEM:WINDOWS `
    /MANIFEST:EMBED "/MANIFESTINPUT:$(Join-Path $PSScriptRoot 'Oriel.Subprocess.exe.manifest')"
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)." }
& (Join-Path $repo 'eng\check-windows-manifest.ps1') $Out | Out-Host
$Out
