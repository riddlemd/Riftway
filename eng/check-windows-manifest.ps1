# Fails unless an exe's embedded manifest declares Windows 8+ in supportedOS: without it Windows refuses the
# GPU process's layered child window, and Chromium silently falls back to software compositing.
# Usage: eng/check-windows-manifest.ps1 <exe>
param([Parameter(Mandatory)] [string] $Exe)
$ErrorActionPreference = 'Stop'

$Win8OrLater = @{
    '4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38' = 'Windows 8'
    '1f676c76-80e1-4239-95bb-83d0f6d0da78' = 'Windows 8.1'
    '8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a' = 'Windows 10/11'
}

if (-not ('Oriel.ExeManifest' -as [type])) {
    Add-Type -Namespace Oriel -Name ExeManifest -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
[DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
[DllImport("kernel32.dll")] static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);
[DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
[DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
[DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);

// Resource 1 of type RT_MANIFEST (24) is the one the loader applies to an exe.
public static string Read(string path) {
    const uint DataFile = 0x2, ImageResource = 0x20;
    IntPtr module = LoadLibraryExW(path, IntPtr.Zero, DataFile | ImageResource);
    if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    try {
        IntPtr resource = FindResourceW(module, (IntPtr)1, (IntPtr)24);
        if (resource == IntPtr.Zero) return null;
        var bytes = new byte[SizeofResource(module, resource)];
        Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
        return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    } finally { FreeLibrary(module); }
}
'@
}

$path = (Resolve-Path $Exe).Path
$xml = [Oriel.ExeManifest]::Read($path)
if (-not $xml) { throw "$path has no embedded manifest; it must declare Windows 8 or later in supportedOS." }

$ns = @{ c = 'urn:schemas-microsoft-com:compatibility.v1' }
$declared = @(Select-Xml -Content $xml -XPath '//c:supportedOS/@Id' -Namespace $ns |
    ForEach-Object { $_.Node.Value.Trim('{}').ToLowerInvariant() })
$ok = @($declared | Where-Object { $Win8OrLater.ContainsKey($_) })
if (-not $ok) {
    $seen = if ($declared) { $declared -join ', ' } else { 'none' }
    throw "$path declares no Windows 8+ supportedOS (found: $seen)."
}
"$([IO.Path]::GetFileName($path)): supportedOS $(($ok | ForEach-Object { $Win8OrLater[$_] }) -join ', ')"
