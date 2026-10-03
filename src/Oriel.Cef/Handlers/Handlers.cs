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

/// <summary><c>cef_app_t</c>: process-level hooks.</summary>
public sealed unsafe class AppHandler
{
    private readonly BrowserProcessHandler? _browserProcess;

    public AppHandler(BrowserProcessHandler? browserProcess = null, CommandLineCallback? beforeCommandLineProcessing = null)
    {
        _browserProcess = browserProcess;
        BeforeCommandLineProcessing = beforeCommandLineProcessing;
        Native = NativeObject.Create<cef_app_t>(this);
        if (browserProcess is not null) Native->get_browser_process_handler = &GetBrowserProcessHandler;
        if (beforeCommandLineProcessing is not null) Native->on_before_command_line_processing = &OnBeforeCommandLineProcessing;
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
    }

    public cef_browser_process_handler_t* Native { get; }

    /// <summary>CEF is initialised; runs on the UI thread.</summary>
    public Action? ContextInitialized { get; init; }

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
