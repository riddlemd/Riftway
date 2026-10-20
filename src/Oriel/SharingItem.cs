namespace Oriel;

/// <summary>What a share menu shares. Electron's <c>SharingItem</c>.</summary>
public sealed record SharingItem
{
    /// <summary>Text to share. Electron's <c>texts</c>.</summary>
    public IReadOnlyList<string>? Texts { get; init; }

    /// <summary>Files to share. Electron's <c>filePaths</c>.</summary>
    public IReadOnlyList<string>? FilePaths { get; init; }

    /// <summary>URLs to share. Electron's <c>urls</c>.</summary>
    public IReadOnlyList<string>? Urls { get; init; }

    // AppKit raises (aborting the process, Electron's too) when ConvertSharingItemToNS adds the nil NSURL that an empty
    // path or an invalid URL becomes.
    internal SharingItem Validated()
    {
        foreach (var path in FilePaths ?? [])
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A sharing item's file path is empty.", nameof(FilePaths));
        foreach (var url in Urls ?? [])
            if (url is null || Gurl.Spec(url).Length == 0) throw new ArgumentException($"A sharing item's URL is not valid: {url}", nameof(Urls));
        return this;
    }

    /// <summary>ConvertSharingItemToNS's order: texts, then files, then URLs as GURL spells them.</summary>
    internal (IReadOnlyList<string> Texts, IReadOnlyList<string> FilePaths, IReadOnlyList<string> Urls) ForMac()
        => (Texts ?? [], FilePaths ?? [], [.. (Urls ?? []).Select(Gurl.Spec)]);
}
