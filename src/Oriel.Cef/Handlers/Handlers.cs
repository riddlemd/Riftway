using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

// Struct arguments CEF passes to these callbacks are released after the managed callback returns;
// a callback that keeps one must AddRef it.

/// <summary>A CEF browser argument handed to a managed callback.</summary>
public unsafe delegate void BrowserCallback(cef_browser_t* browser);

/// <summary>A CEF window argument handed to a managed callback.</summary>
public unsafe delegate void WindowCallback(cef_window_t* window);

/// <summary>A CEF window argument handed to a managed predicate.</summary>
public unsafe delegate bool WindowPredicate(cef_window_t* window);

/// <summary>Runs managed code called from native code: an exception escaping into CEF kills the process.</summary>
internal static class Guard
{
    public static void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a CEF callback: {e}"); }
    }

    public static T Run<T>(Func<T> func, T fallback)
    {
        try { return func(); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] Unhandled exception in a CEF callback: {e}");
            return fallback;
        }
    }
}

/// <summary>A process's command line before Chromium reads it; <paramref name="processType"/> is empty in the browser process.</summary>
public unsafe delegate void CommandLineCallback(string processType, cef_command_line_t* commandLine);

/// <summary>A child process's command line before it launches.</summary>
public unsafe delegate void ChildCommandLineCallback(cef_command_line_t* commandLine);

/// <summary><c>cef_app_t</c>: process-level hooks.</summary>
public sealed unsafe class AppHandler
{
    private readonly BrowserProcessHandler? _browserProcess;

    public AppHandler(
        BrowserProcessHandler? browserProcess = null,
        CommandLineCallback? beforeCommandLineProcessing = null,
        IReadOnlyList<CustomScheme>? customSchemes = null)
    {
        CustomSchemes = customSchemes ?? [];
        _browserProcess = browserProcess;
        BeforeCommandLineProcessing = beforeCommandLineProcessing;
        Native = NativeObject.Create<cef_app_t>(this);
        if (browserProcess is not null) Native->get_browser_process_handler = &GetBrowserProcessHandler;
        if (beforeCommandLineProcessing is not null) Native->on_before_command_line_processing = &OnBeforeCommandLineProcessing;
        if (CustomSchemes.Count > 0) Native->on_register_custom_schemes = &OnRegisterCustomSchemes;
    }

    /// <summary>Schemes registered in this process; every process must register the same set.</summary>
    public IReadOnlyList<CustomScheme> CustomSchemes { get; }

    // The registrar is scoped to the call (cef_base_scoped_t), so it is never released.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegisterCustomSchemes(cef_app_t* self, cef_scheme_registrar_t* registrar)
    {
        var owner = NativeObject.Owner<AppHandler>(self);
        Guard.Run(() => SchemeSwitch.Register(registrar, owner.CustomSchemes));
    }

    public cef_app_t* Native { get; }

    public CommandLineCallback? BeforeCommandLineProcessing { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBeforeCommandLineProcessing(cef_app_t* self, cef_string_utf16_t* processType, cef_command_line_t* commandLine)
    {
        var owner = NativeObject.Owner<AppHandler>(self);
        var type = CefString.Read(processType);
        Guard.Run(() => owner.BeforeCommandLineProcessing!(type, commandLine));
        NativeObject.Release(commandLine);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_browser_process_handler_t* GetBrowserProcessHandler(cef_app_t* self)
    {
        var handler = NativeObject.Owner<AppHandler>(self)._browserProcess!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }
}

/// <summary><c>cef_browser_process_handler_t</c>: browser-process lifecycle.</summary>
public sealed unsafe class BrowserProcessHandler
{
    public BrowserProcessHandler()
    {
        Native = NativeObject.Create<cef_browser_process_handler_t>(this);
        Native->on_context_initialized = &OnContextInitialized;
        Native->on_already_running_app_relaunch = &OnAlreadyRunningAppRelaunch;
        Native->on_before_child_process_launch = &OnBeforeChildProcessLaunch;
    }

    public cef_browser_process_handler_t* Native { get; }

    /// <summary>CEF is initialised; runs on the UI thread.</summary>
    public Action? ContextInitialized { get; init; }

    /// <summary>Another launch of the app found this instance running and handed over its command line
    /// and working directory.</summary>
    public Action<string, string>? AlreadyRunningAppRelaunch { get; init; }

    /// <summary>A child process (zygote, renderer, GPU, utility) is about to launch with this command line.</summary>
    public ChildCommandLineCallback? BeforeChildProcessLaunch { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBeforeChildProcessLaunch(cef_browser_process_handler_t* self, cef_command_line_t* commandLine)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        if (owner.BeforeChildProcessLaunch is { } callback) Guard.Run(() => callback(commandLine));
        NativeObject.Release(commandLine);
    }

    // Always answers "handled": unhandled, CEF opens a default Chrome-styled browser window.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAlreadyRunningAppRelaunch(cef_browser_process_handler_t* self, cef_command_line_t* commandLine, cef_string_utf16_t* currentDirectory)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        var line = CefString.ReadUserFree(commandLine->get_command_line_string(commandLine));
        var directory = CefString.Read(currentDirectory);
        if (owner.AlreadyRunningAppRelaunch is { } callback) Guard.Run(() => callback(line, directory));
        NativeObject.Release(commandLine);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContextInitialized(cef_browser_process_handler_t* self)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        if (owner.ContextInitialized is { } callback) Guard.Run(callback);
    }
}

/// <summary><c>cef_client_t</c>: per-browser handlers. None yet; CEF uses its defaults.</summary>
public sealed unsafe class ClientHandler
{
    public ClientHandler() => Native = NativeObject.Create<cef_client_t>(this);

