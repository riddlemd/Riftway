using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The icon size <see cref="OrielApp.GetFileIcon"/> asks for.</summary>
public enum FileIconSize
{
    /// <summary>16 × 16.</summary>
    Small,

    /// <summary>32 × 32.</summary>
    Normal,

    /// <summary>48 × 48 on Linux, and on Windows for .exe and .dll files; 32 × 32 for other types on Windows, as in
    /// Electron.</summary>
    Large,
}

/// <summary>Options for <see cref="OrielApp.GetFileIcon"/>.</summary>
public sealed record FileIconOptions
{
    /// <summary>The icon size; <see cref="FileIconSize.Normal"/> by default.</summary>
    public FileIconSize Size { get; init; } = FileIconSize.Normal;
}

/// <summary>Icon pixels as premultiplied BGRA, and the scale factor of the image they become.</summary>
internal readonly record struct FileIconPixels(byte[] Bgra, int Width, int Height, float Scale);

public sealed partial class OrielApp
{
    private static readonly object s_fileIconLock = new();
    private static readonly Dictionary<string, string> s_fileIconGroups = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Group, FileIconSize Size), FileIconPixels> s_fileIcons = [];

    internal static IFileIconBackend FileIconBackend { get; set; } = OperatingSystem.IsWindows()
        ? new WindowsFileIconBackend()
        : new UnsupportedFileIconBackend();

    /// <summary>The icon the OS shows for <paramref name="path"/>; Electron's <c>app.getFileIcon()</c>. On Windows an
    /// .exe or .dll gives its own first icon, anything else (or an .exe that cannot be read) its type's icon, so the file
    /// need not exist. Results are cached per path for the life of the process, as Chromium's <c>IconManager</c> does.</summary>
    /// <remarks>A type icon's image has the system DPI scale as its scale factor; an .exe's has 1.</remarks>
    /// <exception cref="InvalidOperationException">The OS has no icon for it (the task faults).</exception>
    /// <exception cref="PlatformNotSupportedException">Not yet implemented on this OS (the task faults).</exception>
    public Task<NativeImage> GetFileIcon(string path, FileIconOptions? options = null) => LoadFileIcon(path, options);

    internal static Task<NativeImage> LoadFileIcon(string path, FileIconOptions? options)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = NormalizeIconPath(path);
        var size = options?.Size ?? FileIconSize.Normal;
        return Task.Run(() => FileIconImage(LookupFileIcon(normalized, size)));
    }

    internal static NativeImage FileIconImage(FileIconPixels? icon) => icon is { } i
        ? NativeImage.CreateFromBitmap(i.Bgra, new NativeImageBitmapOptions { Width = i.Width, Height = i.Height, ScaleFactor = i.Scale })
        : throw new InvalidOperationException("Failed to get file icon.");

    // base::FilePath::NormalizePathSeparators, a no-op off Windows.
    internal static string NormalizeIconPath(string path) => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    /// <summary>Chromium's <c>IconManager</c>: a path seen before with its group's icon cached is answered from the
    /// cache; otherwise <c>IconLoader</c> loads the group and the cache keeps any icon it found.</summary>
    internal static FileIconPixels? LookupFileIcon(string path, FileIconSize size)
    {
        lock (s_fileIconLock)
        {
            if (s_fileIconGroups.TryGetValue(path, out var cachedGroup) && s_fileIcons.TryGetValue((cachedGroup, size), out var cached))
                return cached;
        }

        var group = IconGroup(path);
        var icon = group == path
            ? FileIconBackend.FromFile(path, FileIconPixelSize(size)) ?? FileIconBackend.ForExtension(FilePathExtension(path), size)
            : FileIconBackend.ForExtension(group, size);
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

    /// <summary>Chromium's <c>IconLoader::GroupForFilepath</c>: .exe, .dll and .ico files by path, everything else by
    /// extension, which is empty for a file without one.</summary>
    internal static string IconGroup(string path)
    {
        var extension = FilePathExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? path
            : extension;
    }

    // UtilReadIcon's sizes, at the scale 1 Electron asks for.
    internal static int FileIconPixelSize(FileIconSize size) => size switch
    {
        FileIconSize.Small => 16,
        FileIconSize.Large => 48,
        _ => 32,
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

/// <summary>A platform's file-icon lookups behind <see cref="OrielApp.GetFileIcon"/>.</summary>
internal interface IFileIconBackend
{
    /// <summary>The OS's icon for the type <paramref name="extension"/> (possibly empty); null when there is none.</summary>
    FileIconPixels? ForExtension(string extension, FileIconSize size);

    /// <summary>The file's own first icon at <paramref name="pixels"/> square; null on any failure.</summary>
    FileIconPixels? FromFile(string path, int pixels);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsFileIconBackend : IFileIconBackend
{
    public FileIconPixels? ForExtension(string extension, FileIconSize size)
        => WindowsFileIcon.LoadForExtension(extension, size == FileIconSize.Small) is var (bgra, w, h)
            ? new FileIconPixels(bgra, w, h, WindowsFileIcon.SystemDpiScale())
            : null;

    public FileIconPixels? FromFile(string path, int pixels)
        => WindowsFileIcon.LoadFromFile(path, pixels) is var (bgra, w, h) ? new FileIconPixels(bgra, w, h, 1f) : null;
}

internal sealed class UnsupportedFileIconBackend : IFileIconBackend
{
    public FileIconPixels? ForExtension(string extension, FileIconSize size) => throw Unsupported();

    public FileIconPixels? FromFile(string path, int pixels) => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new("getFileIcon is not implemented on this platform.");
}
