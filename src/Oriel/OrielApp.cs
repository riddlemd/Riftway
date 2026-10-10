using System.ComponentModel;
using System.Reflection;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The application: Electron's <c>app</c>. One per process.</summary>
public sealed unsafe partial class OrielApp
{
    private static OrielApp? s_current;

    private OrielApp(string name) => _name = name;

    /// <summary>The application created by <see cref="Create"/>.</summary>
    /// <exception cref="InvalidOperationException">No application has been created.</exception>
    public static OrielApp Current => s_current ?? throw new InvalidOperationException("Call OrielApp.Create() first.");

    internal static OrielApp? CurrentOrNull => s_current;

    /// <summary>CEF's root cache path: the default session's data, and the parent of every persistent session's.</summary>
    internal static string? DataDirectory { get; private set; }

    /// <summary>The user agent of pages that set none, as Electron's: Chromium's, with <c>Name/version</c> before the Chrome
    /// token and <c>Oriel/version</c> after it. Set before <see cref="Run"/> for every page, or later for pages created
    /// after. Electron's <c>app.userAgentFallback</c>.</summary>
    public string UserAgentFallback
    {
        get => _userAgentFallback ?? DefaultUserAgent();
        set => _userAgentFallback = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string? _userAgentFallback;
    private (string Name, string UserAgent)? _defaultUserAgent;

    // Electron's GetApplicationUserAgent, which a new name invalidates.
    private string DefaultUserAgent()
    {
        var name = Name;
        if (_defaultUserAgent is { } cached && cached.Name == name) return cached.UserAgent;
        var userAgent = BuildUserAgent(name, AppVersion(), ChromeVersion(), OrielVersion());
        _defaultUserAgent = (name, userAgent);
        return userAgent;
    }

    /// <summary>The user agent CEF was started with: every page's unless overridden.</summary>
    internal static string? ProcessUserAgent { get; private set; }

    /// <summary>Electron's format (GetApplicationUserAgent), on Chromium's reduced, per-OS platform string.</summary>
    internal static string BuildUserAgent(string name, string version, string chromeVersion, string orielVersion)
    {
        var platform = OperatingSystem.IsMacOS() ? "Macintosh; Intel Mac OS X 10_15_7"
            : OperatingSystem.IsWindows() ? "Windows NT 10.0; Win64; x64"
            : "X11; Linux x86_64";
        return $"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) {name.Replace(" ", "")}/{version} " +
            $"Chrome/{chromeVersion} Oriel/{orielVersion} Safari/537.36";
    }

    /// <summary>The app's version: its project's <c>Version</c>, as Electron reads package.json's. Electron's <c>app.getVersion()</c>.</summary>
    public string Version => AppVersion();

    private static string AppVersion() => VersionOf(Assembly.GetEntryAssembly());

    // The informational version keeps a prerelease tag; the build adds "+commit" metadata that package.json would not have.
    internal static string VersionOf(Assembly? assembly)
    {
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational)) return informational.Split('+')[0];
        return ThreePart(assembly?.GetName().Version);
    }

    internal static string OrielVersion() => ThreePart(typeof(OrielApp).Assembly.GetName().Version);

    private static string ThreePart(Version? version) => version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    internal static string ChromeVersion()
    {
        CefRuntime.Load(CefLayout.MainProcessLibrary());
        return string.Join('.', Enumerable.Range(4, 4).Select(CefNative.cef_version_info));
    }

    /// <summary>The application's name: <see cref="Create"/>'s, until set. It names <c>userData</c> while that path has
    /// not been read yet, and the default <see cref="UserAgentFallback"/>. Electron's <c>app.name</c>,
    /// <c>getName()</c> and <c>setName()</c>.</summary>
    public string Name
    {
        get => _name;
        set => _name = value ?? throw new ArgumentNullException(nameof(value));
    }

    private string _name;

    /// <summary>Whether Chromium's sandbox is on for child processes. Before <see cref="Run"/>, whether
    /// <see cref="EnableSandbox"/> was called; from <see cref="Run"/> on, the outcome: always on in the Windows bootstrap
    /// layout (as Electron's default), unless the app was launched with <c>--no-sandbox</c>.</summary>
    public bool IsSandboxed { get; private set; }

    private bool _sandboxRequested;

    /// <summary>Whether <see cref="Ready"/> has fired.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Chromium is initialised and windows can be created. Electron's <c>ready</c>.</summary>
    public event EventHandler? Ready;

    /// <summary>The app was launched again while running; the new launch has already exited. Electron's
    /// <c>second-instance</c>. With no handler the existing windows come to the front.</summary>
    public event EventHandler<SecondInstanceEventArgs>? SecondInstance;

    /// <summary>The application is about to close its windows to quit; cancel to keep running. Electron's <c>before-quit</c>.</summary>
    public event EventHandler<CancelEventArgs>? BeforeQuit;

    /// <summary>Every window has closed for a quit; cancel to keep running. Electron's <c>will-quit</c>.</summary>
    public event EventHandler<CancelEventArgs>? WillQuit;

    /// <summary>The application is quitting, by <see cref="Quit"/> or <see cref="Exit"/>; raised on the main thread once the
    /// message loop has ended. Electron's <c>quit</c> event (named for the method it would collide with).</summary>
    public event EventHandler<ExitedEventArgs>? Exited;

    /// <summary>The last window closed. With no handler the application quits. Not raised while quitting.
    /// Electron's <c>window-all-closed</c>.</summary>
    public event EventHandler? WindowAllClosed;

    /// <summary>A page's navigation hit an invalid certificate, after the page's own
    /// <see cref="WebContents.CertificateError"/>. Electron's <c>app</c> <c>certificate-error</c>.</summary>
    public event EventHandler<CertificateErrorEventArgs>? CertificateError;

    /// <summary>A server or proxy asks a page for credentials, after the page's own <see cref="WebContents.Login"/>.
    /// Electron's <c>app</c> <c>login</c>.</summary>
    public event EventHandler<LoginEventArgs>? Login;

    internal void RaiseCertificateError(object? sender, CertificateErrorEventArgs args) => CertificateError?.Invoke(this, args);

    /// <summary>A server asks a page for a client certificate, after the page's own
    /// <see cref="WebContents.SelectClientCertificate"/>. Electron's <c>app</c> <c>select-client-certificate</c>.</summary>
    public event EventHandler<SelectClientCertificateEventArgs>? SelectClientCertificate;

    internal void RaiseSelectClientCertificate(object? sender, SelectClientCertificateEventArgs args) => SelectClientCertificate?.Invoke(this, args);

    internal void RaiseLogin(object? sender, LoginEventArgs args) => Login?.Invoke(this, args);

    /// <summary>Creates the process's application.</summary>
    /// <param name="name">Defaults to the entry assembly's name.</param>
    /// <exception cref="InvalidOperationException">An application already exists.</exception>
    public static OrielApp Create(string? name = null)
    {
        if (s_current is not null) throw new InvalidOperationException("An OrielApp already exists in this process.");
        s_current = new OrielApp(name ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel");
        // Electron's init.ts does this at start-up, from package.json's desktopName or the app's name.
        s_current.SetDesktopName(DefaultDesktopName(s_current.Name));
        return s_current;
    }

    private string? _appUserModelId;

    /// <summary>Windows: the AppUserModelID that groups the app's taskbar buttons and sends its notifications. Not set at
    /// startup; on first use it is the process's explicit id if one was set, else <c>electron.app.{product name}</c>
    /// (the executable's VERSIONINFO product name), which then becomes the process's, as in Electron. Elsewhere the id
    /// given to <see cref="SetAppUserModelId"/>, else <c>electron.app.{Name}</c>.</summary>
    public string AppUserModelId => OperatingSystem.IsWindows()
        ? WindowsAppIdentity.AppUserModelId
        : _appUserModelId ?? "electron.app." + Name;

    /// <summary>Sets the process's AppUserModelID; Electron's <c>app.setAppUserModelId()</c>. No-op off Windows.</summary>
    public void SetAppUserModelId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (OperatingSystem.IsWindows()) WindowsAppIdentity.SetAppUserModelId(id);
        else _appUserModelId = id;
    }

    /// <summary>Windows: the CLSID of the COM class that receives toast activations, in <c>{XXXXXXXX-...}</c> form; a
    /// random one on first use unless set, or the one an existing Start-menu shortcut carries once a notification has
    /// been prepared. Electron's <c>app.toastActivatorCLSID</c>. Null elsewhere.</summary>
    public string? ToastActivatorClsid => OperatingSystem.IsWindows() ? WindowsAppIdentity.ToastActivatorClsid : null;

    /// <summary>Windows: sets <see cref="ToastActivatorClsid"/>, with or without braces. Electron's
    /// <c>app.setToastActivatorCLSID()</c>. No-op elsewhere.</summary>
    /// <exception cref="ArgumentException">Not a CLSID ("Invalid CLSID format").</exception>
    public void SetToastActivatorClsid(string clsid)
    {
        ArgumentNullException.ThrowIfNull(clsid);
        if (OperatingSystem.IsWindows() && !WindowsAppIdentity.SetToastActivatorClsid(clsid))
            throw new ArgumentException("Invalid CLSID format", nameof(clsid));
    }

    /// <summary>Turns on Chromium's sandbox for every child process, renderers included. Electron's <c>app.enableSandbox()</c>.</summary>
    /// <remarks>Call before <see cref="Run"/>. In the Windows bootstrap layout (eng/layout-windows.ps1) every child is
    /// already sandboxed, as no window can opt out (drift W13). Elsewhere on Windows and on macOS <see cref="Run"/>
    /// throws: only the bootstrap exe holds Chromium's sandbox broker, and the macOS helper lacks the sandbox library.</remarks>
    /// <exception cref="InvalidOperationException">The application is already running.</exception>
    public void EnableSandbox()
    {
        if (IsReady) throw new InvalidOperationException("Enable the sandbox before OrielApp.Run().");
        _sandboxRequested = IsSandboxed = true;
    }

    /// <summary>Whether children run sandboxed: Chromium's <c>--no-sandbox</c> always wins, the Windows bootstrap layout
    /// sandboxes by default, and elsewhere only <see cref="EnableSandbox"/> does, where the layout allows it.</summary>
    internal static bool SandboxChildren(bool requested, bool bootstrapSandbox, bool canSandbox, IEnumerable<string> args)
    {
        if (args.Contains("--no-sandbox")) return false;
        if (bootstrapSandbox) return true;
        if (!requested) return false;
        if (!canSandbox)
            throw new PlatformNotSupportedException(OperatingSystem.IsWindows()
                ? "On Windows the sandbox needs the bootstrap layout (eng/layout-windows.ps1), not the .NET apphost."
                : "The sandbox is not wired up on this OS yet.");
        return true;
    }

    /// <summary>Runs the application on the calling thread, which must be the process's main thread,
    /// until <see cref="Quit"/>. Returns the process exit code.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet implemented on this OS, or <see cref="EnableSandbox"/>
    /// was called where the layout cannot sandbox.</exception>
    public int Run()
    {
        void* sandboxInfo = null;
        var bootstrap = OperatingSystem.IsWindows() && BootstrapHost.TryGetSandboxInfo(out sandboxInfo);
        IsSandboxed = SandboxChildren(_sandboxRequested, sandboxInfo is not null, OperatingSystem.IsLinux(), Environment.GetCommandLineArgs());

        CefRuntime.Load(CefLayout.MainProcessLibrary());
        if (OperatingSystem.IsMacOS())
        {
            MacApplication.Install();
            // Electron's ui::InitIdleMonitor runs before ready, so a lock begun before the first idle query still counts.
            MacPower.InstallScreenMonitor();
            InstallMacDelegate();
        }

        var schemes = Protocol.TakePrivilegedSchemes();
        var schemeSwitch = schemes.Count > 0 ? SchemeSwitch.Format(schemes) : null;
        var app = new AppHandler(
            new BrowserProcessHandler
            {
                ContextInitialized = OnContextInitialized,
                AlreadyRunningAppRelaunch = OnSecondInstance,
                RegisterPreferences = WindowStateStore.Register,
                ChildProcessGone = OnChildProcessGone,
                AccessibilityModeChanged = OnAccessibilityModeChanged,
                // Every child (the Linux zygote included) must register the same schemes as this process.
                BeforeChildProcessLaunch = commandLine =>
                {
                    if (schemeSwitch is not null) AppendSwitch(commandLine, SchemeSwitch.Name, schemeSwitch);
                    CrashReporterNative.OnChildProcessLaunch(commandLine);
                },
            },
            OnBeforeCommandLineProcessing,
            schemes);
        var args = CefLayout.MainArgs();

        NativeObject.AddRef(app.Native);
        var exitCode = CefNative.cef_execute_process((cef_main_args_t*)&args, app.Native, sandboxInfo);
        if (exitCode >= 0) return exitCode;
        // Windows starts Crashpad in cef_execute_process (chrome_elf); macOS and Linux only inside cef_initialize.
        if (OperatingSystem.IsWindows()) CrashReporter.OnCrashpadStarted();

        // userData or sessionData set before Run moves the profile, as Electron's before ready.
        var dataDirectory = DataDirectory = GetPath("sessionData");
        // Electron's default log file is in DIR_LOGS, which release Chromium puts in userData.
        _loggingSwitches = CommandLine.ElectronLoggingSwitches(Environment.GetEnvironmentVariable, GetPath("userData"));
        var settings = new cef_settings_t
        {
            size = (nuint)sizeof(cef_settings_t),
            no_sandbox = IsSandboxed ? 0 : 1,
            log_severity = cef_log_severity_t.LOGSEVERITY_WARNING,
        };
        ProcessUserAgent = UserAgentFallback;
        CefString.Set(&settings.user_agent, ProcessUserAgent);
        CefString.Set(&settings.root_cache_path, dataDirectory);
        CefString.Set(&settings.cache_path, dataDirectory);
        // Under bootstrap every child runs as this same exe; naming another would make CEF drop the sandbox.
        if (!bootstrap) CefString.Set(&settings.browser_subprocess_path, CefLayout.SubprocessPath());

        // Before Chromium starts its own portal reads, so both see the portal from the same moment.
        if (OperatingSystem.IsLinux()) LinuxPortalSettings.Start();
        NativeObject.AddRef(app.Native);
        var crashDumps = OperatingSystem.IsWindows() ? null : CrashReporter.PrepareDatabase(GetPath("crashDumps"));
        var initialized = CefNative.cef_initialize((cef_main_args_t*)&args, &settings, app.Native, sandboxInfo) != 0;
        crashDumps?.Dispose();
        CefString.Clear(&settings.user_agent);
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
        if (!OperatingSystem.IsWindows()) CrashReporter.OnCrashpadStarted();

        CefNative.cef_run_message_loop();
        Exited?.Invoke(this, new ExitedEventArgs(_exitCode));
        UtilityProcess.TerminateAll();
        CefNative.cef_shutdown();
        // After shutdown, which releases the data directory a relaunched instance would otherwise find locked.
        StartRelaunches();
        return _exitCode;
    }

    /// <summary>Ends <see cref="Run"/> once open windows have closed. Electron's <c>app.quit()</c>.</summary>
    /// <remarks>May be called from any thread once the application is ready.</remarks>
    /// <remarks>As Electron: raises <see cref="BeforeQuit"/>, closes every window (each may still cancel with
    /// <see cref="BrowserWindow.Closing"/>, which cancels the quit), then raises <see cref="WillQuit"/>.</remarks>
    public void Quit()
    {
        // CEF only quits from the UI thread; called anywhere else, cef_quit_message_loop does nothing.
        if (IsReady) CefThread.RunOnUi(QuitOnUi);
    }

    /// <summary>Quits at once with <paramref name="exitCode"/>: no events, windows not asked. Electron's <c>app.exit()</c>.</summary>
    /// <remarks>May be called from any thread once the application is ready.</remarks>
    public void Exit(int exitCode = 0)
    {
        if (!IsReady) return;
        _exitCode = exitCode;
        CefThread.RunOnUi(CefNative.cef_quit_message_loop);
    }

    private int _exitCode;
    private bool _quitting;

    /// <summary>Whether a quit is in progress: Electron's <c>Browser::is_quitting()</c>.</summary>
    internal bool IsQuitting => _quitting;

    private void QuitOnUi()
    {
        if (_quitting) return;
        var before = new CancelEventArgs();
        BeforeQuit?.Invoke(this, before);
        if (before.Cancel) return;
        _quitting = true;
        var windows = BrowserWindow.GetAllWindows();
        if (windows.Count == 0) FinishQuit();
        else foreach (var window in windows) window.Close();
    }

    private void FinishQuit()
    {
        var will = new CancelEventArgs();
        WillQuit?.Invoke(this, will);
        _quitting = false;
        if (will.Cancel) return;
        SessionHelperPage.CloseAll();
        // CEF must not shut down with a page still open: views outside every window still have theirs.
        WebContentsView.CloseAll(CefNative.cef_quit_message_loop);
    }

    /// <summary>A window refused to close; a quit in progress stops, as in Electron.</summary>
    internal void OnCloseCancelled() => _quitting = false;

    internal void OnAllWindowsClosed()
    {
        if (_quitting) FinishQuit();
        else if (WindowAllClosed is { } handler) handler(this, EventArgs.Empty);
        else Quit();
    }

    private static void OnBeforeCommandLineProcessing(string processType, cef_command_line_t* commandLine)
    {
        // Chromium keeps its cookie-encryption key in the Keychain, whose access follows the code
        // signature; ad-hoc dev builds change it every build and macOS prompts for a password.
        // TODO(M4): use the real Keychain for signed builds, as Electron's safeStorage does.
        if (OperatingSystem.IsMacOS() && processType.Length == 0) AppendSwitch(commandLine, "use-mock-keychain");
        // Chrome style otherwise hands page auth challenges to Chrome's own login prompt and never calls
        // get_auth_credentials, so the login event would not fire.
        if (processType.Length == 0) AppendSwitch(commandLine, "disable-chrome-login-prompt");
        if (processType.Length == 0 && s_current is { } app)
        {
            // After Oriel's own switches, so the app can remove them as an Electron app can Electron's.
            app.CommandLine.ApplyTo(commandLine);
            CommandLine.Append(commandLine, app._loggingSwitches);
        }
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
        var (line, data) = TakeAdditionalData(commandLine);
        if (SecondInstance is { } handler) handler(this, new SecondInstanceEventArgs(line, workingDirectory, data));
        else BrowserWindow.FocusAll();
    }

    private void OnContextInitialized()
    {
        var global = CefNative.cef_request_context_get_global_context();
        Session.ApplyElectronDefaults(global);
        NativeObject.Release(global);
        Session.ResetProcessNetworkPreferences();
        ForgetSavedSystemProxy();
        NativeTheme.OnReady();
        // Before Ready, so the first window's page can already load from a custom scheme.
        Protocol.OnContextInitialized();
        // Electron's terminate: runs Browser::Quit.
        if (OperatingSystem.IsMacOS()) MacApplication.Quit = Quit;
        // macOS raises it from the app delegate, as Electron's does.
        if (!OperatingSystem.IsMacOS()) RaiseWillFinishLaunching();
        IsReady = true;
        Ready?.Invoke(this, EventArgs.Empty);
        _whenReady.TrySetResult();
        // After Ready, so an app that sets its own menu there never shows the default (Electron sets it just before
        // ready and lets the app replace it, which ends the same way).
        Menu.SetDefaultApplicationMenuIfUnset();
        if (OperatingSystem.IsMacOS()) MacApplicationMenu.Install();
    }
}

/// <summary>How the application is quitting; Electron's <c>quit</c> event arguments.</summary>
/// <param name="ExitCode">What <see cref="OrielApp.Run"/> returns.</param>
public sealed record ExitedEventArgs(int ExitCode);

/// <summary>A relaunch handed to the running instance; Electron's <c>second-instance</c> arguments.</summary>
/// <param name="CommandLine">The new launch's full command line.</param>
/// <param name="WorkingDirectory">The new launch's working directory.</param>
/// <param name="AdditionalData">What the new launch passed to <see cref="OrielApp.RequestSingleInstanceLock"/>, or null.</param>
public sealed record SecondInstanceEventArgs(string CommandLine, string WorkingDirectory, System.Text.Json.Nodes.JsonNode? AdditionalData = null);
