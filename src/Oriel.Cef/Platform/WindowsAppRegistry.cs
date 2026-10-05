using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Oriel.Cef.Platform;

/// <summary>The registry reads and writes behind a Windows app's protocol handler and login item, one per step of
/// Electron's <c>browser_win.cc</c>.</summary>
internal interface IAppRegistryBackend
{
    /// <summary>The <c>shell\open\command</c> default value of <c>HKCU\Software\Classes\&lt;protocol&gt;</c>, read as
    /// <c>base::win::RegKey::ReadValue</c> does; null when a key or the value is missing or unreadable.</summary>
    string? GetProtocolCommand(string protocol);

    /// <summary>Writes <c>URL Protocol</c>, then <c>(Default)</c> = <c>URL:&lt;protocol&gt;</c>, then the open command.
    /// False at the first write the registry refuses.</summary>
    bool WriteProtocol(string protocol, string command);

    /// <summary>Deletes <c>&lt;protocol&gt;\shell</c> and everything under it; false when the registry refuses.</summary>
    bool DeleteProtocolShell(string protocol);

    /// <summary>Deletes the protocol key's <c>URL Protocol</c> and <c>(Default)</c> values, then the key itself if it
    /// has no values and no subkeys left. Failures are ignored.</summary>
    void CleanUpProtocol(string protocol);

    /// <summary>Whether <c>HKCR\&lt;scheme&gt;</c> has a <c>URL Protocol</c> value.</summary>
    bool HasUrlProtocolValue(string scheme);

    /// <summary><c>AssocQueryString(ASSOCF_IS_PROTOCOL, <paramref name="assocStr"/>, scheme)</c> into a 1024-character
    /// buffer, or null when it fails.</summary>
    string? AssocQuery(string scheme, int assocStr);

    /// <summary>The <c>HKCU\...\Run</c> value <paramref name="name"/>, read as <c>RegKey::ReadValue</c> does, or null.</summary>
    string? ReadRunValue(string name);

    /// <summary>Every value of <c>HKCU</c> or <c>HKLM</c> <c>...\Run</c> as <c>RegistryValueIterator</c> yields it: last
    /// index first, the data read as a NUL-terminated UTF-16 string whatever its type.</summary>
    IReadOnlyList<(string Name, string Value)> EnumerateRun(bool machine);

    /// <summary>Writes a <c>HKCU</c> <c>Run</c> value.</summary>
    void SetRunEntry(string name, string command);

    /// <summary>Deletes a <c>HKCU</c> <c>Run</c> value; absent is fine.</summary>
    void DeleteRunEntry(string name);

    /// <summary>The <c>StartupApproved\Run</c> value of <c>HKCU</c> or <c>HKLM</c> when it is <c>REG_BINARY</c>; null
    /// otherwise, which Electron treats as enabled.</summary>
    byte[]? GetStartupApproved(bool machine, string name);

    /// <summary>Writes a <c>HKCU</c> <c>StartupApproved\Run</c> value.</summary>
    void SetStartupApproved(string name, byte[] data);

    /// <summary>Deletes a <c>HKCU</c> <c>StartupApproved\Run</c> value; absent is fine.</summary>
    void DeleteStartupApproved(string name);
}

/// <summary>A <c>Run</c> entry as the login-item query reports it; <see cref="Scope"/> is <c>user</c> or <c>machine</c>.</summary>
internal readonly record struct RunItem(string Name, string Path, IReadOnlyList<string> Args, string Scope, bool Enabled);

