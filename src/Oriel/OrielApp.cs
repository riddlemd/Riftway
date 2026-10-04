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

    internal static OrielApp? CurrentOrNull => s_current;

    /// <summary>The application's name; also names its data directory.</summary>
    public string Name { get; }

    /// <summary>Whether Chromium's sandbox is on for child processes; see <see cref="EnableSandbox"/>.</summary>
    public bool IsSandboxed { get; private set; }

    /// <summary>Whether <see cref="Ready"/> has fired.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Chromium is initialised and windows can be created. Electron's <c>ready</c>.</summary>
    public event EventHandler? Ready;

    /// <summary>The app was launched again while running; the new launch has already exited. Electron's
    /// <c>second-instance</c>. With no handler the existing windows come to the front.</summary>
    public event EventHandler<SecondInstanceEventArgs>? SecondInstance;

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

        var schemes = Protocol.TakePrivilegedSchemes();
        var schemeSwitch = schemes.Count > 0 ? SchemeSwitch.Format(schemes) : null;
        var app = new AppHandler(
            new BrowserProcessHandler
            {
                ContextInitialized = OnContextInitialized,
                AlreadyRunningAppRelaunch = OnSecondInstance,
                // Every child (the Linux zygote included) must register the same schemes as this process.
                BeforeChildProcessLaunch = commandLine =>
                {
                    if (schemeSwitch is not null) AppendSwitch(commandLine, SchemeSwitch.Name, schemeSwitch);
                },
            },
            OnBeforeCommandLineProcessing,
            schemes);
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
        // Also false when another instance owns this data directory: CEF has already forwarded this
        // launch to it (SecondInstance there), and this process should just end.
        if (!initialized)
        {
            Console.Error.WriteLine($"[Oriel] Not started: another instance is using {dataDirectory}, or CEF failed to initialise (see its log).");
            return 1;
        }

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

    private static void OnBeforeCommandLineProcessing(string processType, cef_command_line_t* commandLine)
    {
        // Chromium keeps its cookie-encryption key in the Keychain, whose access follows the code
        // signature; ad-hoc dev builds change it every build and macOS prompts for a password.
        // TODO(M4): use the real Keychain for signed builds, as Electron's safeStorage does.
        if (OperatingSystem.IsMacOS() && processType.Length == 0) AppendSwitch(commandLine, "use-mock-keychain");
    }

    private static void AppendSwitch(cef_command_line_t* commandLine, string name)
    {
        cef_string_utf16_t value = default;
        CefString.Set(&value, name);
        commandLine->append_switch(commandLine, &value);
        CefString.Clear(&value);
    }

    private static void AppendSwitch(cef_command_line_t* commandLine, string name, string value)
    {
        cef_string_utf16_t key = default;
        cef_string_utf16_t text = default;
        CefString.Set(&key, name);
        CefString.Set(&text, value);
        commandLine->append_switch_with_value(commandLine, &key, &text);
        CefString.Clear(&key);
        CefString.Clear(&text);
    }

    private void OnSecondInstance(string commandLine, string workingDirectory)
    {
        if (SecondInstance is { } handler) handler(this, new SecondInstanceEventArgs(commandLine, workingDirectory));
        else BrowserWindow.FocusAll();
    }

    private void OnContextInitialized()
    {
        ForgetZoomLevels();
        AllowPopups();
        // Before Ready, so the first window's page can already load from a custom scheme.
        Protocol.OnContextInitialized();
        IsReady = true;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    // Electron has no popup blocker: window.open works without a user gesture, and WebContents.SetWindowOpenHandler decides.
    private static void AllowPopups()
    {
        var context = CefNative.cef_request_context_get_global_context();
        context->set_content_setting(context, null, null, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_POPUPS,
            cef_content_setting_values_t.CEF_CONTENT_SETTING_VALUE_ALLOW);
        NativeObject.Release(context);
    }

    // Chrome-style profiles save each origin's zoom across launches; Electron keeps zoom for the session only.
    private static void ForgetZoomLevels()
    {
        var context = CefNative.cef_request_context_get_global_context();
        var value = CefNative.cef_value_create();
        value->set_dictionary(value, CefNative.cef_dictionary_value_create());
        cef_string_utf16_t name = default, error = default;
        CefString.Set(&name, "partition.per_host_zoom_levels");
        var manager = (cef_preference_manager_t*)context;
        if (manager->set_preference(manager, &name, value, &error) == 0)
            Console.Error.WriteLine($"[Oriel] Could not reset saved zoom levels: {CefString.Read(&error)}");
        CefString.Clear(&name);
        CefString.Clear(&error);
        NativeObject.Release(context);
    }
}

/// <summary>A relaunch handed to the running instance; Electron's <c>second-instance</c> arguments.</summary>
/// <param name="CommandLine">The new launch's full command line.</param>
/// <param name="WorkingDirectory">The new launch's working directory.</param>
public sealed record SecondInstanceEventArgs(string CommandLine, string WorkingDirectory);
