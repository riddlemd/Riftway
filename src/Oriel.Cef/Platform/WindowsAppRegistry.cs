using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Oriel.Cef.Platform;

/// <summary>The registry reads and writes behind a Windows app's protocol handler and login item.</summary>
internal interface IAppRegistryBackend
{
    /// <summary>The <c>shell\open\command</c> default value of <c>HKCU\Software\Classes\&lt;protocol&gt;</c>.</summary>
    string? GetProtocolCommand(string protocol);

    /// <summary>Writes the protocol's class key: its description, <c>URL Protocol</c> marker and open command.</summary>
    void WriteProtocol(string protocol, string command);

    /// <summary>Deletes the protocol's whole class key.</summary>
    void DeleteProtocolTree(string protocol);

    /// <summary>The friendly name of the app registered for a URL scheme, or null.</summary>
    string? GetFriendlyAppName(string scheme);

    /// <summary>Every string value under <c>HKCU\...\Run</c>, by name.</summary>
    IReadOnlyDictionary<string, string> GetRunEntries();

    /// <summary>Writes a <c>Run</c> value.</summary>
    void SetRunEntry(string name, string command);

    /// <summary>Deletes a <c>Run</c> value; absent is fine.</summary>
    void DeleteRunEntry(string name);

    /// <summary>The <c>StartupApproved\Run</c> value for a name, or null.</summary>
    byte[]? GetStartupApproved(string name);

    /// <summary>Writes a <c>StartupApproved\Run</c> value.</summary>
    void SetStartupApproved(string name, byte[] data);

    /// <summary>Deletes a <c>StartupApproved\Run</c> value; absent is fine.</summary>
    void DeleteStartupApproved(string name);
}

/// <summary>A <c>Run</c> entry as the login-item query reports it.</summary>
internal readonly record struct RunItem(string Name, string Path, IReadOnlyList<string> Args, bool Enabled);