/// <summary>Windows protocol-handler registration and login items, as Electron's <c>browser_win.cc</c> does them.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsAppRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const uint AssocfIsProtocol = 0x1000;

    /// <summary><c>ASSOCSTR_EXECUTABLE</c>.</summary>
    public const int AssocstrExecutable = 2;

    /// <summary><c>ASSOCSTR_FRIENDLYAPPNAME</c>.</summary>
    public const int AssocstrFriendlyAppName = 4;
    private const int ReadValueChars = 1024;
    private const int ApprovedBufferBytes = 24;

    private static readonly IAppRegistryBackend s_backend = new RegistryBackend();

    /// <summary>Electron's <c>GetAppUserModelID</c>: the process's explicit AppUserModelID, which is set to
    /// <c>electron.app.</c> + the executable's ProductName on first use when the process has none.</summary>
    public static string AppUserModelId()
    {
        if (WindowsJumpList.ExplicitAppUserModelId() is { } id)
        {
            return id;
        }

        string generated = "electron.app." + ProductName();
        fixed (char* p = generated)
        {
            SetCurrentProcessExplicitAppUserModelID(p);
        }

        return generated;
    }

    /// <summary>Registers the app as the handler of <paramref name="protocol"/> for the current user.</summary>
    /// <param name="protocol">The scheme.</param>
    /// <param name="path">The executable; null is the running process, anything else (even empty) is used as given.</param>
    /// <param name="args">Arguments placed before the URL.</param>
    /// <returns>False for an invalid scheme, no executable, or when the registry refuses.</returns>
    public static bool SetAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => SetAsDefaultProtocolClient(s_backend, protocol, path, args);

    /// <summary>Removes the registration if its command is this app's.</summary>
    /// <returns>True also when nothing is registered or another app is; false for an invalid scheme or when the
    /// registry refuses the delete.</returns>
    public static bool RemoveAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => RemoveAsDefaultProtocolClient(s_backend, protocol, path, args);

    /// <summary>Whether the registered handler command is exactly this app's (case-sensitive).</summary>
    public static bool IsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => IsDefaultProtocolClient(s_backend, protocol, path, args);

    /// <summary>What <c>AssocQueryString</c> reports for <paramref name="url"/>'s scheme (<paramref name="assocStr"/> is an
    /// <c>ASSOCSTR</c>), or an empty string when the scheme is not a URL protocol or the query fails.</summary>
    public static string GetAppInfoForProtocol(string url, int assocStr)
        => GetAppInfoForProtocol(s_backend, url, assocStr);

    /// <summary>Electron's <c>Browser::SetLoginItemSettings</c>: writes or deletes the <c>Run</c> value
    /// <paramref name="name"/> and its <c>StartupApproved</c> marker. Failures are silent, as in Electron.</summary>
    public static void SetLoginItem(string name, bool openAtLogin, bool enabled, string? path, IReadOnlyList<string>? args)
        => SetLoginItem(s_backend, name, openAtLogin, enabled, path, args);

    /// <summary>Electron's <c>Browser::GetLoginItemSettings</c>.</summary>
    /// <returns>Whether the <c>Run</c> value named by the AppUserModelID is the command for <paramref name="path"/> and
    /// <paramref name="args"/>; whether any listed entry is enabled; and every <c>HKCU</c> and <c>HKLM</c> <c>Run</c>
    /// entry whose program is the executable.</returns>
    public static (bool OpenAtLogin, bool WillLaunch, IReadOnlyList<(string Name, string Path, IReadOnlyList<string> Args, string Scope, bool Enabled)> Items)
        GetLoginItems(string? path, IReadOnlyList<string>? args)
    {
        var (open, will, items) = GetLoginItems(s_backend, AppUserModelId(), path, args);
        return (open, will, items.Select(i => (i.Name, i.Path, i.Args, i.Scope, i.Enabled)).ToList());
    }

    internal static bool SetAsDefaultProtocolClient(IAppRegistryBackend backend, string protocol, string? path, IReadOnlyList<string>? args)
    {
        if (!IsValidProtocolScheme(protocol) || ProtocolLaunchPath(path, args) is not { } command)
        {
            return false;
        }

        try
        {
            return backend.WriteProtocol(protocol, command);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    internal static bool RemoveAsDefaultProtocolClient(IAppRegistryBackend backend, string protocol, string? path, IReadOnlyList<string>? args)
    {
        if (!IsValidProtocolScheme(protocol))
        {
            return false;
        }

        try
        {
            // Nothing registered counts as removed.
            if (backend.GetProtocolCommand(protocol) is not { } registered)
            {
                return true;
            }

            if (ProtocolLaunchPath(path, args) is not { } command)
            {
                return false;
            }

            if (registered != command)
            {
                return true;
            }

            if (!backend.DeleteProtocolShell(protocol))
            {
                return false;
            }

            Try(() => backend.CleanUpProtocol(protocol));
            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    internal static bool IsDefaultProtocolClient(IAppRegistryBackend backend, string protocol, string? path, IReadOnlyList<string>? args)
    {
        if (!IsValidProtocolScheme(protocol) || ProtocolLaunchPath(path, args) is not { } command)
        {
            return false;
        }

        try
        {
            return backend.GetProtocolCommand(protocol) == command;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    // Electron's GetAppInfoHelperForProtocol: only schemes Windows treats as URL protocols, so a ProgID is never one.
    internal static string GetAppInfoForProtocol(IAppRegistryBackend backend, string url, int assocStr)
    {
        string scheme = SchemeOf(url);
        try
        {
            return scheme.Length > 0 && backend.HasUrlProtocolValue(scheme) ? backend.AssocQuery(scheme, assocStr) ?? "" : "";
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return "";
        }
    }

    internal static void SetLoginItem(IAppRegistryBackend backend, string name, bool openAtLogin, bool enabled, string? path, IReadOnlyList<string>? args)
    {
        if (openAtLogin)
        {
            if (FormatCommandLine(path, args) is not { } command)
            {
                return;
            }

            Try(() => backend.SetRunEntry(name, command));
            Try(enabled ? () => backend.DeleteStartupApproved(name) : () => backend.SetStartupApproved(name, DisabledMarker()));
        }
        else
        {
            Try(() => backend.DeleteStartupApproved(name));
            Try(() => backend.DeleteRunEntry(name));
        }
    }

    internal static (bool OpenAtLogin, bool WillLaunch, IReadOnlyList<RunItem> Items) GetLoginItems(
        IAppRegistryBackend backend, string appId, string? path, IReadOnlyList<string>? args)
    {
        bool open = false;
        try
        {
            if (backend.ReadRunValue(appId) is { } value && FormatCommandLine(path, args) is { } command)
            {
                open = value == command;
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
        }

        // An unquoted path with spaces is cut at the first space here, as CommandLine::FromString does in Electron.
        string lookup = ParseCommandLine(string.IsNullOrEmpty(path) ? Environment.ProcessPath ?? "" : path).Program;
        List<RunItem> items = [];
        if (lookup.Length > 0)
        {
            AddLaunchItems(backend, lookup, machine: false, items);
            AddLaunchItems(backend, lookup, machine: true, items);
        }

        return (open, items.Any(i => i.Enabled), items);
    }

    private static void AddLaunchItems(IAppRegistryBackend backend, string lookup, bool machine, List<RunItem> items)
    {
        try
        {
            foreach (var (name, value) in backend.EnumerateRun(machine))
            {
                var (program, args) = ParseCommandLine(value);
                if (string.Equals(lookup, program, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new RunItem(name, program, args, machine ? "machine" : "user", IsApproved(backend.GetStartupApproved(machine, name))));
                }
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
        }
    }

    // RFC 3986 section 3.1, as Electron's Browser::IsValidProtocolScheme.
    internal static bool IsValidProtocolScheme(string scheme)
    {
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The scheme <c>GURL</c> gives <paramref name="url"/>, lower-cased; empty when it has none.</summary>
    internal static string SchemeOf(string url)
    {
        string trimmed = url.Trim(TrimmedByGurl);
        int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return "";
        }

        // GURL reads "c:\x" and "c:/x" as Windows paths, i.e. file URLs.
        if (colon == 1 && char.IsAsciiLetter(trimmed[0]) && (trimmed.Length == 2 || trimmed[2] is '/' or '\\'))
        {
            return "file";
        }

        string scheme = trimmed[..colon];
        return IsValidProtocolScheme(scheme) ? scheme.ToLowerInvariant() : "";
    }

    private static readonly char[] TrimmedByGurl = [.. Enumerable.Range(0, 0x21).Select(i => (char)i)];

    // Electron's GetProtocolLaunchPath: a given path, even empty, is used as is.
    internal static string? ProtocolLaunchPath(string? path, IReadOnlyList<string>? args)
    {
        string? exe = path ?? Environment.ProcessPath;
        return exe is null ? null : QuotedCommand(exe, args) + " \"%1\"";
    }

    // Electron's FormatCommandLineString: an empty path is the running process.
    internal static string? FormatCommandLine(string? path, IReadOnlyList<string>? args)
    {
        string? exe = string.IsNullOrEmpty(path) ? Environment.ProcessPath : path;
        return string.IsNullOrEmpty(exe) ? null : QuotedCommand(exe, args);
    }

    private static string QuotedCommand(string exe, IReadOnlyList<string>? args)
    {
        if (exe.Length >= 2 && exe[0] == '"' && exe[^1] == '"')
        {
            exe = exe[1..^1];
        }

        StringBuilder sb = new(AddQuoteForArg(exe));
        foreach (string arg in args ?? [])
        {
            sb.Append(' ').Append(AddQuoteForArg(arg));
        }

        return sb.ToString();
    }

    /// <summary>Electron's <c>AddQuoteForArg</c>: quotes only an argument holding a space, backslash or quote; inside
    /// quotes, backslashes before a quote or the end are doubled and quotes escaped.</summary>
    internal static string AddQuoteForArg(string arg)
    {
        if (arg.AsSpan().IndexOfAny(" \\\"") < 0)
        {
            return arg;
        }

        StringBuilder sb = new("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            if (arg[i] == '\\')
            {
                int end = i + 1;
                while (end < arg.Length && arg[end] == '\\')
                {
                    end++;
                }

                int count = end - i;
                sb.Append('\\', end == arg.Length || arg[end] == '"' ? count * 2 : count);
                i = end - 1;
            }
            else if (arg[i] == '"')
            {
                sb.Append("\\\"");
            }
            else
            {
                sb.Append(arg[i]);
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary><c>base::CommandLine::FromString</c>'s program and <c>GetArgs()</c>: switches (<c>--x</c>, <c>-x</c>,
    /// <c>/x</c>) before a <c>--</c> are not arguments, and the first <c>--</c> is dropped.</summary>
    internal static (string Program, IReadOnlyList<string> Args) ParseCommandLine(string commandLine)
    {
        string raw = commandLine.Trim();
        if (raw.Length == 0)
        {
            return ("", []);
        }

        string[] argv = CommandLineToArgv(raw);
        string program = argv.Length > 0 ? argv[0].Trim() : "";
        List<string> args = [];
        bool parseSwitches = true;
        foreach (string item in argv.Skip(1))
        {
            string arg = item.Trim();
            parseSwitches &= arg != "--";
            if (parseSwitches && SwitchPrefixLength(arg) is var prefix and > 0 && prefix < arg.Length)
            {
                if (IsSingleArgumentSwitch(arg, prefix))
                {
                    return (program, SingleArgument(raw, program, arg));
                }

                continue;
            }

            args.Add(arg);
        }

        args.Remove("--");
        return (program, args);
    }

    private static int SwitchPrefixLength(string arg)
        => arg.StartsWith("--", StringComparison.Ordinal) ? 2 : arg.StartsWith('-') || arg.StartsWith('/') ? 1 : 0;

    private static bool IsSingleArgumentSwitch(string arg, int prefix)
    {
        int equals = arg.IndexOf('=', StringComparison.Ordinal);
        return arg.AsSpan(prefix, (equals < 0 ? arg.Length : equals) - prefix).SequenceEqual("single-argument");
    }

    // CommandLine::ParseAsSingleArgument: everything one character past the switch in the raw string is the only argument.
    private static List<string> SingleArgument(string raw, string program, string switchString)
    {
        int equals = switchString.IndexOf('=', StringComparison.Ordinal);
        string key = equals < 0 ? switchString : switchString[..equals];
        int start = raw.AsSpan().IndexOfAnyExcept(' ');
        int afterProgram = start + program.Length + (raw[start] == '"' ? 2 : 0);
        if (afterProgram >= raw.Length)
        {
            return [];
        }

        int at = raw.IndexOf(key, afterProgram, StringComparison.Ordinal);
        int arg = at < 0 ? -1 : at + key.Length + 1;
        return arg < 0 || arg >= raw.Length ? [] : [raw[arg..]];
    }

    private static string[] CommandLineToArgv(string commandLine)
    {
        int count;
        char** argv;
        fixed (char* p = commandLine)
        {
            argv = CommandLineToArgvW(p, &count);
        }

        if (argv is null)
        {
            return [];
        }

        try
        {
            string[] result = new string[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = new string(argv[i]);
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    internal static byte[] DisabledMarker() => [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    // Electron compares the bytes as C strings against "" and "\x02": a first byte of 0, or 2 followed by 0, is enabled.
    internal static bool IsApproved(byte[]? data)
    {
        if (data is null || data.Length > ApprovedBufferBytes)
        {
            return true;
        }

        int end = Array.IndexOf(data, (byte)0);
        ReadOnlySpan<byte> text = data.AsSpan(0, end < 0 ? data.Length : end);
        return text.IsEmpty || text.SequenceEqual((ReadOnlySpan<byte>)[0x02]);
    }

    private static string ProductName()
        => Environment.ProcessPath is { } exe ? FileVersionInfo.GetVersionInfo(exe).ProductName ?? "" : "";

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
        }
    }

    private static bool IsRegistryFailure(Exception ex)
        => ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException or InvalidOperationException;

    // base::win::RegKey::ReadValue: REG_SZ, or REG_EXPAND_SZ expanded, within a 1024-character buffer.
    private static string? ReadValue(string subKey, string name)
    {
        try
        {
            // Electron opens these keys with KEY_ALL_ACCESS, so a key it may not write is one it cannot read.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)
                || key.GetValueKind(name) is not (RegistryValueKind.String or RegistryValueKind.ExpandString)
                || key.GetValue(name) is not string value)
            {
                return null;
            }

            return value.Length < ReadValueChars ? value : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [DllImport("shlwapi.dll", ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, int str, char* assoc, char* extra, char* output, uint* length);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(char* appId);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern char** CommandLineToArgvW(char* commandLine, int* count);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void* LocalFree(void* memory);

    [SupportedOSPlatform("windows")]
    private sealed class RegistryBackend : IAppRegistryBackend
    {
        public string? GetProtocolCommand(string protocol) => ReadValue(@$"Software\Classes\{protocol}\shell\open\command", "");

        public bool WriteProtocol(string protocol, string command)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{protocol}");
            key.SetValue("URL Protocol", "");
            key.SetValue("", "URL:" + protocol);
            using RegistryKey cmd = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{protocol}\shell\open\command");
            cmd.SetValue("", command);
            return true;
        }

        public bool DeleteProtocolShell(string protocol)
        {
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            if (classes is null)
            {
                return false;
            }

            classes.DeleteSubKeyTree(protocol + @"\shell", throwOnMissingSubKey: true);
            return true;
        }

        public void CleanUpProtocol(string protocol)
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@$"Software\Classes\{protocol}", writable: true))
            {
                if (key is null)
                {
                    return;
                }

                key.DeleteValue("URL Protocol", throwOnMissingValue: false);
                key.SetValue("", "");
                key.DeleteValue("", throwOnMissingValue: false);
                if (key.ValueCount != 0)
                {
                    return;
                }
            }

            // Non-recursive: a key that still has subkeys stays.
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKey(protocol, throwOnMissingSubKey: false);
        }

        public bool HasUrlProtocolValue(string scheme)
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key is not null && key.GetValueNames().Contains("URL Protocol", StringComparer.OrdinalIgnoreCase);
        }

        public string? AssocQuery(string scheme, int assocStr)
        {
            char* buffer = stackalloc char[ReadValueChars];
            uint length = ReadValueChars;
            fixed (char* assoc = scheme)
            {
                return AssocQueryStringW(AssocfIsProtocol, assocStr, assoc, null, buffer, &length) < 0 ? null : new string(buffer);
            }
        }

        public string? ReadRunValue(string name) => ReadValue(RunKeyPath, name);

        public IReadOnlyList<(string Name, string Value)> EnumerateRun(bool machine)
        {
            using RegistryKey? key = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(RunKeyPath);
            if (key is null)
            {
                return [];
            }

            List<(string, string)> values = [];
            foreach (string name in key.GetValueNames().Reverse())
            {
                values.Add((name, AsCString(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames))));
            }

            return values;
        }

        // RegistryValueIterator::Value() is the raw data as a wchar_t string, whatever the value's type.
        private static string AsCString(object? data)
        {
            string text = data switch
            {
                string s => s,
                string[] m => m.Length > 0 ? m[0] : "",
                byte[] b => Encoding.Unicode.GetString(b, 0, b.Length & ~1),
                int i => Encoding.Unicode.GetString(BitConverter.GetBytes(i)),
                long l => Encoding.Unicode.GetString(BitConverter.GetBytes(l)),
                _ => "",
            };
            int nul = text.IndexOf('\0', StringComparison.Ordinal);
            return nul < 0 ? text : text[..nul];
        }

        public void SetRunEntry(string name, string command)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(name, command, RegistryValueKind.String);
        }

        public void DeleteRunEntry(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }

        public byte[]? GetStartupApproved(bool machine, string name)
        {
            using RegistryKey? key = (machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(ApprovedKeyPath);
            return key?.GetValue(name) is byte[] data && key.GetValueKind(name) == RegistryValueKind.Binary ? data : null;
        }

        public void SetStartupApproved(string name, byte[] data)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(ApprovedKeyPath);
            key.SetValue(name, data, RegistryValueKind.Binary);
        }

        public void DeleteStartupApproved(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
