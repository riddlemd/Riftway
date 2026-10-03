using System.Globalization;
using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>A custom scheme and its <c>cef_scheme_options_t</c> flags.</summary>
public readonly record struct CustomScheme(string Name, int Options);

/// <summary>The command-line switch that carries custom schemes from the browser to every child process.</summary>
/// <remarks>
/// Chromium requires identical scheme registration in every process, and Oriel's subprocesses have
/// no app code to ask, so the browser appends <c>--oriel-custom-schemes=app:89,media:25</c> to each
/// child's command line and the subprocesses register from it. The C subprocess parses the same format.
/// </remarks>
public static class SchemeSwitch
{
    /// <summary>The switch name, without dashes.</summary>
    public const string Name = "oriel-custom-schemes";

    /// <summary><c>name:options</c> pairs joined by commas.</summary>
    /// <exception cref="ArgumentException">A scheme name is not a valid lowercase RFC 3986 scheme.</exception>
    public static string Format(IEnumerable<CustomScheme> schemes)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        return string.Join(',', schemes.Select(s =>
        {
            if (!IsValidName(s.Name)) throw new ArgumentException($"'{s.Name}' is not a valid lowercase scheme name.", nameof(schemes));
            return string.Create(CultureInfo.InvariantCulture, $"{s.Name}:{s.Options}");
        }));
    }

    /// <summary>Reads the switch value; malformed entries are skipped rather than registered half-right.</summary>
    public static IReadOnlyList<CustomScheme> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return [];

        var schemes = new List<CustomScheme>();
        foreach (var entry in value.Split(','))
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;

            var name = entry[..colon];
            if (!IsValidName(name)) continue;
            if (!int.TryParse(entry.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var options)) continue;

            schemes.Add(new CustomScheme(name, options));
        }

        return schemes;
    }

    /// <summary>Finds the switch in a process's arguments.</summary>
    public static IReadOnlyList<CustomScheme> FromArgs(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        const string prefix = "--" + Name + "=";
        var value = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal));
        return value is null ? [] : Parse(value[prefix.Length..]);
    }

    /// <summary>RFC 3986 scheme syntax, lowercase only, so a name can never contain ',' or ':'.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name[0] is < 'a' or > 'z') return false;
        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '.' or '-')) return false;
        }
        return true;
    }

    /// <summary>Registers <paramref name="schemes"/> with CEF; called from <c>on_register_custom_schemes</c> in every process.</summary>
    public static unsafe void Register(cef_scheme_registrar_t* registrar, IEnumerable<CustomScheme> schemes)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        foreach (var scheme in schemes)
        {
            cef_string_utf16_t name = default;
            CefString.Set(&name, scheme.Name);
            registrar->add_custom_scheme(registrar, &name, scheme.Options);
            CefString.Clear(&name);
        }
    }
}
