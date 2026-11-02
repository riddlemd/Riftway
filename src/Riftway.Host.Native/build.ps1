# Builds Riftway's Windows client DLL for CEF's bootstrap.exe against the pinned CEF, with MSVC.
# The DLL is app-independent: eng/layout-windows.ps1 copies it in as <App>.dll beside bootstrap.exe renamed <App>.exe.
# Usage: src/Riftway.Host.Native/build.ps1 <output-dll> [cef-platform]
# Needs Visual Studio (or Build Tools) with the C++ workload and a Windows SDK.
param(
    [Parameter(Mandatory)] [string] $Out,
    [string] $Platform = 'windows64'
)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$arch = $Platform -eq 'windowsarm64' ? 'arm64' : 'amd64'

$pin = Get-Content (Join-Path $repo 'eng\cef.json') -Raw | ConvertFrom-Json
$cache = $env:RIFTWAY_CEF_CACHE ? $env:RIFTWAY_CEF_CACHE : (Join-Path $HOME '.cache\riftway\cef')
$cef = Join-Path $cache "cef_binary_$($pin.version)_${Platform}_minimal"
if (-not (Test-Path (Join-Path $cef '.complete'))) {
    throw "CEF $Platform is not in $cache; run eng/fetch-cef.sh $Platform from Git Bash first."
}

# The same API version the C# bindings were generated for.
$version = [regex]::Match((Get-Content (Join-Path $repo 'src\Riftway.Cef\Interop\CefApiVersion.cs') -Raw), 'Version = (\d+)').Groups[1].Value

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$tools = $arch -eq 'arm64' ? 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' : 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$vs = & $vswhere -latest -products * -requires $tools -property installationPath
if (-not $vs) { throw "No Visual Studio with $tools found (the C++ workload, plus 'MSVC ARM64 build tools' for windowsarm64)." }
# VsDevCmd calls vswhere by name and prints an error when the Installer folder isn't on PATH.
$env:PATH = "$(Split-Path $vswhere);$env:PATH"
Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
# OSArchitecture, not the process's: an x64 PowerShell on an arm64 host would otherwise pick emulated x64 tools.
$hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64' ? 'arm64' : 'amd64'
Enter-VsDevShell -VsInstallPath $vs -Arch $arch -HostArch $hostArch -SkipAutomaticLocation | Out-Null

$Out = [IO.Path]::GetFullPath($Out, (Get-Location).Path)
$obj = Join-Path ([IO.Path]::GetDirectoryName($Out)) 'obj-host'
New-Item -ItemType Directory -Force $obj | Out-Null

# /MT: the static CRT, as libcef itself uses, so the DLL needs no VC++ redistributable. No manifest: the exe's
# (bootstrap.exe's) already declares supportedOS, which the GPU process needs.
cl /nologo /std:c11 /O2 /W4 /WX /MT /LD /DUNICODE /D_UNICODE "/DCEF_API_VERSION=$version" "/I$cef" `
    (Join-Path $PSScriptRoot 'host.c') "/Fo$obj\" "/Fe$Out" `
    /link (Join-Path $cef 'Release\libcef.lib') shell32.lib user32.lib /MANIFEST:NO
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)." }
$Out
