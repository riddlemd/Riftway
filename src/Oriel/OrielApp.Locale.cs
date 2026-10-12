using System.Runtime.InteropServices;
using Oriel.Cef.Platform;

namespace Oriel;

public sealed unsafe partial class OrielApp
{
    /// <summary>The locale Chromium runs the app in (its UI strings and the renderers' default locale), or empty before
    /// <see cref="Ready"/>. Electron's <c>app.getLocale()</c>: Chromium's choice is recomputed from its inputs, CEF's
    /// <c>--lang</c> on macOS and Windows (en-US unless set) and the environment's languages on Linux, against the locale
    /// files that ship (drift A30).</summary>
    public string GetLocale()
    {
        if (!IsReady) return "";
        var lang = GlobalSwitch("lang") ?? "";
        var candidates = OperatingSystem.IsLinux() ? LinuxLanguages(Environment.GetEnvironmentVariable) : [];
        return ResolveApplicationLocale(OperatingSystem.IsLinux() ? "" : lang, candidates, LocalePakExists);
    }

    private static unsafe string? GlobalSwitch(string name)
    {
        var line = Oriel.Cef.Interop.CefNative.cef_command_line_get_global();
        if (line is null) return null;
        Oriel.Cef.Interop.cef_string_utf16_t key = default;
        Oriel.Cef.CefString.Set(&key, name);
        try { return line->has_switch(line, &key) != 0 ? Oriel.Cef.CefString.ReadUserFree(line->get_switch_value(line, &key)) : null; }
        finally
        {
            Oriel.Cef.CefString.Clear(&key);
            Oriel.Cef.NativeObject.Release(line);
        }
    }

    // ResourceBundle::LocaleDataPakExists: <locale>.pak in locales/ beside libcef; on macOS <locale>.lproj/locale.pak in the
    // framework's Resources, with en-US as "en" and "_" for "-".
    private static bool LocalePakExists(string locale)
    {
        var module = Path.GetDirectoryName(CefLayout.MainProcessLibrary())!;
        if (!OperatingSystem.IsMacOS()) return File.Exists(Path.Combine(module, "locales", locale + ".pak"));
        var macLocale = locale == "en-US" ? "en" : locale.Replace('-', '_');
        return File.Exists(Path.Combine(module, "Resources", macLocale + ".lproj", "locale.pak"));
    }

    // l10n_util::GetApplicationLocaleInternal: the preferred locale if it resolves; else the first candidate with its own
    // locale file, else the first that resolves; else en-US if it ships.
    internal static string ResolveApplicationLocale(string preferred, IReadOnlyList<string> candidates, Func<string, bool> pakExists)
    {
        if (preferred.Length > 0 && ResolveLocale(preferred, pakExists) is { } resolved) return resolved;
        string? matched = null;
        foreach (var raw in candidates)
        {
            var candidate = CanonicalTag(raw);
            if (candidate.Length == 0) continue;
            if (pakExists(candidate)) return candidate;
            matched ??= ResolveLocale(candidate, pakExists);
        }
        return matched ?? (pakExists("en-US") ? "en-US" : "");
    }

    // CheckAndResolveLocale: the locale itself when it ships, else what Chromium's language matcher maps it to, when
    // that ships. The matcher is reduced to the regional fallbacks Chromium's l10n tests pin down.
    internal static string? ResolveLocale(string locale, Func<string, bool> pakExists)
    {
        var tag = CanonicalTag(locale);
        if (tag.Length == 0) return null;
        if (pakExists(tag)) return tag;
        var dash = tag.IndexOf('-');
        var language = dash < 0 ? tag : tag[..dash];
        var region = dash < 0 ? "" : tag[(dash + 1)..];
        var match = language switch
        {
            "en" => region is "AU" or "NZ" or "CA" or "ZA" or "IN" or "IE" or "GB" ? "en-GB" : "en-US",
            "es" => region is "" or "ES" ? "es" : "es-419",
            "pt" => region is "" or "BR" ? "pt-BR" : "pt-PT",
            "zh" => region is "HK" or "MO" or "TW" || region.StartsWith("Hant", StringComparison.Ordinal) ? "zh-TW" : "zh-CN",
            "iw" => "he",
            "no" or "nn" => "nb",
            "tl" => "fil",
            _ => language,
        };
        return pakExists(match) ? match : null;
    }

