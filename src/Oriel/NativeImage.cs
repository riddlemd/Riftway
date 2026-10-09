using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Size and scale of a raw bitmap; Electron's <c>options</c> for <c>createFromBitmap</c> and
/// <c>createFromBuffer</c>.</summary>
public sealed record NativeImageBitmapOptions
{
    /// <summary>The bitmap's width in pixels. Electron's <c>width</c>.</summary>
    public int Width { get; init; }

    /// <summary>The bitmap's height in pixels. Electron's <c>height</c>.</summary>
    public int Height { get; init; }

    /// <summary>The scale factor of this representation. Electron's <c>scaleFactor</c>.</summary>
    public double ScaleFactor { get; init; } = 1.0;
}

/// <summary>How <see cref="NativeImage.Resize"/> trades speed for quality. Electron's <c>quality</c>.</summary>
public enum ResizeQuality
{
    /// <summary>Electron's <c>good</c>.</summary>
    Good,

    /// <summary>Electron's <c>better</c>.</summary>
    Better,

    /// <summary>Electron's <c>best</c>.</summary>
    Best,
}

/// <summary>The target size for <see cref="NativeImage.Resize"/>; Electron's <c>options</c>.</summary>
public sealed record ResizeOptions
{
    /// <summary>The new width in DIPs; null keeps the aspect ratio. Electron's <c>width</c>.</summary>
    public int? Width { get; init; }

    /// <summary>The new height in DIPs; null keeps the aspect ratio. Electron's <c>height</c>.</summary>
    public int? Height { get; init; }

    /// <summary>Electron's <c>quality</c>; <c>Good</c> and <c>Better</c> filter with Hamming-1, <c>Best</c> with Lanczos-3,
    /// as Chromium's Skia does. Electron defaults to <c>best</c>.</summary>
    public ResizeQuality Quality { get; init; } = ResizeQuality.Best;
}

/// <summary>A representation for <see cref="NativeImage.AddRepresentation"/>; Electron's <c>options</c>.</summary>
public sealed record AddRepresentationOptions
{
    /// <summary>The scale factor this representation is for. Electron's <c>scaleFactor</c>.</summary>
    public double ScaleFactor { get; init; } = 1.0;

    /// <summary>The width in pixels when <see cref="Buffer"/> is a raw bitmap. Electron's <c>width</c>.</summary>
    public int Width { get; init; }

    /// <summary>The height in pixels when <see cref="Buffer"/> is a raw bitmap. Electron's <c>height</c>.</summary>
    public int Height { get; init; }

    /// <summary>A PNG, a JPEG or a raw BGRA bitmap. Electron's <c>buffer</c>.</summary>
    public byte[]? Buffer { get; init; }

    /// <summary>A base64 PNG or JPEG data URL. Electron's <c>dataURL</c>.</summary>
    public string? DataUrl { get; init; }
}

/// <summary>An image at one or more scale factors: tray, window and taskbar icons, clipboard images.
/// Electron's <c>nativeImage</c>.</summary>
/// <remarks>Pixels are 32-bit BGRA with premultiplied alpha, top row first, as Electron's <c>toBitmap</c> returns them.
/// Decoding and encoding PNG and JPEG need CEF, so make those calls after <see cref="OrielApp.Create"/>.</remarks>
public sealed unsafe partial class NativeImage
{
    private readonly List<(float Scale, ImageBitmap Bitmap)> _representations = [];
    private volatile bool _template;

    private NativeImage() { }

    /// <summary>An empty image. Electron's <c>nativeImage.createEmpty()</c>.</summary>
    public static NativeImage CreateEmpty() => new();

    /// <summary>Loads a PNG or JPEG file (on Windows also an <c>.ico</c>, as its 256x256 icon), and its <c>@2x</c>-style siblings as further scale factors; empty if the file
    /// cannot be read or decoded. On macOS a path matching <c>*Template.*</c> or <c>*Template@*x.*</c> makes a template
    /// image. Electron's <c>nativeImage.createFromPath()</c>.</summary>
    public static NativeImage CreateFromPath(string path) => CreateFromPath(path, OperatingSystem.IsWindows());