/// <summary>Windows protocol-handler registration and login items, as Electron's <c>browser_win.cc</c> does them.</summary>
/// <remarks>Per-user (<c>HKCU</c>) only; nothing here needs elevation.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsAppRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const uint AssocfIsProtocol = 0x1000;
    private const int AssocstrFriendlyAppName = 4;

    private static readonly IAppRegistryBackend s_backend = new RegistryBackend();

    /// <summary>Registers the app as the handler of <paramref name="protocol"/> for the current user.</summary>
    /// <returns>False for an empty or malformed protocol, or when the registry refuses.</returns>
    public static bool SetAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => SetAsDefaultProtocolClient(s_backend, protocol, path, args);

    /// <summary>Removes the registration if the registered command is this app's.</summary>
    /// <returns>False when the protocol is not ours or nothing could be removed.</returns>
    public static bool RemoveAsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => RemoveAsDefaultProtocolClient(s_backend, protocol, path, args);

    /// <summary>Whether the registered handler command is this app's.</summary>
    public static bool IsDefaultProtocolClient(string protocol, string? path = null, IReadOnlyList<string>? args = null)
        => IsDefaultProtocolClient(s_backend, protocol, path, args);

    /// <summary>The friendly name of the app that handles <paramref name="url"/>'s scheme, or an empty string.</summary>
    public static string GetApplicationNameForProtocol(string url)
        => GetApplicationNameForProtocol(s_backend, url);

    /// <summary>Adds or removes the app's <c>Run</c> entry and its <c>StartupApproved</c> state.</summary>
    /// <returns>False when the registry refuses.</returns>
    public static bool SetLoginItem(string name, bool openAtLogin, bool enabled, string? path, IReadOnlyList<string>? args)
        => SetLoginItem(s_backend, name, openAtLogin, enabled, path, args);

    /// <summary>Whether a matching <c>Run</c> entry exists and will launch, and every entry for the executable.</summary>
    public static (bool OpenAtLogin, bool WillLaunch, IReadOnlyList<(string Name, string Path, IReadOnlyList<string> Args, bool Enabled)> Items)
        GetLoginItems(string? path, IReadOnlyList<string>? args)
    {
        var (open, will, items) = GetLoginItems(s_backend, path, args);
        return (open, will, items.Select(i => (i.Name, i.Path, i.Args, i.Enabled)).ToList());
    }

    internal static bool SetAsDefaultProtocolClient(IAppRegistryBackend backend, string protocol, string? path, IReadOnlyList<string>? args)
    {
        string? exe = ResolveExe(path);
        if (!IsValidProtocol(protocol) || exe is null)
        {
            return false;
        }

        try
        {
            backend.WriteProtocol(protocol, BuildProtocolCommand(exe, args));
            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    internal static bool RemoveAsDefaultProtocolClient(IAppRegistryBackend backend, string protocol, string? path, IReadOnlyList<string>? args)
    {
        if (!IsDefaultProtocolClient(backend, protocol, path, args))
        {
            return false;
        }

        try
        {
            backend.DeleteProtocolTree(protocol);
            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    internal static bool IsDefaultProtocolClient(IAppRegistryBackend backend, string protocol, string? path, IReadOnlyList<string>? args)
    {
        string? exe = ResolveExe(path);
        if (!IsValidProtocol(protocol) || exe is null)
        {
            return false;
        }

        try
        {
            string? registered = backend.GetProtocolCommand(protocol);
            return registered is not null && CommandsEqual(registered, BuildProtocolCommand(exe, args));
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    internal static string GetApplicationNameForProtocol(IAppRegistryBackend backend, string url)
    {
        string scheme = SchemeOf(url);
        if (scheme.Length == 0)
        {
            return "";
        }

        try
        {
            return backend.GetFriendlyAppName(scheme) ?? "";
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return "";
        }
    }

    internal static bool SetLoginItem(IAppRegistryBackend backend, string name, bool openAtLogin, bool enabled, string? path, IReadOnlyList<string>? args)
    {
        string? exe = ResolveExe(path);
        if (string.IsNullOrEmpty(name) || (openAtLogin && exe is null))
        {
            return false;
        }

        try
        {
            if (openAtLogin)
            {
                backend.SetRunEntry(name, BuildCommand(exe!, args));
                backend.SetStartupApproved(name, ApprovedData(enabled));
            }
            else
            {
                backend.DeleteRunEntry(name);
                backend.DeleteStartupApproved(name);
            }

            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    internal static (bool OpenAtLogin, bool WillLaunch, IReadOnlyList<RunItem> Items) GetLoginItems(
        IAppRegistryBackend backend, string? path, IReadOnlyList<string>? args)
    {
        string? exe = ResolveExe(path);
        if (exe is null)
        {
            return (false, false, []);
        }

        string wanted = BuildCommand(exe, args);
        bool open = false;
        bool will = false;
        List<RunItem> items = [];
        try
        {
            foreach (var (name, command) in backend.GetRunEntries())
            {
                var (itemPath, itemArgs) = ParseCommand(command);
                if (!string.Equals(itemPath, exe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool enabled = IsApproved(backend.GetStartupApproved(name));
                items.Add(new RunItem(name, itemPath, itemArgs, enabled));
                if (CommandsEqual(command, wanted))
                {
                    open = true;
                    will |= enabled;
                }
            }
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return (false, false, []);
        }

        return (open, will, items);
    }

    internal static string? ResolveExe(string? path)
    {
        string? exe = string.IsNullOrEmpty(path) ? Environment.ProcessPath : path;
        return string.IsNullOrEmpty(exe) ? null : exe;
    }

    // A slash or backslash would walk out of HKCU\Software\Classes\<protocol>.
    internal static bool IsValidProtocol(string protocol)
        => !string.IsNullOrWhiteSpace(protocol) && protocol.AsSpan().IndexOfAny('\\', '/', '\0') < 0;

    internal static string SchemeOf(string url)
    {
        int colon = url.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? url : url[..colon];
    }

    internal static string BuildCommand(string path, IReadOnlyList<string>? args)
    {
        StringBuilder sb = new StringBuilder("\"").Append(path).Append('"');
        foreach (string arg in args ?? [])
        {
            sb.Append(' ').Append(QuoteArg(arg));
        }

        return sb.ToString();
    }

    internal static string BuildProtocolCommand(string path, IReadOnlyList<string>? args)
        => BuildCommand(path, args) + " \"%1\"";

    // The inverse of CommandLineToArgvW's backslash-and-quote rules, so ParseCommand round-trips it.
    internal static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            return arg;
        }

        StringBuilder sb = new("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (slashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', slashes);
            }

            slashes = 0;
            sb.Append(c);
        }

        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static (string Path, IReadOnlyList<string> Args) ParseCommand(string command)
    {
        int i = 0;
        while (i < command.Length && char.IsWhiteSpace(command[i]))
        {
            i++;
        }

        string path;
        if (i < command.Length && command[i] == '"')
        {
            int close = command.IndexOf('"', i + 1);
            if (close < 0)
            {
                close = command.Length;
            }

            path = command[(i + 1)..close];
            i = Math.Min(close + 1, command.Length);
        }
        else
        {
            int start = i;
            while (i < command.Length && !char.IsWhiteSpace(command[i]))
            {
                i++;
            }

            path = command[start..i];
        }

        return (path, SplitArgs(command, i));
    }

    private static List<string> SplitArgs(string s, int i)
    {
        List<string> args = [];
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length)
            {
                return args;
            }

            StringBuilder arg = new();
            bool quoted = false;
            while (i < s.Length && (quoted || !char.IsWhiteSpace(s[i])))
            {
                int slashes = 0;
                while (i < s.Length && s[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                if (i < s.Length && s[i] == '"')
                {
                    arg.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                    {
                        arg.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }

                    i++;
                }
                else
                {
                    arg.Append('\\', slashes);
                    if (i < s.Length)
                    {
                        arg.Append(s[i++]);
                    }
                }
            }

            args.Add(arg.ToString());
        }
    }

    internal static bool CommandsEqual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static byte[] ApprovedData(bool enabled)
    {
        byte[] data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        return data;
    }

    // Explorer writes an even first byte for enabled and an odd one for disabled; absent means enabled.
    internal static bool IsApproved(byte[]? data) => data is not { Length: > 0 } || (data[0] & 1) == 0;

    private static bool IsRegistryFailure(Exception ex)
        => ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException;

    [DllImport("shlwapi.dll", ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, int str, char* assoc, char* extra, char* output, uint* length);

    [SupportedOSPlatform("windows")]
    private sealed class RegistryBackend : IAppRegistryBackend
    {
        public string? GetProtocolCommand(string protocol)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@$"Software\Classes\{protocol}\shell\open\command");
            return key?.GetValue("") as string;
        }

        public void WriteProtocol(string protocol, string command)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@$"Software\Classes\{protocol}");
            key.SetValue("", "URL:" + protocol);
            key.SetValue("URL Protocol", "");
            using RegistryKey cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", command);
        }

        public void DeleteProtocolTree(string protocol)
            => Registry.CurrentUser.DeleteSubKeyTree(@$"Software\Classes\{protocol}", throwOnMissingSubKey: false);

        public string? GetFriendlyAppName(string scheme)
        {
            const int bufferChars = 1024;
            char* buffer = stackalloc char[bufferChars];
            uint length = bufferChars;
            fixed (char* assoc = scheme)
            fixed (char* verb = "open")
            {
                int hr = AssocQueryStringW(AssocfIsProtocol, AssocstrFriendlyAppName, assoc, verb, buffer, &length);
                return hr == 0 ? new string(buffer) : null;
            }
        }

        public IReadOnlyDictionary<string, string> GetRunEntries()
        {
            Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key is null)
            {
                return entries;
            }

            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string command)
                {
                    entries[name] = command;
                }
            }

            return entries;
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

        public byte[]? GetStartupApproved(string name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            return key?.GetValue(name) as byte[];
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
