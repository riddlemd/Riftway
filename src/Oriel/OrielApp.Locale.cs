using System.Globalization;
using Oriel.Cef.Platform;

namespace Oriel;

public sealed unsafe partial class OrielApp
{
    /// <summary>The user's languages in order of preference. Electron's <c>app.getPreferredSystemLanguages()</c>.</summary>
    public IReadOnlyList<string> GetPreferredSystemLanguages()
    {
        if (OperatingSystem.IsMacOS()) return MacLocale.PreferredLanguages();
        if (OperatingSystem.IsLinux()) return LinuxLanguages(Environment.GetEnvironmentVariable);
        return [CultureInfo.CurrentUICulture.Name];
    }

    /// <summary>The locale the OS is set to, e.g. <c>en-US</c>. Electron's <c>app.getSystemLocale()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready, as Electron throws.</exception>
    public string GetSystemLocale()
    {
        if (!IsReady) throw new InvalidOperationException("app.getSystemLocale() can only be called after app is ready");
        if (OperatingSystem.IsMacOS()) return MacLocale.CurrentIdentifier().Replace('_', '-');
        if (OperatingSystem.IsLinux()) return LinuxConfiguredLocale(Environment.GetEnvironmentVariable);
        return CultureInfo.CurrentCulture.Name;
    }

    /// <summary>The two-letter country code of the user's locale, or empty. Electron's <c>app.getLocaleCountryCode()</c>.</summary>
    public string GetLocaleCountryCode()
    {
        var region = OperatingSystem.IsMacOS() ? MacLocale.CountryCode()
            : OperatingSystem.IsLinux() ? LinuxRegion(Environment.GetEnvironmentVariable)
            : RegionInfo.CurrentRegion.TwoLetterISORegionName;
        return region.Length == 2 ? region : "";
    }

    // GLib's g_get_language_names: LANGUAGE (a list), else LC_ALL, LC_MESSAGES, LANG; each locale with its variants,
    // longest first; "C" dropped and each canonicalised, as Electron's language_util_linux.cc does.
    internal static IReadOnlyList<string> LinuxLanguages(Func<string, string?> environment)
    {
        var value = new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" }.Select(environment).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "C";
        var languages = new List<string>();
        foreach (var locale in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var variant in Variants(locale))
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

    // Electron's: the region of the LC_TIME locale (LC_ALL, LC_TIME, then LANG, as setlocale reads them).
    internal static string LinuxRegion(Func<string, string?> environment)
    {
        var locale = new[] { "LC_ALL", "LC_TIME", "LANG" }.Select(environment).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "C";
        var dot = locale.IndexOf('.');
        if (dot >= 0) locale = locale[..dot];
        var underscore = locale.IndexOf('_');
        return underscore >= 0 ? locale[(underscore + 1)..] : "";
    }
}
