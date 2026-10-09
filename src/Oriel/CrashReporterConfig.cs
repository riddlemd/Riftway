using System.Text;

namespace Oriel;

/// <summary>A crash key slot declared in <c>crash_reporter.cfg</c>: CEF only records keys named there at startup.</summary>
internal readonly record struct CrashKeySlot(string Name, string Size)
{
    /// <summary>The bytes CEF keeps of a value in this slot (cef_crash_util.h).</summary>
    public int MaxBytes => Size switch { "small" => 64, "medium" => 256, _ => 1024 };
}

/// <summary>Builds and reads CEF's <c>crash_reporter.cfg</c> (see include/cef_crash_util.h) for <see cref="CrashReporter"/>.</summary>
internal static class CrashReporterConfig
{
    /// <summary>The file CEF looks for beside the executable (Windows, Linux) or in the bundle's Resources (macOS).</summary>
    public const string FileName = "crash_reporter.cfg";

    /// <summary>First line of a cfg Oriel wrote; a cfg without it belongs to the app's packaging and is left alone.</summary>
    public const string Marker = "# Written by Oriel CrashReporter.Start(); rewritten on every start.";

    /// <summary>CEF allows 26 keys of each size.</summary>
    public const int SlotsPerSize = 26;

    /// <summary>Electron ignores (with a warning) crash key names this long or longer (shell/common/crash_keys.cc).</summary>
    public const int MaxKeyNameBytes = 40;

    /// <summary>Crashpad's default pruning (PruneCondition::GetDefault), which Electron keeps; CEF's own defaults are 20 MB and 5 days.</summary>
    public const int MaxDatabaseSizeInMb = 128;

    /// <summary>See <see cref="MaxDatabaseSizeInMb"/>.</summary>
    public const int MaxDatabaseAgeInDays = 365;

    /// <summary>Electron's rateLimit is one upload per hour; CEF's patched Crashpad counts uploads per 24 hours.</summary>
    public const int RateLimitedUploadsPerDay = 24;

    /// <summary>Set in every process, as Electron's process_type and platform keys are.</summary>
    public static readonly string[] FixedKeys = ["process_type", "platform"];

    /// <summary>Electron's value of the <c>platform</c> crash key on this OS.</summary>
    public static string Platform => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";

    /// <summary>Where CEF reads the cfg for an app whose executable is in <paramref name="exeDirectory"/>.</summary>
    public static string PathFor(string exeDirectory) => OperatingSystem.IsMacOS()
        ? Path.GetFullPath(Path.Combine(exeDirectory, "..", "Resources", FileName))
        : Path.Combine(exeDirectory, FileName);

    /// <summary>Whether CEF's line-based parser keeps <paramref name="key"/> intact as a [CrashKeys] name.</summary>
    public static bool IsDeclarable(string key)
        => key.Length > 0 && key.Trim() == key && key.IndexOfAny(['=', '\r', '\n']) < 0 && key[0] != '#' && key[0] != '[';

    /// <summary>Assigns slots: the fixed keys small, then the rest large, spilling to medium and small once 26 are used.</summary>
    /// <remarks>Duplicates and undeclarable names are skipped; keys beyond all 78 slots are dropped.</remarks>
    public static List<CrashKeySlot> AssignSlots(IEnumerable<string> keys)
    {
        var slots = new List<CrashKeySlot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int small = 0, medium = 0, large = 0;
        foreach (var key in FixedKeys)
        {
            seen.Add(key);
            slots.Add(new(key, "small"));
            small++;
        }
        foreach (var key in keys)
        {
            if (!IsDeclarable(key) || !seen.Add(key)) continue;
            if (large < SlotsPerSize) { slots.Add(new(key, "large")); large++; }
            else if (medium < SlotsPerSize) { slots.Add(new(key, "medium")); medium++; }
            else if (small < SlotsPerSize) { slots.Add(new(key, "small")); small++; }
        }
        return slots;
    }

