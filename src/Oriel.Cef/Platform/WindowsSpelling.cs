using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The Windows spell checker's suggestions, which Chromium leaves out of a context menu's details on Windows.</summary>
public static unsafe class WindowsSpelling
{
    private static readonly Guid s_clsidFactory = new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC");
    private static readonly Guid s_iidFactory = new("8E018A9D-2415-4677-BF08-794EA61F94BB");

    // Chromium's spellcheck::kMaxSuggestions.
    private const int MaxSuggestions = 5;
    private const uint InprocServer = 1;

    // ISpellCheckerFactory, ISpellChecker and IEnumString vtable slots, after IUnknown's three.
    private const int SupportedLanguagesSlot = 3;
    private const int CreateSpellCheckerSlot = 5;
    private const int SuggestSlot = 5;
    private const int NextSlot = 3;
    private const int ReleaseSlot = 2;
    private const int S_OK = 0;

    /// <summary>The Windows spell checker's suggestions for <paramref name="word"/> in each of <paramref name="languages"/>
    /// it has a dictionary for, interleaved as Chromium's <c>FillSuggestions</c>: what Electron's patched Chromium puts
    /// in the context menu's details.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> Suggestions(string word, IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        if (word.Length == 0) return [];
        void* factory = null;
        var clsid = s_clsidFactory;
        var iid = s_iidFactory;
        if (CoCreateInstance(&clsid, null, InprocServer, &iid, &factory) < 0) return [];
        try
        {
            var supported = Strings(factory, SupportedLanguagesSlot, null);
            var perLanguage = new List<List<string>>();
            foreach (var tag in languages.Select(l => WindowsTag(l, supported)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                void* checker = null;
                fixed (char* t = tag)
                    if (((delegate* unmanaged<void*, char*, void**, int>)Slot(factory, CreateSpellCheckerSlot))(factory, t, &checker) < 0) continue;
                try { perLanguage.Add(Strings(checker, SuggestSlot, word)); }
                finally { Release(checker); }
            }
            return Interleave(perLanguage);
        }
        finally { Release(factory); }
    }

    /// <summary>The Windows dictionary for a Chromium language: the same tag, else the first installed one that narrows
    /// it, as Chromium maps <c>fr</c> to an installed <c>fr-FR</c>.</summary>
    internal static string? WindowsTag(string language, IReadOnlyList<string> supported)
        => supported.FirstOrDefault(s => s.Equals(language, StringComparison.OrdinalIgnoreCase))
           ?? supported.FirstOrDefault(s => s.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>Chromium's <c>spellcheck::FillSuggestions</c>: one from each language in turn, no repeats, at most five.</summary>
    internal static IReadOnlyList<string> Interleave(IReadOnlyList<IReadOnlyList<string>> perLanguage)
    {
        var result = new List<string>();
        var longest = perLanguage.Count == 0 ? 0 : perLanguage.Max(l => l.Count);
        for (var count = 0; count < longest * perLanguage.Count && result.Count < MaxSuggestions; count++)
        {
            var list = perLanguage[count % perLanguage.Count];
            var index = count / perLanguage.Count;
            if (index < list.Count && !result.Contains(list[index])) result.Add(list[index]);
        }
        return result;
    }

    // Suggest answers S_FALSE, with the word itself, for a correct word; Chromium reads only an S_OK enumeration.
    private static List<string> Strings(void* com, int slot, string? word)
    {
        var result = new List<string>();
        void* items = null;
        int hr;
        if (word is null) hr = ((delegate* unmanaged<void*, void**, int>)Slot(com, slot))(com, &items);
        else fixed (char* w = word) hr = ((delegate* unmanaged<void*, char*, void**, int>)Slot(com, slot))(com, w, &items);
        if (hr != S_OK || items is null)
        {
            if (items is not null && hr >= 0) Release(items);
            return result;
        }
        try
        {
            char* item;
            while (((delegate* unmanaged<void*, uint, char**, uint*, int>)Slot(items, NextSlot))(items, 1, &item, null) == S_OK)
            {
                result.Add(new string(item));
                Marshal.FreeCoTaskMem((nint)item);
            }
        }
        finally { Release(items); }
        return result;
    }

    private static void Release(void* com) => ((delegate* unmanaged<void*, uint>)Slot(com, ReleaseSlot))(com);

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
}
