using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

public sealed unsafe partial class Session
{
    // Chromium's spellcheck preferences.
    private const string SpellCheckEnablePreference = "browser.enable_spellchecking";
    private const string SpellCheckDictionariesPreference = "spellcheck.dictionaries";

    // Chromium writes added words here, in the profile, with a checksum line at the end.
    private const string CustomDictionaryFile = "Custom Dictionary.txt";

    private readonly HashSet<string> _addedWords = new(StringComparer.Ordinal);

    /// <summary>The languages the built-in spell checker has dictionaries for; Chromium's list. Electron's
    /// <c>ses.availableSpellCheckerLanguages</c>.</summary>
    public static IReadOnlyList<string> AvailableSpellCheckerLanguages { get; } =
    [
        "af", "bg", "ca", "cs", "cy", "da", "de", "de-DE", "el", "en-AU", "en-CA", "en-GB", "en-GB-oxendict", "en-US", "es",
        "es-419", "es-AR", "es-ES", "es-MX", "es-US", "et", "fa", "fo", "fr", "fr-FR", "he", "hi", "hr", "hu", "hy", "id", "it",
        "it-IT", "ko", "lt", "lv", "nb", "nl", "pl", "pt", "pt-BR", "pt-PT", "ro", "ru", "sh", "sk", "sl", "sq", "sr", "sv", "ta",
        "tg", "tr", "uk", "vi",
    ];

    /// <summary>Turns the spell checker on or off for every page of the session. Electron's <c>ses.setSpellCheckerEnabled()</c>.</summary>
    public void SetSpellCheckerEnabled(bool enable)
    {
        WaitUntilInitialized();
        CefThread.InvokeOnUi(() =>
        {
            var value = CefNative.cef_value_create();
            value->set_bool(value, enable ? 1 : 0);
            SetPreference(_context, SpellCheckEnablePreference, value);
            return true;
        });
    }

    /// <summary>Electron's <c>ses.isSpellCheckerEnabled()</c>.</summary>
    public bool IsSpellCheckerEnabled()
    {
        WaitUntilInitialized();
        return CefThread.InvokeOnUi(() => ReadPreference(SpellCheckEnablePreference, v => v->get_bool(v) != 0, true));
    }

    /// <summary>The languages to check spelling in; each must be in <see cref="AvailableSpellCheckerLanguages"/>. Does
    /// nothing on macOS, whose own spell checker picks the language. Electron's <c>ses.setSpellCheckerLanguages()</c>.</summary>
    /// <exception cref="ArgumentException">A language has no dictionary.</exception>
    public void SetSpellCheckerLanguages(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);
        var list = languages.ToList();
        foreach (var language in list)
            if (!AvailableSpellCheckerLanguages.Contains(language))
                throw new ArgumentException($"Invalid language code provided: \"{language}\" is not a valid language code", nameof(languages));
        if (OperatingSystem.IsMacOS()) return;
        WaitUntilInitialized();
        CefThread.InvokeOnUi(() =>
        {
            var values = CefNative.cef_list_value_create();
            for (var i = 0; i < list.Count; i++)
            {
                cef_string_utf16_t text = default;
                CefString.Set(&text, list[i]);
                values->set_string(values, (nuint)i, &text);
                CefString.Clear(&text);
            }
            var value = CefNative.cef_value_create();
            value->set_list(value, values);
            SetPreference(_context, SpellCheckDictionariesPreference, value);
            return true;
        });
    }

    /// <summary>The languages spelling is checked in. Electron's <c>ses.getSpellCheckerLanguages()</c>.</summary>
    public IReadOnlyList<string> GetSpellCheckerLanguages()
    {
        WaitUntilInitialized();
        return CefThread.InvokeOnUi(() => ReadPreference<IReadOnlyList<string>>(SpellCheckDictionariesPreference, v =>
    {
        var list = v->get_list(v);
        var languages = new List<string>();
        for (nuint i = 0; i < list->get_size(list); i++) languages.Add(CefString.ReadUserFree(list->get_string(list, i)));
        NativeObject.Release(list);
        return languages;
    }, []));
    }

    /// <summary>Adds a word to the session's custom dictionary; false for an in-memory session, as Electron, or when the
    /// session has no open page to add it through. Electron's <c>ses.addWordToSpellCheckerDictionary()</c>.</summary>
    public bool AddWordToSpellCheckerDictionary(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        if (!IsPersistent()) return false;
        WebContents? page;
        lock (_pages) page = _pages.Select(p => p.TryGetTarget(out var target) ? target : null).FirstOrDefault(p => p is { HasBrowser: true });
        if (page is null) return false;
        lock (_addedWords) _addedWords.Add(word);
        page.AddWordToDictionary(word);
        return true;
    }

    /// <summary>The words in the session's custom dictionary. Electron's <c>ses.listWordsInSpellCheckerDictionary()</c>.</summary>
    public Task<IReadOnlyList<string>> ListWordsInSpellCheckerDictionaryAsync() => Task.Run<IReadOnlyList<string>>(() =>
    {
        var words = new SortedSet<string>(StringComparer.Ordinal);
        lock (_addedWords) words.UnionWith(_addedWords);
        if (_storagePath is { } path && File.Exists(Path.Combine(path, CustomDictionaryFile)))
            words.UnionWith(File.ReadLines(Path.Combine(path, CustomDictionaryFile)).Where(l => l.Length > 0 && !l.StartsWith("checksum_v1 = ", StringComparison.Ordinal)));
        return [.. words];
    });

    /// <summary>Not supported: CEF can add a word to the custom dictionary but not remove one. Electron's
    /// <c>ses.removeWordFromSpellCheckerDictionary()</c>.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public bool RemoveWordFromSpellCheckerDictionary(string word)
        => throw new NotSupportedException("Removing a word from the spell checker's dictionary is not supported: CEF has no way to.");

    /// <summary>Not supported: CEF downloads dictionaries from Chromium's own server. Electron's
    /// <c>ses.setSpellCheckerDictionaryDownloadURL()</c>.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public void SetSpellCheckerDictionaryDownloadUrl(string url)
        => throw new NotSupportedException("A dictionary download URL is not supported: CEF downloads dictionaries from Chromium's server.");


    private delegate T ValueReader<T>(cef_value_t* value);

    // UI thread.
    private T ReadPreference<T>(string name, ValueReader<T> read, T fallback)
    {
        cef_string_utf16_t key = default;
        CefString.Set(&key, name);
        var manager = (cef_preference_manager_t*)_context;
        var value = manager->get_preference(manager, &key);
        CefString.Clear(&key);
        if (value is null) return fallback;
        try { return read(value); }
        finally { NativeObject.Release(value); }
    }
}