    public cef_client_t* Native { get; }
}

/// <summary><c>cef_browser_view_delegate_t</c>: events for the view hosting a browser.</summary>
public sealed unsafe class BrowserViewDelegate
{
    public BrowserViewDelegate()
    {
        Native = NativeObject.Create<cef_browser_view_delegate_t>(this);
        Native->on_browser_created = &OnBrowserCreated;
        Native->on_browser_destroyed = &OnBrowserDestroyed;
    }

    public cef_browser_view_delegate_t* Native { get; }

    public BrowserCallback? BrowserCreated { get; init; }

    public BrowserCallback? BrowserDestroyed { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBrowserCreated(cef_browser_view_delegate_t* self, cef_browser_view_t* view, cef_browser_t* browser)
    {
        var owner = NativeObject.Owner<BrowserViewDelegate>(self);
        if (owner.BrowserCreated is { } callback) Guard.Run(() => callback(browser));
        NativeObject.Release(browser);
        NativeObject.Release(view);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBrowserDestroyed(cef_browser_view_delegate_t* self, cef_browser_view_t* view, cef_browser_t* browser)
    {
        var owner = NativeObject.Owner<BrowserViewDelegate>(self);
        if (owner.BrowserDestroyed is { } callback) Guard.Run(() => callback(browser));
        NativeObject.Release(browser);
        NativeObject.Release(view);
    }
}

/// <summary><c>cef_window_delegate_t</c>: events and policy for a top-level Views window.</summary>
public sealed unsafe class WindowDelegate
{
    public WindowDelegate()
    {
        Native = NativeObject.Create<cef_window_delegate_t>(this);
        Native->on_window_created = &OnWindowCreated;
        Native->on_window_destroyed = &OnWindowDestroyed;
        Native->can_close = &CanClose;

        // A missing C callback answers 0, unlike the C++ defaults (true): without these the window
        // has no traffic-light buttons and cannot be resized.
        Native->with_standard_window_buttons = &WithStandardWindowButtons;
        Native->can_resize = &CanResize;
        Native->can_minimize = &CanMinimize;
        Native->can_maximize = &CanMaximize;
    }

    public cef_window_delegate_t* Native { get; }

    public bool Resizable { get; init; } = true;

    public bool Minimizable { get; init; } = true;

    public bool Maximizable { get; init; } = true;

    public WindowCallback? Created { get; init; }

    public WindowCallback? Destroyed { get; init; }

    /// <summary>Asked when the user or code closes the window; true lets it close.</summary>
    public WindowPredicate? Closing { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowCreated(cef_window_delegate_t* self, cef_window_t* window)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        if (owner.Created is { } callback) Guard.Run(() => callback(window));
        NativeObject.Release(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowDestroyed(cef_window_delegate_t* self, cef_window_t* window)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        if (owner.Destroyed is { } callback) Guard.Run(() => callback(window));
        NativeObject.Release(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanClose(cef_window_delegate_t* self, cef_window_t* window)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        var allow = owner.Closing is not { } predicate || Guard.Run(() => predicate(window), true);
        NativeObject.Release(window);
        return allow ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WithStandardWindowButtons(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanResize(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).Resizable ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanMinimize(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).Minimizable ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanMaximize(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).Maximizable ? 1 : 0;
    }
}

/// <summary>A raw DevTools protocol message (UTF-8 JSON) delivered to a managed callback.</summary>
public delegate void DevToolsMessageCallback(ReadOnlySpan<byte> message);

/// <summary><c>cef_dev_tools_message_observer_t</c>: every DevTools protocol result and event for one browser.</summary>
public sealed unsafe class DevToolsMessageObserver
{
    public DevToolsMessageObserver(DevToolsMessageCallback callback)
    {
        Callback = callback;
        Native = NativeObject.Create<cef_dev_tools_message_observer_t>(this);
        Native->on_dev_tools_message = &OnDevToolsMessage;
    }

    public cef_dev_tools_message_observer_t* Native { get; }

    public DevToolsMessageCallback Callback { get; }

    // Handling the raw message (returning 1) stops CEF from also splitting it into result/event calls.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnDevToolsMessage(cef_dev_tools_message_observer_t* self, cef_browser_t* browser, void* message, nuint size)
    {
        var owner = NativeObject.Owner<DevToolsMessageObserver>(self);
        try { owner.Callback(new ReadOnlySpan<byte>(message, (int)size)); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a DevTools callback: {e}"); }
        NativeObject.Release(browser);
        return 1;
    }
}
