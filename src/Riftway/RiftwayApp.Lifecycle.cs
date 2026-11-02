using System.Diagnostics;
using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>What <see cref="RiftwayApp.Relaunch"/> starts; Electron's <c>app.relaunch()</c> options.</summary>
public sealed record RelaunchOptions
{
    /// <summary>The new instance's arguments, in place of this one's. Electron's <c>args</c>.</summary>
    public IReadOnlyList<string>? Args { get; init; }

    /// <summary>The executable to start in place of this one. Electron's <c>execPath</c>.</summary>
    public string? ExecPath { get; init; }
}

/// <summary>A page's renderer process ended; Electron's <c>app</c> <c>render-process-gone</c> arguments.</summary>
/// <param name="WebContents">The page whose renderer ended.</param>
/// <param name="Details">Why it ended.</param>
public sealed record AppRenderProcessGoneEventArgs(WebContents WebContents, RenderProcessGoneEventArgs Details);

public sealed partial class RiftwayApp
{
    private readonly TaskCompletionSource _whenReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<IReadOnlyList<string>> _relaunches = [];
    private bool _hardwareAccelerationDisabled;

    /// <summary>The application is about to finish launching: on Windows and Linux just before <see cref="Ready"/>, on
    /// macOS from AppKit's <c>applicationWillFinishLaunching:</c>. Electron's <c>will-finish-launching</c>.</summary>
    /// <remarks>macOS: CEF raises <see cref="Ready"/> inside its initialisation, before AppKit finishes launching, so
    /// this event comes after it there (drift A23).</remarks>
    public event EventHandler? WillFinishLaunching;

    /// <summary>A page's renderer process ended unexpectedly; raised before the page's own
    /// <see cref="WebContents.RenderProcessGone"/>. Electron's <c>app</c> <c>render-process-gone</c>.</summary>
    public event EventHandler<AppRenderProcessGoneEventArgs>? RenderProcessGone;

    /// <summary>Completes once the application is ready, after the <see cref="Ready"/> handlers have run. Electron's
    /// <c>app.whenReady()</c>.</summary>
    public Task WhenReady() => _whenReady.Task;

    /// <summary>The application's directory: where the entry assembly was loaded from, as Electron answers the app's
    /// package directory. Electron's <c>app.getAppPath()</c>.</summary>
    public static string GetAppPath() => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>Whether the app runs as its own executable rather than under the <c>dotnet</c> host, which plays the
    /// part of Electron's development <c>electron</c> binary; <c>ELECTRON_FORCE_IS_PACKAGED</c> makes it true. Electron's
    /// <c>app.isPackaged</c>.</summary>
    public static bool IsPackaged => IsPackagedExecutable(Environment.ProcessPath, Environment.GetEnvironmentVariable("ELECTRON_FORCE_IS_PACKAGED") is not null);

