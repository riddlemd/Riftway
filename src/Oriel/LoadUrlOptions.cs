using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Options for <see cref="WebContents.LoadUrl"/>: Electron's <c>loadURL</c> <c>options</c>.</summary>
public sealed record LoadUrlOptions
{
    /// <summary>The HTTP referrer; a plain string uses <see cref="ReferrerPolicy.Default"/>. Electron's <c>httpReferrer</c>.</summary>
    public Referrer? HttpReferrer { get; init; }

    /// <summary>The user agent; like <see cref="WebContents.SetUserAgent"/>, it stays for later navigations.
    /// Electron's <c>userAgent</c>.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Extra request headers, one <c>Name: value</c> per line, separated by <c>"\n"</c>.
    /// Electron's <c>extraHeaders</c>.</summary>
    public string? ExtraHeaders { get; init; }

    /// <summary>The request body; when set the navigation is a POST. Electron's <c>postData</c>.</summary>
    public IReadOnlyList<PostDataEntry>? PostData { get; init; }

    /// <summary>Not supported: CEF has no way to commit a <c>data:</c> URL under another base URL.
    /// Electron's <c>baseURLForDataURL</c>.</summary>
    public string? BaseUrlForDataUrl { get; init; }
}

/// <summary>A referrer URL and the policy that trims it. Electron's <c>Referrer</c>.</summary>
/// <param name="Url">An http or https URL.</param>
/// <param name="Policy">How much of <paramref name="Url"/> the target sees.</param>
public sealed record Referrer(string Url, ReferrerPolicy Policy = ReferrerPolicy.Default)
{
    /// <summary>A referrer with <see cref="ReferrerPolicy.Default"/>, like Electron's string form.</summary>
    public static implicit operator Referrer(string url) => new(url);
}

/// <summary>Electron's <c>Referrer.policy</c> values; see the Referrer-Policy spec.</summary>
public enum ReferrerPolicy
{
    /// <summary><c>default</c>: Chromium's default, <c>strict-origin-when-cross-origin</c>.</summary>
    Default,

    /// <summary><c>unsafe-url</c>.</summary>
    UnsafeUrl,

    /// <summary><c>no-referrer-when-downgrade</c>.</summary>
    NoReferrerWhenDowngrade,

    /// <summary><c>no-referrer</c>.</summary>
    NoReferrer,

    /// <summary><c>origin</c>.</summary>
    Origin,

    /// <summary><c>strict-origin-when-cross-origin</c>.</summary>
    StrictOriginWhenCrossOrigin,

    /// <summary><c>same-origin</c>.</summary>
    SameOrigin,

    /// <summary><c>strict-origin</c>.</summary>
    StrictOrigin,
}

/// <summary>One part of <see cref="LoadUrlOptions.PostData"/>: <see cref="UploadRawData"/> or <see cref="UploadFile"/>.</summary>
public abstract record PostDataEntry;

/// <summary>Bytes sent as part of the body. Electron's <c>UploadRawData</c>.</summary>
/// <param name="Bytes">The data.</param>
public sealed record UploadRawData(byte[] Bytes) : PostDataEntry;

/// <summary>A file sent as part of the body. Electron's <c>UploadFile</c>.</summary>
/// <param name="FilePath">The file to upload.</param>
public sealed record UploadFile(string FilePath) : PostDataEntry
{
    /// <summary>Where the part starts, in bytes. Electron's <c>offset</c>.</summary>
    public long Offset { get; init; }

    /// <summary>How many bytes to send; 0 sends the rest of the file. Electron's <c>length</c>.</summary>
    public long Length { get; init; }

    /// <summary>The file's last-write time, in seconds since the UNIX epoch, that it must still have, or the load fails
    /// with <c>ERR_UPLOAD_FILE_CHANGED</c>; 0 checks nothing. Electron's <c>modificationTime</c>.</summary>
    public double ModificationTime { get; init; }

