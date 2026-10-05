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

    /// <summary>48 × 48 on Linux, 32 × 32 on Windows, as in Electron.</summary>
    Large,
}

/// <summary>Options for <see cref="OrielApp.GetFileIcon"/>.</summary>
public sealed record FileIconOptions
{
    /// <summary>The icon size; <see cref="FileIconSize.Normal"/> by default.</summary>
    public FileIconSize Size { get; init; } = FileIconSize.Normal;
}

public sealed partial class OrielApp
{
    internal static IFileIconBackend FileIconBackend { get; set; } = OperatingSystem.IsWindows()
        ? new WindowsFileIconBackend()
        : new UnsupportedFileIconBackend();

    /// <summary>The icon the OS shows for <paramref name="path"/>; Electron's <c>app.getFileIcon()</c>. Files other than
    /// .exe, .dll and .ico get their type's icon and need not exist.</summary>
    /// <exception cref="InvalidOperationException">The OS has no icon for it (the task faults).</exception>
    /// <exception cref="PlatformNotSupportedException">Not yet implemented on this OS (the task faults).</exception>
    public Task<NativeImage> GetFileIcon(string path, FileIconOptions? options = null) => LoadFileIcon(path, options);

    internal static Task<NativeImage> LoadFileIcon(string path, FileIconOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var small = (options?.Size ?? FileIconSize.Normal) == FileIconSize.Small;
        var (query, byType) = IconQuery(path);
        return Task.Run(() =>
        {
            var icon = FileIconBackend.Load(query, small, byType) ?? throw new InvalidOperationException("Failed to get file icon.");
            return NativeImage.CreateFromBitmap(icon.Bgra, new NativeImageBitmapOptions { Width = icon.Width, Height = icon.Height });
        });
    }

    /// <summary>Chromium's icon grouping: executables and icon files by path, everything else by extension.</summary>
    internal static (string Query, bool ByType) IconQuery(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length == 0 || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? (path, false)
            : (extension, true);
    }
}

/// <summary>A platform's file-icon lookup behind <see cref="OrielApp.GetFileIcon"/>.</summary>
internal interface IFileIconBackend
{
    /// <summary>Premultiplied BGRA pixels for <paramref name="query"/>, or null when there is no icon.</summary>
    (byte[] Bgra, int Width, int Height)? Load(string query, bool small, bool byType);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsFileIconBackend : IFileIconBackend
{
    public (byte[] Bgra, int Width, int Height)? Load(string query, bool small, bool byType) => WindowsFileIcon.Load(query, small, byType);
}

internal sealed class UnsupportedFileIconBackend : IFileIconBackend
{
    public (byte[] Bgra, int Width, int Height)? Load(string query, bool small, bool byType)
        => throw new PlatformNotSupportedException("getFileIcon is not implemented on this platform.");
}
