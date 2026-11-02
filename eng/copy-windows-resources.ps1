# Gives an exe another exe's version information and icons (the .NET apphost's: the app's name, version and
# ApplicationIcon). The shell names taskbar buttons and Task Manager rows after FileDescription, which bootstrap.exe
# would otherwise give as "CEF Bootstrap Application".
# Usage: eng/copy-windows-resources.ps1 <from-exe> <to-exe>
param(
    [Parameter(Mandatory)] [string] $From,
    [Parameter(Mandatory)] [string] $To
)
$ErrorActionPreference = 'Stop'

if (-not ('Riftway.ExeResources' -as [type])) {
    Add-Type -Namespace Riftway -Name ExeResources -MemberDefinition @'
const uint DataFile = 0x2, ImageResource = 0x20;
public const int Icon = 3, GroupIcon = 14, Version = 16;

delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
delegate bool EnumLanguages(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr param);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
[DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
[DllImport("kernel32.dll")] static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumNames callback, IntPtr param);
[DllImport("kernel32.dll")] static extern bool EnumResourceLanguagesW(IntPtr module, IntPtr type, IntPtr name, EnumLanguages callback, IntPtr param);
[DllImport("kernel32.dll")] static extern IntPtr FindResourceExW(IntPtr module, IntPtr type, IntPtr name, ushort language);
[DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
[DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
[DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr BeginUpdateResourceW(string path, bool deleteExisting);
[DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateResourceW(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
[DllImport("kernel32.dll", SetLastError = true)] static extern bool EndUpdateResourceW(IntPtr update, bool discard);

public sealed class Entry { public int Type; public object Name; public ushort Language; public byte[] Data; }

// Names are integer ids or strings (IS_INTRESOURCE).
static object NameOf(IntPtr name) { return ((long)name >> 16) == 0 ? (object)(int)name : Marshal.PtrToStringUni(name); }

public static System.Collections.Generic.List<Entry> Read(string path, int type) {
    var entries = new System.Collections.Generic.List<Entry>();
    IntPtr module = LoadLibraryExW(path, IntPtr.Zero, DataFile | ImageResource);
    if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    try {
        EnumResourceNamesW(module, (IntPtr)type, (m, t, name, p) => {
            EnumResourceLanguagesW(m, t, name, (m2, t2, n2, language, p2) => {
                IntPtr resource = FindResourceExW(m2, t2, n2, language);
                var data = new byte[SizeofResource(m2, resource)];
                Marshal.Copy(LockResource(LoadResource(m2, resource)), data, 0, data.Length);
                entries.Add(new Entry { Type = type, Name = NameOf(n2), Language = language, Data = data });
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    } finally { FreeLibrary(module); }
    return entries;
}

static void Update(IntPtr update, Entry entry, byte[] data) {
    IntPtr name = entry.Name is int ? (IntPtr)(int)entry.Name : Marshal.StringToHGlobalUni((string)entry.Name);
    try {
        if (!UpdateResourceW(update, (IntPtr)entry.Type, name, entry.Language, data, data == null ? 0u : (uint)data.Length))
            throw new System.ComponentModel.Win32Exception();
    } finally { if (!(entry.Name is int)) Marshal.FreeHGlobal(name); }
}

public static void Replace(string path, System.Collections.Generic.List<Entry> remove, System.Collections.Generic.List<Entry> add) {
    IntPtr update = BeginUpdateResourceW(path, false);
    if (update == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    try {
        foreach (var entry in remove) Update(update, entry, null);
        foreach (var entry in add) Update(update, entry, entry.Data);
    } catch { EndUpdateResourceW(update, true); throw; }
    if (!EndUpdateResourceW(update, false)) throw new System.ComponentModel.Win32Exception();
}
'@
}

$From = (Resolve-Path $From).Path
$To = (Resolve-Path $To).Path
$types = @([Riftway.ExeResources]::Version)
# Icons only when the source has some: an apphost without ApplicationIcon has none, and CEF's beats a blank one.
if ([Riftway.ExeResources]::Read($From, [Riftway.ExeResources]::GroupIcon).Count -gt 0) {
    $types += [Riftway.ExeResources]::GroupIcon, [Riftway.ExeResources]::Icon
}
$remove = [Collections.Generic.List[Riftway.ExeResources+Entry]]::new()
$add = [Collections.Generic.List[Riftway.ExeResources+Entry]]::new()
foreach ($type in $types) {
    foreach ($entry in [Riftway.ExeResources]::Read($To, $type)) { $remove.Add($entry) }
    foreach ($entry in [Riftway.ExeResources]::Read($From, $type)) { $add.Add($entry) }
}
[Riftway.ExeResources]::Replace($To, $remove, $add)