    /// <summary>Whether the file was written since <see cref="ModificationTime"/>.</summary>
    internal bool HasChanged()
    {
        if (ModificationTime == 0) return false;
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(FilePath)).ToUnixTimeMilliseconds() / 1000.0;
        return Math.Abs(written - ModificationTime) > 0.001;
    }

    /// <summary>The part to send when it is not the whole file; CEF attaches only whole files.</summary>
    internal byte[]? ReadSlice()
    {
        if (Offset == 0 && Length == 0) return null;
        using var stream = File.OpenRead(FilePath);
        stream.Position = Math.Min(Offset, stream.Length);
        var count = Length == 0 ? stream.Length - stream.Position : Math.Min(Length, stream.Length - stream.Position);
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>What one <see cref="WebContents.LoadUrl"/> call changes on its navigation request.</summary>
internal sealed record PendingLoad(
    string MatchUrl,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Referrer? Referrer,
    IReadOnlyList<PostDataEntry>? PostData)
{
    /// <summary>The request changes for <paramref name="options"/>, or null when there are none. The user agent
    /// is not one: the DevTools override already sets its header.</summary>
    /// <exception cref="NotSupportedException">An option CEF cannot honour.</exception>
    public static PendingLoad? Create(string url, LoadUrlOptions? options)
    {
        if (options is null) return null;
        if (options.BaseUrlForDataUrl is not null)
            throw new NotSupportedException("baseURLForDataURL is not supported: CEF cannot load a data: URL under another base URL.");
        foreach (var entry in options.PostData ?? [])
        {
            if (entry is UploadFile { Offset: < 0 } or UploadFile { Length: < 0 })
                throw new ArgumentOutOfRangeException(nameof(options), "UploadFile offset and length cannot be negative.");
            if (entry is null) throw new ArgumentException("PostData contains a null entry.", nameof(options));
        }

        var headers = ParseExtraHeaders(options.ExtraHeaders);
        var referrer = options.HttpReferrer;
        // CEF takes the referrer only through set_referrer, never as a header.
        var refererHeader = headers.FindIndex(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase));
        if (refererHeader >= 0)
        {
            referrer ??= headers[refererHeader].Value;
            headers.RemoveAll(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase));
        }

        var postData = options.PostData is { Count: > 0 } entries ? entries : null;
        if (headers.Count == 0 && referrer is null && postData is null) return null;
        return new PendingLoad(NormalizeUrl(url), headers, referrer, postData);
    }

    /// <summary>Splits <c>extraHeaders</c> into name/value pairs; lines without a name are skipped, as Chromium does.</summary>
    public static List<KeyValuePair<string, string>> ParseExtraHeaders(string? extraHeaders)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrEmpty(extraHeaders)) return result;
        foreach (var line in extraHeaders.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            if (name.Length == 0) continue;
            result.Add(new(name, line[(colon + 1)..].Trim()));
        }
        return result;
    }

    /// <summary>The URL as the network request carries it: canonical, without the fragment.</summary>
    public static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var text = uri.AbsoluteUri;
        var hash = text.IndexOf('#');
        return hash < 0 ? text : text[..hash];
    }

    /// <summary>Whether <paramref name="requestUrl"/> is this load's navigation request.</summary>
    public bool Matches(string requestUrl) => NormalizeUrl(requestUrl) == MatchUrl;

    /// <summary>CEF's value for <paramref name="policy"/>; CEF's enum follows Chromium's net::ReferrerPolicy.</summary>
    public static cef_referrer_policy_t ToCef(ReferrerPolicy policy) => policy switch
    {
        ReferrerPolicy.UnsafeUrl => cef_referrer_policy_t.REFERRER_POLICY_NEVER_CLEAR_REFERRER,
        ReferrerPolicy.NoReferrerWhenDowngrade => cef_referrer_policy_t.REFERRER_POLICY_CLEAR_REFERRER_ON_TRANSITION_FROM_SECURE_TO_INSECURE,
        ReferrerPolicy.NoReferrer => cef_referrer_policy_t.REFERRER_POLICY_NO_REFERRER,
        ReferrerPolicy.Origin => cef_referrer_policy_t.REFERRER_POLICY_ORIGIN,
        ReferrerPolicy.SameOrigin => cef_referrer_policy_t.REFERRER_POLICY_CLEAR_REFERRER_ON_TRANSITION_CROSS_ORIGIN,
        ReferrerPolicy.StrictOrigin => cef_referrer_policy_t.REFERRER_POLICY_ORIGIN_CLEAR_ON_TRANSITION_FROM_SECURE_TO_INSECURE,
        // Blink's kDefault resolves to strict-origin-when-cross-origin; CEF's DEFAULT is the older policy.
        _ => cef_referrer_policy_t.REFERRER_POLICY_REDUCE_REFERRER_GRANULARITY_ON_TRANSITION_CROSS_ORIGIN,
    };

    /// <summary>Writes this load's headers, referrer and body into <paramref name="request"/>.</summary>
    public unsafe void ApplyTo(cef_request_t* request)
    {
        foreach (var (name, value) in Headers) SetHeader(request, name, value);

        if (Referrer is { } referrer)
        {
            cef_string_utf16_t url = default;
            CefString.Set(&url, referrer.Url);
            request->set_referrer(request, &url, ToCef(referrer.Policy));
            CefString.Clear(&url);
        }

        if (PostData is null) return;
        cef_string_utf16_t method = default;
        CefString.Set(&method, "POST");
        request->set_method(request, &method);
        CefString.Clear(&method);

        // Each struct passed into CEF hands over the reference it was created with.
        var body = CefNative.cef_post_data_create();
        foreach (var entry in PostData)
        {
            var element = CefNative.cef_post_data_element_create();
            switch (entry)
            {
                case UploadRawData raw:
                    fixed (byte* bytes = raw.Bytes) element->set_to_bytes(element, (nuint)raw.Bytes.Length, bytes);
                    break;
                case UploadFile file when file.ReadSlice() is { } slice:
                    fixed (byte* bytes = slice) element->set_to_bytes(element, (nuint)slice.Length, bytes);
                    break;
                case UploadFile file:
                    cef_string_utf16_t path = default;
                    CefString.Set(&path, Path.GetFullPath(file.FilePath));
                    element->set_to_file(element, &path);
                    CefString.Clear(&path);
                    break;
            }
            body->add_element(body, element);
        }
        request->set_post_data(request, body);
    }

    private static unsafe void SetHeader(cef_request_t* request, string name, string value)
    {
        cef_string_utf16_t key = default;
        cef_string_utf16_t text = default;
        CefString.Set(&key, name);
        CefString.Set(&text, value);
        request->set_header_by_name(request, &key, &text, 1);
        CefString.Clear(&key);
        CefString.Clear(&text);
    }
}
