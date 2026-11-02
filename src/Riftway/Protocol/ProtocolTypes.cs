using System.Text;

namespace Riftway;

/// <summary>What a custom scheme may do; Electron's <c>CustomScheme.privileges</c>.</summary>
public sealed record SchemePrivileges
{
    /// <summary>URLs have a host and port, so pages get a real origin (localStorage, CORS, relative URLs).</summary>
    public bool Standard { get; init; }

    /// <summary>Pages are secure contexts, like https.</summary>
    public bool Secure { get; init; }

    /// <summary>Resources load regardless of the page's Content-Security-Policy.</summary>
    public bool BypassCsp { get; init; }

    /// <summary>Pages may <c>fetch()</c> URLs on this scheme.</summary>
    public bool SupportFetchApi { get; init; }

    /// <summary>Cross-origin requests to this scheme follow CORS.</summary>
    public bool CorsEnabled { get; init; }

    /// <summary>Accepted for Electron compatibility; streaming needs no flag in CEF.</summary>
    public bool Stream { get; init; }

    /// <summary>Not supported: CEF does not let schemes register service workers.</summary>
    public bool AllowServiceWorkers { get; init; }

    /// <summary>Not supported: CEF does not expose V8's code cache for custom schemes.</summary>
    public bool CodeCache { get; init; }
}

/// <summary>A scheme to register before the app starts; Electron's <c>CustomScheme</c>.</summary>
/// <param name="Scheme">Lowercase scheme name, e.g. <c>app</c>.</param>
/// <param name="Privileges">What the scheme may do.</param>
public sealed record PrivilegedScheme(string Scheme, SchemePrivileges Privileges);

/// <summary>A request to a handled scheme.</summary>
public sealed class ProtocolRequest
{
    internal ProtocolRequest(
        string url, string method, string? referrer,
        IReadOnlyDictionary<string, string> headers, byte[]? body, IReadOnlyList<string> uploadFiles)
    {
        Url = url;
        Method = method;
        Referrer = referrer;
        Headers = headers;
        Body = body;
        UploadFiles = uploadFiles;
    }

    /// <summary>The full URL, e.g. <c>app://bundle/index.html</c>.</summary>
    public string Url { get; }

    /// <summary>The HTTP method.</summary>
    public string Method { get; }

    /// <summary>The referrer URL, if any.</summary>
    public string? Referrer { get; }

    /// <summary>Request headers, case-insensitive; repeated headers are joined with ", ".</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The request body bytes, or null when there is none.</summary>
    public byte[]? Body { get; }

    /// <summary>Paths of files uploaded as part of the body.</summary>
    public IReadOnlyList<string> UploadFiles { get; }

    /// <summary>The body decoded as UTF-8, or null when there is none.</summary>
    public string? BodyText => Body is null ? null : Encoding.UTF8.GetString(Body);
}

/// <summary>A response to a <see cref="ProtocolRequest"/>.</summary>
/// <remarks>Riftway answers <c>Range</c> requests itself when the body's length is known: return the whole
/// body with status 200 and the requested slice goes out as 206. A handler that returns 206 is trusted
/// to have sliced the body already.</remarks>
public sealed class ProtocolResponse
{
    private static readonly Dictionary<string, string> s_mimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".wasm"] = "application/wasm",
        [".woff2"] = "font/woff2",
        [".webm"] = "video/webm",
        [".mp4"] = "video/mp4",
        [".m3u8"] = "application/vnd.apple.mpegurl",
        [".ogg"] = "audio/ogg",
        [".mp3"] = "audio/mpeg",
    };

    /// <summary>The status code.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>The status text; defaults to the standard reason phrase.</summary>
    public string? StatusText { get; init; }

    /// <summary>The Content-Type, e.g. <c>text/html; charset=utf-8</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>Other response headers. Content-Type and Content-Length come from <see cref="ContentType"/>
    /// and the body.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>The body, read once and disposed by Riftway; null for none.</summary>
    public Stream? Body { get; init; }

    /// <summary>The body's length in bytes; defaults to the remaining length of a seekable <see cref="Body"/>.
    /// Media and range requests need it.</summary>
    public long? Length { get; init; }

    /// <summary>A text body.</summary>
    public static ProtocolResponse Text(string text, string contentType = "text/plain; charset=utf-8", int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Bytes(Encoding.UTF8.GetBytes(text), contentType, statusCode);
    }

    /// <summary>A byte body.</summary>
    public static ProtocolResponse Bytes(byte[] body, string contentType = "application/octet-stream", int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new ProtocolResponse { StatusCode = statusCode, ContentType = contentType, Body = new MemoryStream(body, writable: false) };
    }

    /// <summary>A file, with its Content-Type from the extension unless given; 404 when it does not exist.</summary>
    public static ProtocolResponse File(string path, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!System.IO.File.Exists(path)) return Status(404);
        return new ProtocolResponse
        {
            ContentType = contentType ?? MimeTypeOf(path),
            Body = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan),
        };
    }

    /// <summary>An empty response with <paramref name="statusCode"/>.</summary>
    public static ProtocolResponse Status(int statusCode, string? statusText = null)
        => new() { StatusCode = statusCode, StatusText = statusText };

    /// <summary>The Content-Type Riftway uses for a file extension; <c>application/octet-stream</c> when unknown.</summary>
    public static string MimeTypeOf(string path)
        => s_mimeTypes.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
