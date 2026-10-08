using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The system spell checker's suggestions, which Chromium leaves out of a context menu's details on macOS.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacSpelling
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // Chromium's spellcheck::kMaxSuggestions.
    private const int MaxSuggestions = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRange
    {
        public nuint Location, Length;
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary>Main thread: NSSpellChecker's guesses for <paramref name="word"/>, as Chrome's macOS menu asks for them.</summary>
    public static IReadOnlyList<string> Guesses(string word)
    {
        if (word.Length == 0) return [];
        var checker = Send(Class("NSSpellChecker"u8), Sel("sharedSpellChecker"u8));
        nint text;
        fixed (char* chars = word)
            text = ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), chars, (nuint)word.Length);
        var language = Send(checker, Sel("language"u8));
        var guesses = ((delegate* unmanaged<nint, nint, NSRange, nint, nint, nint, nint>)s_msgSend)(
            checker, Sel("guessesForWordRange:inString:language:inSpellDocumentWithTag:"u8), new NSRange { Length = (nuint)word.Length }, text, language, 0);
        if (guesses == 0) return [];
        var result = new List<string>();
        for (nint i = 0, n = Send(guesses, Sel("count"u8)); i < n && result.Count < MaxSuggestions; i++)
        {
            var guess = ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(guesses, Sel("objectAtIndex:"u8), i);
            if (Marshal.PtrToStringUTF8(Send(guess, Sel("UTF8String"u8))) is { } value) result.Add(value);
        }
        return result;
    }

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
}
