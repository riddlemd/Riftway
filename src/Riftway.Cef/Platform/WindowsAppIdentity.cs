using System.Diagnostics;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The process's identity to the Windows shell: the AppUserModelID and the toast activator CLSID, kept
/// process-wide as Electron's <c>application_info_win.cc</c> keeps them.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAppIdentity
{
    private static readonly Lock s_lock = new();
    private static string? s_appUserModelId;
    private static string? s_toastActivatorClsid;
    private static string? s_productName;

    /// <summary>The executable's VERSIONINFO product name, "" when it has none; Electron's <c>GetApplicationName()</c>
    /// on Windows.</summary>
    public static string ProductName => s_productName ??= Environment.ProcessPath is { } exe
        ? FileVersionInfo.GetVersionInfo(exe).ProductName ?? ""
        : "";

    /// <summary>The AppUserModelID, set on first use: the process's explicit one if it has one, else
    /// <c>electron.app.{ProductName}</c>, which then becomes the process's. Electron's <c>GetRawAppUserModelID()</c>.</summary>
    public static string AppUserModelId
    {
        get
        {
            lock (s_lock)
                return s_appUserModelId ??= Resolve(WindowsToast.GetProcessAppUserModelId, id => WindowsToast.SetProcessAppUserModelId(id), ProductName);
        }
    }

    /// <summary>Sets the process's explicit AppUserModelID; Electron's <c>app.setAppUserModelId()</c>.</summary>
    public static void SetAppUserModelId(string id)
    {
        lock (s_lock)
        {
            s_appUserModelId = id;
            WindowsToast.SetProcessAppUserModelId(id);
        }
    }

    /// <summary>The first-use rule: an explicit id already set wins, else the generated one is set and returned.</summary>
    internal static string Resolve(Func<string?> getProcessId, Action<string> setProcessId, string productName)
    {
        if (getProcessId() is { } current) return current;
        var generated = "electron.app." + productName;
        setProcessId(generated);
        return generated;
    }

    /// <summary>The CLSID of the toast activator COM class, in registry form (<c>{XXXXXXXX-...}</c>); a new random one on
    /// first use unless set. Electron's <c>app.toastActivatorCLSID</c>.</summary>
    public static string ToastActivatorClsid
    {
        get
        {
            lock (s_lock) return s_toastActivatorClsid ??= Guid.NewGuid().ToString("B").ToUpperInvariant();
        }
    }

    /// <summary>Sets <see cref="ToastActivatorClsid"/>, accepting it with or without braces; false (and unchanged) when it is
    /// not a GUID. Electron's <c>SetAppToastActivatorCLSID()</c>.</summary>
    public static bool SetToastActivatorClsid(string clsid)
    {
        if (NormalizeClsid(clsid) is not { } normalized) return false;
        lock (s_lock) s_toastActivatorClsid = normalized;
        return true;
    }

    /// <summary><paramref name="clsid"/> in <c>StringFromGUID2</c>'s form, or null if neither it nor it in braces parses as
    /// <c>CLSIDFromString</c> does.</summary>
    internal static string? NormalizeClsid(string clsid)
    {
        if (Guid.TryParseExact(clsid, "B", out var guid)
            || (clsid.Length > 0 && clsid[0] != '{' && Guid.TryParseExact("{" + clsid + "}", "B", out guid)))
            return guid.ToString("B").ToUpperInvariant();
        return null;
    }
}