    // Electron's App::IsPackaged: any executable but the development one, compared case-insensitively.
    internal static bool IsPackagedExecutable(string? exePath, bool forced)
    {
        if (forced) return true;
        var name = Path.GetFileName(exePath ?? "").ToLowerInvariant();
        return name != (OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    /// <summary>Starts a new instance once this one has exited (after <see cref="Run"/> returns); each call starts one.
    /// Does not quit: call <see cref="Quit"/> or <see cref="Exit"/>. Electron's <c>app.relaunch()</c>.</summary>
    /// <returns>Whether the relaunch was recorded.</returns>
    public bool Relaunch(RelaunchOptions? options = null)
    {
        var argv = RelaunchArgv(Environment.ProcessPath, Environment.GetCommandLineArgs(), options);
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0])) return false;
        lock (_relaunches) _relaunches.Add(argv);
        return true;
    }

    // Electron's App::Relaunch: the original argv, or with either option the executable (execPath, else this one)
    // followed by args alone. .NET's argv[0] is the entry assembly, which only the dotnet host needs back.
    internal static IReadOnlyList<string> RelaunchArgv(string? processPath, IReadOnlyList<string> commandLineArgs, RelaunchOptions? options)
    {
        if (options is not null && (options.ExecPath is not null || options.Args is not null))
            return [string.IsNullOrEmpty(options.ExecPath) ? processPath ?? "" : options.ExecPath, .. options.Args ?? []];
        var hosted = !IsPackagedExecutable(processPath, forced: false) && commandLineArgs.Count > 0;
        return [processPath ?? "", .. hosted ? commandLineArgs : commandLineArgs.Skip(1)];
    }

    private void StartRelaunches()
    {
        List<IReadOnlyList<string>> pending;
        lock (_relaunches)
        {
            pending = [.. _relaunches];
            _relaunches.Clear();
        }
        foreach (var argv in pending)
        {
            var start = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
            foreach (var arg in argv.Skip(1)) start.ArgumentList.Add(arg);
            try { Process.Start(start)?.Dispose(); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"[Riftway] Relaunch of {argv[0]} failed: {e.Message}");
            }
        }
    }

    /// <summary>Turns hardware acceleration off by starting Chromium with <c>--disable-gpu</c>. Electron's
    /// <c>app.disableHardwareAcceleration()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is ready.</exception>
    public void DisableHardwareAcceleration()
    {
        if (IsReady) throw new InvalidOperationException("app.disableHardwareAcceleration() can only be called before app is ready");
        if (!CommandLine.HasSwitch("disable-gpu")) CommandLine.AppendSwitch("disable-gpu");
        _hardwareAccelerationDisabled = true;
    }

    /// <summary>Whether hardware acceleration is on: before <see cref="Ready"/>, unless
    /// <see cref="DisableHardwareAcceleration"/> was called; after, Chromium's answer with Riftway's CEF patch 0013, which
    /// counts its fallback to software after GPU crashes, and with stock CEF unless Chromium was started with
    /// <c>--disable-gpu</c> (drift A24). Electron's <c>app.isHardwareAccelerationEnabled()</c>.</summary>
    public bool IsHardwareAccelerationEnabled()
    {
        if (!IsReady) return !_hardwareAccelerationDisabled;
        return CefPatchedApi.IsHardwareAccelerationEnabled() ?? !CommandLine.HasSwitch("disable-gpu");
    }

    /// <summary>The menu bar of every window (Windows, Linux) or the app (macOS). Electron's <c>app.applicationMenu</c>.</summary>
    public Menu? ApplicationMenu
    {
        get => Menu.GetApplicationMenu();
        set => Menu.SetApplicationMenu(value);
    }

    /// <summary><see cref="GetBadgeCount"/> and <see cref="SetBadgeCount"/>. Electron's <c>app.badgeCount</c>.</summary>
    public int BadgeCount
    {
        get => GetBadgeCount();
        set => SetBadgeCount(value);
    }

    /// <summary>Whether the process is an x64 build running on ARM64 under Rosetta (macOS) or emulation (Windows).
    /// Electron's <c>app.runningUnderARM64Translation</c>; false on Linux, where Electron has none.</summary>
    public static bool RunningUnderArm64Translation
    {
        get
        {
            if (OperatingSystem.IsMacOS()) return MacApp.RunningUnderTranslation();
            if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return WindowsNativeMachineIsArm64();
            return false;
        }
    }

    // base::win::OSInfo::IsRunningEmulatedOnArm64: IsWow64Process2's native machine is ARM64.
    private static unsafe bool WindowsNativeMachineIsArm64()
    {
        ushort process, native;
        try { return IsWow64Process2(-1, &process, &native) != 0 && native == 0xAA64; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern unsafe int IsWow64Process2(nint process, ushort* processMachine, ushort* nativeMachine);

    internal void RaiseRenderProcessGone(WebContents page, RenderProcessGoneEventArgs details)
        => RaiseApp(RenderProcessGone, new AppRenderProcessGoneEventArgs(page, details), "render-process-gone");

    private void RaiseWillFinishLaunching() => WillFinishLaunching?.Invoke(this, EventArgs.Empty);
}
