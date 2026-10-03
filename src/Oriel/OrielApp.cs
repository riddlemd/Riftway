using System.Reflection;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The application: Electron's <c>app</c>. One per process.</summary>
public sealed unsafe class OrielApp
{
    private static OrielApp? s_current;

    private OrielApp(string name) => Name = name;

    /// <summary>The application created by <see cref="Create"/>.</summary>
    /// <exception cref="InvalidOperationException">No application has been created.</exception>
    public static OrielApp Current => s_current ?? throw new InvalidOperationException("Call OrielApp.Create() first.");

    /// <summary>The application's name; also names its data directory.</summary>
    public string Name { get; }

    /// <summary>Whether Chromium's sandbox is on for child processes; see <see cref="EnableSandbox"/>.</summary>
    public bool IsSandboxed { get; private set; }

    /// <summary>Whether <see cref="Ready"/> has fired.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Chromium is initialised and windows can be created. Electron's <c>ready</c>.</summary>
    public event EventHandler? Ready;

    /// <summary>The last window closed. With no handler the application quits. Electron's <c>window-all-closed</c>.</summary>
    public event EventHandler? WindowAllClosed;

    /// <summary>Creates the process's application.</summary>
    /// <param name="name">Defaults to the entry assembly's name.</param>
    /// <exception cref="InvalidOperationException">An application already exists.</exception>
    public static OrielApp Create(string? name = null)
    {
        if (s_current is not null) throw new InvalidOperationException("An OrielApp already exists in this process.");
        return s_current = new OrielApp(name ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel");
    }

    /// <summary>Turns on Chromium's sandbox for every child process. Electron's <c>app.enableSandbox()</c>.</summary>
    /// <remarks>Call before <see cref="Run"/>. Linux only so far: macOS and Windows link CEF's sandbox library into
    /// the subprocess exe, which their packaging does not do yet.</remarks>
    /// <exception cref="InvalidOperationException">The application is already running.</exception>
    public void EnableSandbox()
    {
        if (IsReady) throw new InvalidOperationException("Enable the sandbox before OrielApp.Run().");
        IsSandboxed = true;
    }

    /// <summary>Runs the application on the calling thread, which must be the process's main thread,
    /// until <see cref="Quit"/>. Returns the process exit code.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet implemented on this OS.</exception>
    public int Run()
    {
        if (IsSandboxed && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The sandbox is only wired up on Linux so far.");

        CefRuntime.Load(CefLayout.MainProcessLibrary());
        if (OperatingSystem.IsMacOS()) MacApplication.Install();

        var app = new AppHandler(new BrowserProcessHandler { ContextInitialized = OnContextInitialized });
        var args = CefLayout.MainArgs();

        NativeObject.AddRef(app.Native);
        var exitCode = CefNative.cef_execute_process((cef_main_args_t*)&args, app.Native, null);
        if (exitCode >= 0) return exitCode;

        // Without Create, Linux answers "" when ~/.config does not exist yet, and CEF rejects the relative path.
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), Name);
        var settings = new cef_settings_t
        {
            size = (nuint)sizeof(cef_settings_t),
            no_sandbox = IsSandboxed ? 0 : 1,
            log_severity = cef_log_severity_t.LOGSEVERITY_WARNING,
        };
        CefString.Set(&settings.root_cache_path, dataDirectory);
        CefString.Set(&settings.cache_path, dataDirectory);
        CefString.Set(&settings.browser_subprocess_path, CefLayout.SubprocessPath());

        NativeObject.AddRef(app.Native);
        var initialized = CefNative.cef_initialize((cef_main_args_t*)&args, &settings, app.Native, null) != 0;
        CefString.Clear(&settings.root_cache_path);
        CefString.Clear(&settings.cache_path);
        CefString.Clear(&settings.browser_subprocess_path);
        if (!initialized) throw new InvalidOperationException("CEF failed to initialise; see its log output.");

        CefNative.cef_run_message_loop();
        CefNative.cef_shutdown();
        return 0;
    }

    /// <summary>Ends <see cref="Run"/> once open windows have closed. Electron's <c>app.quit()</c>.</summary>
    public void Quit() => CefNative.cef_quit_message_loop();

    internal void OnAllWindowsClosed()
    {
        if (WindowAllClosed is { } handler) handler(this, EventArgs.Empty);
        else Quit();
    }

    private void OnContextInitialized()
    {
        IsReady = true;
        Ready?.Invoke(this, EventArgs.Empty);
    }
}