    // Electron reads .ico only on Windows, where it keeps the 256x256 icon as the single 1x representation and skips the
    // @Nx siblings. createFromBuffer never reads ICO.
    internal static NativeImage CreateFromPath(string path, bool readIco)
    {
        ArgumentNullException.ThrowIfNull(path);
        var image = new NativeImage();
        if (readIco && path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            // Windows' own LoadImage, as Electron, picks the entry and stretches it; the managed decoder stands in elsewhere.
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path) && Oriel.Cef.Platform.WindowsIcon.Load(Path.GetFullPath(path), 256) is var (w, h, bgra)) image.Add(1f, new ImageBitmap(w, h, bgra));
                return image;
            }
            try
            {
                if (IcoDecoder.Decode(File.ReadAllBytes(path), 256, bytes => ImageCodec.Decode(bytes)) is { } icon) image.Add(1f, icon);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return image;
        }
        foreach (var (candidate, scale) in ImagePixels.ScaleVariants(path))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(candidate); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (ImageCodec.Decode(bytes) is { } bitmap) image.Add(scale, bitmap);
        }
        if (OperatingSystem.IsMacOS() && path.Length > 0 && NativeImageRules.IsTemplateFilename(Path.GetFullPath(path))) image.SetTemplateImage(true);
        return image;
    }

    /// <summary>Decodes a PNG or JPEG, or failing that takes <paramref name="buffer"/> as a raw bitmap of the size in
    /// <paramref name="options"/>; empty if neither works. Electron's <c>nativeImage.createFromBuffer()</c>.</summary>
    public static NativeImage CreateFromBuffer(byte[] buffer, NativeImageBitmapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var image = new NativeImage();
        var scale = (float)(options?.ScaleFactor ?? 1.0);
        if (ImageCodec.Decode(buffer) is { } decoded) image.Add(scale, decoded);
        else if (options is { Width: > 0, Height: > 0 } && buffer.Length == options.Width * options.Height * 4)
            image.Add(scale, new ImageBitmap(options.Width, options.Height, (byte[])buffer.Clone()));
        return image;
    }

    /// <summary>Takes <paramref name="buffer"/> as a raw premultiplied BGRA bitmap. Electron's
    /// <c>nativeImage.createFromBitmap()</c>.</summary>
    /// <exception cref="ArgumentException">The buffer is not <c>Width × Height × 4</c> bytes.</exception>
    public static NativeImage CreateFromBitmap(byte[] buffer, NativeImageBitmapOptions options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Width <= 0 || options.Height <= 0 || buffer.Length != options.Width * options.Height * 4)
            throw new ArgumentException($"A {options.Width}x{options.Height} bitmap needs {Math.Max(0, options.Width * options.Height * 4)} bytes, not {buffer.Length}.", nameof(buffer));
        var image = new NativeImage();
        image.Add((float)options.ScaleFactor, new ImageBitmap(options.Width, options.Height, (byte[])buffer.Clone()));
        return image;
    }

    /// <summary>Decodes a base64 PNG or JPEG data URL; empty for anything else. Electron's
    /// <c>nativeImage.createFromDataURL()</c>.</summary>
    public static NativeImage CreateFromDataUrl(string dataUrl)
    {
        ArgumentNullException.ThrowIfNull(dataUrl);
        var image = new NativeImage();
        if (DecodeDataUrl(dataUrl) is { } bitmap) image.Add(1f, bitmap);
        return image;
    }

    /// <summary>Whether the image has no representation. Electron's <c>image.isEmpty()</c>.</summary>
    public bool IsEmpty() { lock (_representations) return _representations.Count == 0; }

    /// <summary>The scale factors the image has, in the order they were added. Electron's <c>image.getScaleFactors()</c>.</summary>
    public IReadOnlyList<double> GetScaleFactors() { lock (_representations) return [.. _representations.Select(r => (double)r.Scale)]; }

    /// <summary>The size in DIPs of the representation closest to <paramref name="scaleFactor"/>; zero when empty.
    /// Electron's <c>image.getSize()</c>.</summary>
    public (int Width, int Height) GetSize(double scaleFactor = 1.0)
        => Closest(scaleFactor) is var (scale, bitmap) && bitmap.Width > 0
            ? ((int)(bitmap.Width / scale), (int)(bitmap.Height / scale))
            : (0, 0);

    /// <summary>Width over height; 0 when empty. Electron's <c>image.getAspectRatio()</c>.</summary>
    public double GetAspectRatio(double scaleFactor = 1.0)
        => GetSize(scaleFactor) is var (width, height) && height > 0 ? (double)width / height : 0;

    /// <summary>The pixels of the representation closest to <paramref name="scaleFactor"/>, as premultiplied BGRA; empty
    /// when the image is. Electron's <c>image.toBitmap()</c>.</summary>
    public byte[] ToBitmap(double scaleFactor = 1.0) => (byte[])Closest(scaleFactor).Bitmap.Bgra.Clone();

    /// <summary>PNG of the representation closest to <paramref name="scaleFactor"/>; empty when the image is.
    /// Electron's <c>image.toPNG()</c>.</summary>
    public byte[] ToPng(double scaleFactor = 1.0) => Closest(scaleFactor).Bitmap is { Width: > 0 } bitmap ? ImageCodec.EncodePng(bitmap) : [];

    /// <summary>JPEG of the 1x representation (or the closest), at <paramref name="quality"/> from 0 to 100; empty when
    /// the image is. Electron's <c>image.toJPEG()</c>.</summary>
    public byte[] ToJpeg(int quality)
        => Closest(1.0).Bitmap is { Width: > 0 } bitmap ? ImageCodec.EncodeJpeg(bitmap, Math.Clamp(quality, 0, 100)) : [];

    /// <summary>A PNG data URL of the representation closest to <paramref name="scaleFactor"/>. Electron's
    /// <c>image.toDataURL()</c>.</summary>
    public string ToDataUrl(double scaleFactor = 1.0) => ImagePixels.ToDataUrl("image/png", ToPng(scaleFactor));

    /// <summary>A new image of <paramref name="rect"/> (DIPs) cut from every representation, clipped to the image.
    /// Electron's <c>image.crop()</c>.</summary>
    public NativeImage Crop(Rectangle rect)
    {
        var image = new NativeImage();
        foreach (var (scale, bitmap) in Snapshot())
        {
            var left = (int)Math.Floor(rect.X * scale);
            var top = (int)Math.Floor(rect.Y * scale);
            var right = (int)Math.Ceiling((rect.X + rect.Width) * scale);
            var bottom = (int)Math.Ceiling((rect.Y + rect.Height) * scale);
            var cropped = ImagePixels.Crop(bitmap, left, top, right - left, bottom - top);
            if (cropped.Width > 0) image.Add(scale, cropped);
        }
        return image;
    }

    /// <summary>A new image resized to <paramref name="options"/> (DIPs), every representation at its own scale; a
    /// missing width or height keeps the aspect ratio. Electron's <c>image.resize()</c>.</summary>
    public NativeImage Resize(ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (width, height) = GetSize();
        var target = ImagePixels.ResizeTarget(width, height, options.Width, options.Height);
        var image = new NativeImage();
        foreach (var (scale, bitmap) in Snapshot())
        {
            var resized = ImagePixels.Resize(bitmap, (int)Math.Round(target.Width * scale), (int)Math.Round(target.Height * scale), options.Quality);
            if (resized.Width > 0) image.Add(scale, resized);
        }
        return image;
    }

    /// <summary>Adds or replaces the representation for a scale factor from a PNG, JPEG, raw bitmap or data URL.
    /// Electron's <c>image.addRepresentation()</c>.</summary>
    /// <exception cref="ArgumentException">Neither <c>Buffer</c> nor <c>DataUrl</c> could be read.</exception>
    public void AddRepresentation(AddRepresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ImageBitmap? bitmap = null;
        if (options.Buffer is { } buffer)
        {
            bitmap = ImageCodec.Decode(buffer);
            if (bitmap is null && options is { Width: > 0, Height: > 0 } && buffer.Length == options.Width * options.Height * 4)
                bitmap = new ImageBitmap(options.Width, options.Height, (byte[])buffer.Clone());
        }
        else if (options.DataUrl is { } url)
        {
            bitmap = DecodeDataUrl(url);
        }
        Add((float)options.ScaleFactor, bitmap ?? throw new ArgumentException("The representation is not a PNG, a JPEG, a bitmap of the given size, or an image data URL.", nameof(options)));
    }

    /// <summary>Whether the image is a macOS template image: always false elsewhere and for an empty image. Electron's
    /// <c>image.isTemplateImage()</c>.</summary>
    public bool IsTemplateImage() => OperatingSystem.IsMacOS() && TemplateFlag();

    /// <summary>Marks the image as a macOS template image, which the tray and the Dock icon then draw tinted for the
    /// menu bar's appearance. Does nothing off macOS or on an empty image. Electron's <c>image.setTemplateImage()</c>.</summary>
    public void SetTemplateImage(bool option) => SetTemplateImage(option, OperatingSystem.IsMacOS());

    /// <summary>Electron's <c>image.isMacTemplateImage</c>, the same flag as <see cref="IsTemplateImage"/>.</summary>
    public bool IsMacTemplateImage
    {
        get => IsTemplateImage();
        set => SetTemplateImage(value);
    }

    // Electron keeps the flag on the image's cached NSImage, which an empty image (nil) or another OS lacks.
    internal void SetTemplateImage(bool option, bool isMac)
    {
        if (isMac && !IsEmpty()) _template = option;
    }

    internal bool TemplateFlag() => _template;

    /// <summary>The representation closest to <paramref name="scaleFactor"/>, shared, not copied.</summary>
    internal ImageBitmap BitmapAt(double scaleFactor) => Closest(scaleFactor).Bitmap;

    /// <summary>A new <c>cef_image_t</c> holding every representation, with one reference for the caller.</summary>
    internal cef_image_t* ToCefImage() => ImageCodec.ToCefImage(Snapshot());

    private void Add(float scale, ImageBitmap bitmap)
    {
        lock (_representations)
        {
            _representations.RemoveAll(r => r.Scale == scale);
            _representations.Add((scale, bitmap));
        }
    }

    private List<(float Scale, ImageBitmap Bitmap)> Snapshot() { lock (_representations) return [.. _representations]; }

    // Chromium's ImageSkia rule: the nearest scale wins, the larger on a tie.
    private (float Scale, ImageBitmap Bitmap) Closest(double scaleFactor)
    {
        lock (_representations)
        {
            if (_representations.Count == 0) return (1f, new ImageBitmap(0, 0, []));
            return _representations.OrderBy(r => Math.Abs(r.Scale - scaleFactor)).ThenByDescending(r => r.Scale).First();
        }
    }

    private static ImageBitmap? DecodeDataUrl(string url)
        => ImagePixels.TryParseDataUrl(url, out var mime, out var data) && mime is "image/png" or "image/jpeg" ? ImageCodec.Decode(data) : null;
}

