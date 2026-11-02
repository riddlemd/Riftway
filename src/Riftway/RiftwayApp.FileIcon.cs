using System.Runtime.Versioning;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>The icon size <see cref="RiftwayApp.GetFileIcon"/> asks for.</summary>
public enum FileIconSize
{
    /// <summary>16 × 16.</summary>
    Small,

    /// <summary>32 × 32.</summary>
    Normal,

    /// <summary>48 × 48 on Linux, and on Windows for .exe and .dll files; 32 × 32 for other types on Windows, as in
    /// Electron. Not supported on macOS.</summary>
    Large,
}

/// <summary>Options for <see cref="RiftwayApp.GetFileIcon"/>.</summary>
public sealed record FileIconOptions
{
    /// <summary>The icon size; <see cref="FileIconSize.Normal"/> by default.</summary>
    public FileIconSize Size { get; init; } = FileIconSize.Normal;
}

/// <summary>Icon pixels as premultiplied BGRA, and the scale factor of the image they become.</summary>
internal readonly record struct FileIconPixels(byte[] Bgra, int Width, int Height, float Scale);

public sealed partial class RiftwayApp
{
    private static readonly object s_fileIconLock = new();
    private static readonly Dictionary<string, string> s_fileIconGroups = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Group, FileIconSize Size), FileIconPixels[]> s_fileIcons = [];

    internal static IFileIconBackend FileIconBackend { get; set; } = OperatingSystem.IsWindows() ? new WindowsFileIconBackend()
        : OperatingSystem.IsMacOS() ? new MacFileIconBackend()
        : new UnsupportedFileIconBackend();

    /// <summary>The icon the OS shows for <paramref name="path"/>; Electron's <c>app.getFileIcon()</c>. On Windows an
    /// .exe or .dll gives its own first icon, anything else (or an .exe that cannot be read) its type's icon, so the file
    /// need not exist. On macOS the icon of the file's content type, else of its extension's, 16 or 32 points with 1x and
    /// 2x representations. Results are cached per path for the life of the process, as Chromium's <c>IconManager</c> does.</summary>
    /// <remarks>On Windows a type icon's image has the system DPI scale as its scale factor; an .exe's has 1.</remarks>
    /// <exception cref="InvalidOperationException">The OS has no icon for it (the task faults).</exception>
    /// <exception cref="NotSupportedException"><see cref="FileIconSize.Large"/> on macOS (the task faults).</exception>
    /// <exception cref="PlatformNotSupportedException">Not yet implemented on this OS (the task faults).</exception>
    public Task<NativeImage> GetFileIcon(string path, FileIconOptions? options = null) => LoadFileIcon(path, options);

    internal static Task<NativeImage> LoadFileIcon(string path, FileIconOptions? options)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = NormalizeIconPath(path);
        var size = options?.Size ?? FileIconSize.Normal;
        return Task.Run(() => FileIconImage(LookupFileIcon(normalized, size)));
    }

    internal static NativeImage FileIconImage(FileIconPixels[]? icon) => icon is { Length: > 0 }
        ? NativeImage.FromBitmaps(icon.Select(i => (i.Scale, i.Width, i.Height, i.Bgra)))
        : throw new InvalidOperationException("Failed to get file icon.");

    // base::FilePath::NormalizePathSeparators, a no-op off Windows.
    internal static string NormalizeIconPath(string path) => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    /// <summary>Chromium's <c>IconManager</c>: a path seen before with its group's icon cached is answered from the
    /// cache; otherwise <c>IconLoader</c> loads the group and the cache keeps any icon it found.</summary>
    internal static FileIconPixels[]? LookupFileIcon(string path, FileIconSize size)
    {
        lock (s_fileIconLock)
        {
            if (s_fileIconGroups.TryGetValue(path, out var cachedGroup) && s_fileIcons.TryGetValue((cachedGroup, size), out var cached))
                return cached;
        }

        var (group, fromFile) = FileIconBackend.GroupFor(path);
        var icon = fromFile
            ? FileIconBackend.FromFile(path, FileIconPixelSize(size)) ?? FileIconBackend.ForGroup(FilePathExtension(path), size)
            : FileIconBackend.ForGroup(group, size);
        lock (s_fileIconLock)
        {
            if (icon is { } found) s_fileIcons[(group, size)] = found;
            else s_fileIcons.Remove((group, size));
            s_fileIconGroups[path] = group;
        }
        return icon;
    }

    internal static void ClearFileIconCache()
    {
        lock (s_fileIconLock)
        {
            s_fileIconGroups.Clear();
            s_fileIcons.Clear();
        }
    }

    /// <summary>Chromium's <c>IconLoader::GroupForFilepath</c> on Windows: .exe, .dll and .ico files by path, everything
    /// else by extension, which is empty for a file without one.</summary>
    internal static string IconGroup(string path)
    {
        var extension = FilePathExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? path
            : extension;
    }

    internal static (string Group, bool FromFile) WindowsIconGroup(string path)
    {
        var group = IconGroup(path);
        return (group, group == path);
    }

    /// <summary><c>base::FilePath::FinalExtension()</c> on POSIX: from the last dot of the base name, a leading dot
    /// included; empty without one.</summary>
    internal static string PosixFinalExtension(string path)
    {
        var end = path.Length;
        while (end > 1 && path[end - 1] == '/') end--;
        var name = path.AsSpan(0, end);
        name = name[(name.LastIndexOf('/') + 1)..];
        if (name is "." or "..") return "";
        var dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[dot..].ToString();
    }

    // UtilReadIcon's sizes, at the scale 1 Electron asks for.
    internal static int FileIconPixelSize(FileIconSize size) => size switch
    {
        FileIconSize.Small => 16,
        FileIconSize.Large => 48,
        _ => 32,
    };

    // icon_loader_mac.mm's ReadIcon sizes in points; its LARGE is NOTREACHED, which crashes Electron.
    internal static int MacFileIconPoints(FileIconSize size) => size switch
    {
        FileIconSize.Small => 16,
        FileIconSize.Normal => 32,
        _ => throw new NotSupportedException("FileIconSize.Large is not supported on macOS."),
    };

    private static readonly string[] s_doubleExtensionSuffixes = ["bz", "bz2", "gz", "lz", "lzma", "lzo", "xz", "z", "zst"];

    /// <summary><c>base::FilePath::Extension()</c> on Windows: from the last dot of the base name, or the one before it
    /// for <c>.user.js</c> and short stems before common compression suffixes (<c>.tar.gz</c>); a leading dot counts.</summary>
    internal static string FilePathExtension(string path)
    {
        var name = FilePathBaseName(path);
        if (name is "." or "..") return "";
        var last = name.LastIndexOf('.');
        if (last <= 0) return last < 0 ? "" : name;
        var penultimate = name.LastIndexOf('.', last - 1);
        if (penultimate < 0) return name[last..];
        if (name[(penultimate + 1)..].Equals("user.js", StringComparison.OrdinalIgnoreCase)) return name[penultimate..];
        var final = name[(last + 1)..];
        return s_doubleExtensionSuffixes.Any(s => s.Equals(final, StringComparison.OrdinalIgnoreCase)) && last - penultimate is > 1 and <= 5
            ? name[penultimate..]
            : name[last..];
    }

    // base::FilePath::BaseName on Windows: after the last separator, trailing separators ignored. Its drive-letter
    // handling cannot change an extension, so it is left out.
    private static string FilePathBaseName(string path)
    {
        var end = path.Length;
        while (end > 1 && path[end - 1] is '\\' or '/') end--;
        var span = path.AsSpan(0, end);
        return span[(span.LastIndexOfAny('\\', '/') + 1)..].ToString();
    }
}