    // A POSIX or BCP 47 name as a tag: no codeset or modifier, "-" between the parts, the region upper-cased.
    private static string CanonicalTag(string locale)
    {
        var end = locale.IndexOfAny(['.', '@']);
        var parts = (end >= 0 ? locale[..end] : locale).Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is "C" or "POSIX") return "";
        parts[0] = parts[0].ToLowerInvariant();
        for (var i = 1; i < parts.Length; i++) parts[i] = parts[i].Length == 2 ? parts[i].ToUpperInvariant() : parts[i];
        return string.Join('-', parts);
    }

    /// <summary>The user's languages in order of preference. Electron's <c>app.getPreferredSystemLanguages()</c>.</summary>
    public IReadOnlyList<string> GetPreferredSystemLanguages()
    {
        if (OperatingSystem.IsMacOS()) return MacLocale.PreferredLanguages();
        if (OperatingSystem.IsLinux()) return LinuxLanguages(Environment.GetEnvironmentVariable);
        return OperatingSystem.IsWindows() ? WindowsLocale.PreferredLanguages() : [];
    }

    /// <summary>The locale the OS is set to, e.g. <c>en-US</c>. Electron's <c>app.getSystemLocale()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready, as Electron throws.</exception>
    public string GetSystemLocale()
    {
        if (!IsReady) throw new InvalidOperationException("app.getSystemLocale() can only be called after app is ready");
        if (OperatingSystem.IsMacOS()) return MacLocale.CurrentIdentifier().Replace('_', '-');
        if (OperatingSystem.IsLinux()) return LinuxConfiguredLocale(Environment.GetEnvironmentVariable);
        return OperatingSystem.IsWindows() ? WindowsLocale.ConfiguredLocale() : "en-US";
    }

    /// <summary>The two-letter country code of the user's locale, or empty. Electron's <c>app.getLocaleCountryCode()</c>.</summary>
    public string GetLocaleCountryCode()
    {
        var region = OperatingSystem.IsMacOS() ? MacLocale.CountryCode()
            : OperatingSystem.IsLinux() ? LinuxRegion(IsReady ? LibcTimeLocale() : LinuxTimeLocale(Environment.GetEnvironmentVariable))
            : OperatingSystem.IsWindows() ? WindowsLocale.IsoCountryName() : "";
        return CountryCodeOrEmpty(region);
    }

    // Electron returns the region only when it is exactly two characters, on every OS.
    internal static string CountryCodeOrEmpty(string region) => region.Length == 2 ? region : "";

    // GLib's g_get_language_names: LANGUAGE (a list), else LC_ALL, LC_MESSAGES, LANG; each locale with its variants,
    // longest first; "C" dropped and each canonicalised, as Electron's language_util_linux.cc does.
    internal static IReadOnlyList<string> LinuxLanguages(Func<string, string?> environment) => LinuxLanguages(environment, GLibAlias);

    // Each entry is first unaliased through GLib's locale.alias, as g_get_language_names does.
    internal static IReadOnlyList<string> LinuxLanguages(Func<string, string?> environment, Func<string, string?> alias)
    {
        var value = new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" }.Select(environment).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "C";
        var languages = new List<string>();
        foreach (var locale in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var variant in Variants(Unalias(locale, alias)))
                if (variant != "C") languages.Add(Canonical(variant));
        return languages;
    }

    // language[_territory][.codeset][@modifier]: every combination, from all parts to the language alone.
    private static IEnumerable<string> Variants(string locale)
    {
        var at = locale.IndexOf('@');
        var modifier = at >= 0 ? locale[at..] : "";
        var rest = at >= 0 ? locale[..at] : locale;
        var dot = rest.IndexOf('.');
        var codeset = dot >= 0 ? rest[dot..] : "";
        rest = dot >= 0 ? rest[..dot] : rest;
        var underscore = rest.IndexOf('_');
        var territory = underscore >= 0 ? rest[underscore..] : "";
        var language = underscore >= 0 ? rest[..underscore] : rest;
        var mask = (codeset.Length > 0 ? 1 : 0) | (territory.Length > 0 ? 2 : 0) | (modifier.Length > 0 ? 4 : 0);
        for (var i = mask; i >= 0; i--)
            if ((i & ~mask) == 0)
                yield return language + ((i & 2) != 0 ? territory : "") + ((i & 1) != 0 ? codeset : "") + ((i & 4) != 0 ? modifier : "");
    }

    // ICU's canonical form, as base::i18n::GetCanonicalLocale: no codeset or modifier, '-' between the parts.
    private static string Canonical(string locale)
    {
        var end = locale.IndexOfAny(['.', '@']);
        return (end >= 0 ? locale[..end] : locale).Replace('_', '-');
    }

    // ICU's default locale from LC_ALL, LC_MESSAGES or LANG, as base::i18n::GetConfiguredLocale prints it: language-region,
    // then any variant as @lowercase. C and POSIX are ICU's en_US_POSIX.
    internal static string LinuxConfiguredLocale(Func<string, string?> environment)
    {
        var value = new[] { "LC_ALL", "LC_MESSAGES", "LANG" }.Select(environment).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "C";
        if (value is "C" or "POSIX" || value.StartsWith("C.", StringComparison.Ordinal)) return "en-US@posix";
        var at = value.IndexOf('@');
        var modifier = at >= 0 ? "@" + value[(at + 1)..].ToLowerInvariant() : "";
        return Canonical(value) + modifier;
    }

    // GLib's unalias_lang: follow the alias chain, at most 31 steps.
    internal static string Unalias(string locale, Func<string, string?> alias)
    {
        for (var i = 0; i <= 30 && alias(locale) is { } next && next != locale; i++) locale = next;
        return locale;
    }

    private static readonly Lazy<Dictionary<string, string>> s_glibAliases = new(() => ReadAliases("/usr/share/locale/locale.alias"));

    private static string? GLibAlias(string locale) => s_glibAliases.Value.GetValueOrDefault(locale);

    // GLib's read_aliases: "alias value" per line, whitespace-separated, # comments; the first entry for an alias wins.
    internal static Dictionary<string, string> ReadAliases(string path)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return aliases;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith('#')) continue;
            aliases.TryAdd(parts[0], parts[1]);
        }
        return aliases;
    }

    // What setlocale(LC_TIME, "") would read: LC_ALL, LC_TIME, then LANG. Used before Chromium has applied it.
    internal static string LinuxTimeLocale(Func<string, string?> environment)
        => new[] { "LC_ALL", "LC_TIME", "LANG" }.Select(environment).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "C";

    // Chromium has called setlocale(LC_ALL, "") by Ready, so this is the locale the C library took: one it lacks is C.
    private static string LibcTimeLocale() => Marshal.PtrToStringUTF8(setlocale(LcTime, null)) ?? "C";

    private const int LcTime = 2;

    [DllImport("libc", EntryPoint = "setlocale")] private static extern nint setlocale(int category, string? locale);

    // Electron's: the region of the LC_TIME locale.
    internal static string LinuxRegion(string locale)
    {
        var dot = locale.IndexOf('.');
        if (dot >= 0) locale = locale[..dot];
        var underscore = locale.IndexOf('_');
        return underscore >= 0 ? locale[(underscore + 1)..] : "";
    }
}