/// <summary>PNG and JPEG through CEF's image codecs, and bitmaps into <c>cef_image_t</c>.</summary>
internal static unsafe class ImageCodec
{
    /// <summary>The image's pixels as premultiplied BGRA, or null if it is not a PNG or JPEG CEF can decode.</summary>
    internal static ImageBitmap? Decode(byte[] bytes)
    {
        var isPng = bytes is [0x89, 0x50, 0x4E, 0x47, ..];
        var isJpeg = bytes is [0xFF, 0xD8, 0xFF, ..];
        if (!isPng && !isJpeg) return null;
        var image = CefNative.cef_image_create();
        try
        {
            int added;
            fixed (byte* data = bytes)
                added = isPng ? image->add_png(image, 1f, data, (nuint)bytes.Length) : image->add_jpeg(image, 1f, data, (nuint)bytes.Length);
            if (added == 0) return null;
            int width, height;
            var binary = image->get_as_bitmap(image, 1f, cef_color_type_t.CEF_COLOR_TYPE_BGRA_8888, cef_alpha_type_t.CEF_ALPHA_TYPE_PREMULTIPLIED, &width, &height);
            return binary is null ? null : new ImageBitmap(width, height, Take(binary));
        }
        finally { NativeObject.Release(image); }
    }

    /// <summary>A PNG with transparency.</summary>
    internal static byte[] EncodePng(ImageBitmap bitmap) => Encode(bitmap, (image, w, h) => image->get_as_png(image, 1f, 1, w, h));