    /// <summary>The cfg text for one <see cref="CrashReporter.Start"/>.</summary>
    /// <param name="appName">Names the database folder: %LOCALAPPDATA%\&lt;appName&gt;\User Data\Crashpad on Windows.</param>
    /// <param name="productName">The <c>product</c> annotation on every report.</param>
    /// <param name="version">The <c>version</c> annotation on every report.</param>
    /// <param name="submitUrl">Empty keeps reports on disk only.</param>
    /// <param name="rateLimit">Electron's <c>rateLimit</c>.</param>
    /// <param name="ignoreSystemCrashHandler">Electron's <c>ignoreSystemCrashHandler</c> (macOS only in CEF).</param>
    /// <param name="externalHandler">Windows: the exe CEF launches as the crashpad handler; null relaunches the main exe.</param>
    /// <param name="slots">From <see cref="AssignSlots"/>.</param>
    public static string Build(string appName, string productName, string version, string submitUrl, bool rateLimit,
        bool ignoreSystemCrashHandler, string? externalHandler, IEnumerable<CrashKeySlot> slots)
    {
        var text = new StringBuilder();
        text.Append(Marker).Append('\n');
        text.Append("[Config]\n");
        text.Append("ProductName=").Append(SingleLine(productName)).Append('\n');
        text.Append("ProductVersion=").Append(SingleLine(version)).Append('\n');
        text.Append("AppName=").Append(SingleLine(appName)).Append('\n');
        if (!string.IsNullOrEmpty(externalHandler)) text.Append("ExternalHandler=").Append(SingleLine(externalHandler)).Append('\n');
        if (OperatingSystem.IsMacOS())
            text.Append("BrowserCrashForwardingEnabled=").Append(ignoreSystemCrashHandler ? "false" : "true").Append('\n');
        if (!string.IsNullOrEmpty(submitUrl)) text.Append("ServerURL=").Append(SingleLine(submitUrl)).Append('\n');
        text.Append("RateLimitEnabled=").Append(rateLimit ? "true" : "false").Append('\n');
        if (rateLimit) text.Append("MaxUploadsPerDay=").Append(RateLimitedUploadsPerDay).Append('\n');
        text.Append("MaxDatabaseSizeInMb=").Append(MaxDatabaseSizeInMb).Append('\n');
        text.Append("MaxDatabaseAgeInDays=").Append(MaxDatabaseAgeInDays).Append('\n');
        text.Append("[CrashKeys]\n");
        foreach (var slot in slots) text.Append(slot.Name).Append('=').Append(slot.Size).Append('\n');
        return text.ToString();
    }

    /// <summary>The [CrashKeys] names and sizes in <paramref name="cfg"/>, read as CEF's parser does.</summary>
    public static List<CrashKeySlot> ReadSlots(string cfg)
    {
        var slots = new List<CrashKeySlot>();
        var inKeys = false;
        foreach (var raw in cfg.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                inKeys = line == "[CrashKeys]";
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inKeys || equals <= 0) continue;
            var size = line[(equals + 1)..].Trim();
            if (size is "small" or "medium" or "large") slots.Add(new(line[..equals].Trim(), size));
        }
        return slots;
    }

    /// <summary>The leading bytes of <paramref name="value"/> that fit <paramref name="maxBytes"/> of UTF-8, whole characters only.</summary>
    public static string Truncate(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var bytes = 0;
        var end = 0;
        while (end < value.Length)
        {
            var width = char.IsHighSurrogate(value[end]) && end + 1 < value.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(value.AsSpan(end, width));
            if (bytes + size > maxBytes) break;
            bytes += size;
            end += width;
        }
        return value[..end];
    }

    /// <summary>Key/value pairs as the hex of "k\0v\0k\0v\0" in UTF-8: survives any command-line quoting.</summary>
    /// <remarks>Stops before the encoding exceeds <paramref name="maxChars"/>; Windows command lines end at 32767.</remarks>
    public static string EncodePairs(IEnumerable<KeyValuePair<string, string>> pairs, int maxChars = 24000)
    {
        var hex = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            var bytes = Encoding.UTF8.GetBytes(key + "\0" + value + "\0");
            if (hex.Length + bytes.Length * 2 > maxChars) break;
            hex.Append(Convert.ToHexString(bytes));
        }
        return hex.ToString();
    }

    /// <summary>Reverses <see cref="EncodePairs"/>; the native subprocess decodes the same format.</summary>
    public static List<KeyValuePair<string, string>> DecodePairs(string hex)
    {
        var parts = Encoding.UTF8.GetString(Convert.FromHexString(hex)).Split('\0');
        var pairs = new List<KeyValuePair<string, string>>();
        for (var i = 0; i + 1 < parts.Length; i += 2) pairs.Add(new(parts[i], parts[i + 1]));
        return pairs;
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
