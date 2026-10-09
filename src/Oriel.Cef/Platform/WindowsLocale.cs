using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Oriel.Cef.Platform;

/// <summary>The Win32 locale answers Electron reads on Windows: the preferred languages, ICU's default locale, and the
/// ISO 3166 country name.</summary>
public static unsafe class WindowsLocale
{
    private const uint MuiLanguageName = 0x8;
    private const uint MuiMergeSystemFallback = 0x10;
    private const uint MuiMergeUserFallback = 0x20;
    private const uint LocaleSName = 0x5C;
    private const uint LocaleSIso3166CtryName = 0x5A;
    private const int LocaleNameMaxLength = 85;

    // IGlobalizationPreferencesStatics' vtable: IInspectable's six slots, then four getters before Languages.
    private const int GetLanguagesSlot = 9;
    private const int VectorGetAtSlot = 6;
    private const int VectorGetSizeSlot = 7;

    private const string GlobalizationPreferencesClass = "Windows.System.UserProfile.GlobalizationPreferences";
    private static readonly Guid s_iidGlobalizationPreferencesStatics = new("01BF4326-ED37-4E96-B0E9-C1340D1EA158");

    /// <summary>The user's languages, most preferred first, as BCP-47 names. Electron's
    /// <c>GetPreferredLanguages</c>: WinRT <c>GlobalizationPreferences.Languages</c>, else the thread's UI languages.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> PreferredLanguages()
    {
        var languages = new List<string>();
        if (TryGlobalizationPreferences(languages)) return languages;
        languages.Clear();
        // Chromium's GetThreadPreferredUILanguageList flags.
        TryUiLanguages(MuiMergeSystemFallback | MuiMergeUserFallback, languages);
        return languages;
    }

    /// <summary>ICU's default locale as <c>base::i18n::GetConfiguredLocale</c> prints it, from the user's
    /// <c>LOCALE_SNAME</c>; <c>en-US</c> if Windows gives none.</summary>
    [SupportedOSPlatform("windows")]
    public static string ConfiguredLocale()
    {
        var buffer = stackalloc char[LocaleNameMaxLength];
        var length = GetLocaleInfoEx(null, LocaleSName, buffer, LocaleNameMaxLength);
        return IcuLocale(length > 1 ? new string(buffer, 0, length - 1) : "");
    }

    /// <summary><c>LOCALE_SISO3166CTRYNAME</c> of the user's default locale, else the system's, else empty.</summary>
    [SupportedOSPlatform("windows")]
    public static string IsoCountryName()
    {
        var buffer = stackalloc char[LocaleNameMaxLength];
        // The literal is Win32's LOCALE_NAME_SYSTEM_DEFAULT.
        fixed (char* system = "!x-sys-default-locale")
        {
            var length = GetLocaleInfoEx(null, LocaleSIso3166CtryName, buffer, LocaleNameMaxLength);
            if (length == 0) length = GetLocaleInfoEx(system, LocaleSIso3166CtryName, buffer, LocaleNameMaxLength);
            return length > 1 ? new string(buffer, 0, length - 1) : "";
        }
    }

    /// <summary>Splits the NUL-separated, double-NUL-terminated list the MUI language APIs fill in, dropping empty entries.</summary>
    public static List<string> ParseMultiString(ReadOnlySpan<char> buffer)
    {
        var result = new List<string>();
        foreach (var range in buffer.Split('\0'))
            if (range.End.Value - range.Start.Value > 0) result.Add(buffer[range].ToString());
        return result;
    }

    /// <summary>What ICU's default locale prints for a Windows locale name: <c>uloc_canonicalize</c> of the name with
    /// <c>_</c> as <c>-</c>, then Chromium's <c>language[-Script][-REGION][@variant]</c>. A sort-order suffix such as
    /// <c>de-DE_phoneb</c> becomes a variant. ICU's alias table (legacy tags) is not applied.</summary>
    public static string IcuLocale(string windowsName)
    {
        var parts = windowsName.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length is < 2 or > 3) return "en-US";
        string? script = null, region = null;
        var variants = new List<string>();
        foreach (var part in parts.Skip(1))
        {
            if (variants.Count == 0 && script is null && region is null && part.Length == 4 && part.All(char.IsAsciiLetter))
                script = char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            else if (variants.Count == 0 && region is null && (part.Length == 2 && part.All(char.IsAsciiLetter) || part.Length == 3 && part.All(char.IsAsciiDigit)))
                region = part.ToUpperInvariant();
            else variants.Add(part.ToLowerInvariant());
        }
        var result = new StringBuilder(parts[0].ToLowerInvariant());
        if (script is not null) result.Append('-').Append(script);
        if (region is not null) result.Append('-').Append(region);
        if (variants.Count > 0) result.Append('@').Append(string.Join('_', variants));
        return result.ToString();
    }

    private static bool TryUiLanguages(uint flags, List<string> languages)
    {
        uint count = 0, length = 0;
        var call = flags | MuiLanguageName;
        if (GetThreadPreferredUILanguages(call, &count, null, &length) == 0 || length == 0) return false;
        var buffer = new char[length];
        fixed (char* p = buffer)
            if (GetThreadPreferredUILanguages(call, &count, p, &length) == 0 || count == 0) return false;
        languages.AddRange(ParseMultiString(buffer.AsSpan(0, (int)length)));
        return true;
    }

    private static bool TryGlobalizationPreferences(List<string> languages)
    {
        var factory = Activate();
        if (factory is null) return false;
        void* vector = null;
        try
        {
            if (((delegate* unmanaged<void*, void**, int>)Slot(factory, GetLanguagesSlot))(factory, &vector) < 0) return false;
            uint size;
            if (((delegate* unmanaged<void*, uint*, int>)Slot(vector, VectorGetSizeSlot))(vector, &size) < 0) return false;
            for (uint i = 0; i < size; i++)
            {
                nint text;
                if (((delegate* unmanaged<void*, uint, nint*, int>)Slot(vector, VectorGetAtSlot))(vector, i, &text) < 0) continue;
                uint length;
                var raw = WindowsGetStringRawBuffer(text, &length);
                languages.Add(new string(raw, 0, (int)length));
                WindowsDeleteString(text);
            }
            return true;
        }
        finally
        {
            if (vector is not null) Release(vector);
            Release(factory);
        }
    }

    private static void* Activate()
    {
        fixed (char* name = GlobalizationPreferencesClass)
        {
            nint hstring;
            if (WindowsCreateString(name, (uint)GlobalizationPreferencesClass.Length, &hstring) < 0) return null;
            try
            {
                var iid = s_iidGlobalizationPreferencesStatics;
                void* factory;
                if (RoGetActivationFactory(hstring, &iid, &factory) >= 0) return factory;
                // A thread outside any apartment: join the implicit MTA, as Chromium's threads are in one.
                nint cookie;
                if (CoIncrementMTAUsage(&cookie) < 0) return null;
                return RoGetActivationFactory(hstring, &iid, &factory) >= 0 ? factory : null;
            }
            finally { WindowsDeleteString(hstring); }
        }
    }

    private static void Release(void* com) => ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    [DllImport("kernel32.dll")] private static extern int GetLocaleInfoEx(char* name, uint type, char* data, int length);
    [DllImport("kernel32.dll")] private static extern int GetThreadPreferredUILanguages(uint flags, uint* count, char* buffer, uint* length);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, void** factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(nint hstring, uint* length);
    [DllImport("ole32.dll")] private static extern int CoIncrementMTAUsage(nint* cookie);
}