    /// <summary>A JPEG at <paramref name="quality"/> from 0 to 100.</summary>
    internal static byte[] EncodeJpeg(ImageBitmap bitmap, int quality) => Encode(bitmap, (image, w, h) => image->get_as_jpeg(image, 1f, quality, w, h));

    /// <summary>A new <c>cef_image_t</c> with one reference for the caller.</summary>
    internal static cef_image_t* ToCefImage(IEnumerable<(float Scale, ImageBitmap Bitmap)> representations)
    {
        var image = CefNative.cef_image_create();
        foreach (var (scale, bitmap) in representations) AddBitmap(image, scale, bitmap);
        return image;
    }

    private delegate cef_binary_value_t* Encoder(cef_image_t* image, int* width, int* height);

    private static byte[] Encode(ImageBitmap bitmap, Encoder encode)
    {
        var image = CefNative.cef_image_create();
        try
        {
            if (!AddBitmap(image, 1f, bitmap)) return [];
            int width, height;
            var binary = encode(image, &width, &height);
            return binary is null ? [] : Take(binary);
        }
        finally { NativeObject.Release(image); }
    }

    private static bool AddBitmap(cef_image_t* image, float scale, ImageBitmap bitmap)
    {
        fixed (byte* data = bitmap.Bgra)
            return image->add_bitmap(image, scale, bitmap.Width, bitmap.Height, cef_color_type_t.CEF_COLOR_TYPE_BGRA_8888, cef_alpha_type_t.CEF_ALPHA_TYPE_PREMULTIPLIED, data, (nuint)bitmap.Bgra.Length) != 0;
    }

    // Copies the bytes out and releases the value.
    private static byte[] Take(cef_binary_value_t* binary)
    {
        try
        {
            var bytes = new byte[(int)binary->get_size(binary)];
            fixed (byte* data = bytes) binary->get_data(binary, data, (nuint)bytes.Length, 0);
            return bytes;
        }
        finally { NativeObject.Release(binary); }
    }
}