/// <summary>A platform's file-icon lookups behind <see cref="RiftwayApp.GetFileIcon"/>.</summary>
internal interface IFileIconBackend
{
    /// <summary>Chromium's <c>IconLoader::GroupForFilepath</c>: the cache group of <paramref name="path"/>, and whether its
    /// icon is read from the file itself.</summary>
    (string Group, bool FromFile) GroupFor(string path);

    /// <summary>The OS's icon for <paramref name="group"/> (Windows: an extension, possibly empty; macOS: a type
    /// identifier), one bitmap per scale; null when there is none.</summary>
    FileIconPixels[]? ForGroup(string group, FileIconSize size);

    /// <summary>The file's own first icon at <paramref name="pixels"/> square; null on any failure.</summary>
    FileIconPixels[]? FromFile(string path, int pixels);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsFileIconBackend : IFileIconBackend
{
    public (string Group, bool FromFile) GroupFor(string path) => RiftwayApp.WindowsIconGroup(path);

    public FileIconPixels[]? ForGroup(string group, FileIconSize size)
        => WindowsFileIcon.LoadForExtension(group, size == FileIconSize.Small) is var (bgra, w, h)
            ? [new FileIconPixels(bgra, w, h, WindowsFileIcon.SystemDpiScale())]
            : null;

    public FileIconPixels[]? FromFile(string path, int pixels)
        => WindowsFileIcon.LoadFromFile(path, pixels) is var (bgra, w, h) ? [new FileIconPixels(bgra, w, h, 1f)] : null;
}

/// <summary>chrome/browser/icon_loader_mac.mm: every file by content type, never from the file itself.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacFileIconBackend : IFileIconBackend
{
    // gfx's supported resource scales on macOS, each of which ImageSkiaFromResizedNSImage draws.
    private static readonly double[] s_scales = [1, 2];

    public (string Group, bool FromFile) GroupFor(string path)
    {
        var extension = RiftwayApp.PosixFinalExtension(path);
        return (MacFileIcon.TypeFor(path, extension.Length == 0 ? null : extension[1..]), false);
    }

    public FileIconPixels[]? ForGroup(string group, FileIconSize size)
    {
        var points = RiftwayApp.MacFileIconPoints(size);
        var bitmaps = MacFileIcon.Icon(group, points, s_scales);
        return bitmaps.Count == 0 ? null : [.. bitmaps.Select(b => new FileIconPixels(b.Bgra, b.Width, b.Height, (float)b.Scale))];
    }

    public FileIconPixels[]? FromFile(string path, int pixels) => null;
}

internal sealed class UnsupportedFileIconBackend : IFileIconBackend
{
    public (string Group, bool FromFile) GroupFor(string path) => throw Unsupported();

    public FileIconPixels[]? ForGroup(string group, FileIconSize size) => throw Unsupported();

    public FileIconPixels[]? FromFile(string path, int pixels) => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new("getFileIcon is not implemented on this platform.");
}
