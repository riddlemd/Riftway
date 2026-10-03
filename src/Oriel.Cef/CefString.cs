using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>Conversions between .NET strings and CEF's UTF-16 <c>cef_string_t</c>.</summary>
public static unsafe class CefString
{
    /// <summary>Copies <paramref name="value"/> into <paramref name="target"/>; free it with <see cref="Clear"/>.</summary>
    public static void Set(cef_string_utf16_t* target, string? value)
    {
        fixed (char* chars = value)
        {
            CefNative.cef_string_utf16_set(chars, (nuint)(value?.Length ?? 0), target, 1);
        }
    }

    /// <summary>Reads a CEF string without taking ownership of it.</summary>
    public static string Read(cef_string_utf16_t* value)
        => value == null || value->str == null ? string.Empty : new string(value->str, 0, (int)value->length);

    /// <summary>Reads and frees a <c>cef_string_userfree_t</c> returned by CEF.</summary>
    public static string ReadUserFree(cef_string_utf16_t* value)
    {
        if (value == null) return string.Empty;
        var result = Read(value);
        CefNative.cef_string_userfree_utf16_free(value);
        return result;
    }

    /// <summary>Frees the buffer of a string written by <see cref="Set"/>.</summary>
    public static void Clear(cef_string_utf16_t* value) => CefNative.cef_string_utf16_clear(value);
}
