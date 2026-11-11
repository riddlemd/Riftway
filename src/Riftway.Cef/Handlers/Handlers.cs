using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

// Struct arguments CEF passes to these callbacks are released after the managed callback returns;
// a callback that keeps one must AddRef it.

/// <summary>A CEF browser argument handed to a managed callback.</summary>
public unsafe delegate void BrowserCallback(cef_browser_t* browser);

/// <summary>A CEF window argument handed to a managed callback.</summary>
public unsafe delegate void WindowCallback(cef_window_t* window);

/// <summary>A CEF window argument handed to a managed predicate.</summary>
public unsafe delegate bool WindowPredicate(cef_window_t* window);

/// <summary>A key event handed to a managed predicate; true if handled.</summary>
public unsafe delegate bool KeyEventPredicate(cef_key_event_t* keyEvent);

/// <summary>Runs managed code called from native code: an exception escaping into CEF kills the process.</summary>
internal static class Guard
{
    public static void Run(Action action)
    {
        try { action(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in a CEF callback: {e}"); }
    }

    public static T Run<T>(Func<T> func, T fallback)
    {
        try { return func(); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Unhandled exception in a CEF callback: {e}");
            return fallback;
        }
    }
}

/// <summary>A process's command line before Chromium reads it; <paramref name="processType"/> is empty in the browser process.</summary>
public unsafe delegate void CommandLineCallback(string processType, cef_command_line_t* commandLine);

/// <summary>A child process's command line before it launches.</summary>
public unsafe delegate void ChildCommandLineCallback(cef_command_line_t* commandLine);

/// <summary>A renderer's command line before it launches, with a request context of the profile it will serve.</summary>
public unsafe delegate void RenderCommandLineCallback(cef_command_line_t* commandLine, cef_request_context_t* context);

/// <summary>Registers preferences of <paramref name="type"/>: the app-wide <c>Local State</c> (global) or a profile's.</summary>
public unsafe delegate void RegisterPreferencesCallback(cef_preferences_type_t type, cef_preference_registrar_t* registrar);

/// <summary><c>cef_app_t</c>: process-level hooks.</summary>
public sealed unsafe class AppHandler
{
    private readonly BrowserProcessHandler? _browserProcess;
    private readonly RenderProcessHandler? _renderProcess;

    public AppHandler(
        BrowserProcessHandler? browserProcess = null,
        CommandLineCallback? beforeCommandLineProcessing = null,
        IReadOnlyList<CustomScheme>? customSchemes = null,
        RenderProcessHandler? renderProcess = null)
    {
        CustomSchemes = customSchemes ?? [];
        _browserProcess = browserProcess;
        _renderProcess = renderProcess;
        BeforeCommandLineProcessing = beforeCommandLineProcessing;
        Native = NativeObject.Create<cef_app_t>(this);
        if (browserProcess is not null) Native->get_browser_process_handler = &GetBrowserProcessHandler;
        if (renderProcess is not null) Native->get_render_process_handler = &GetRenderProcessHandler;
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_render_process_handler_t* GetRenderProcessHandler(cef_app_t* self)
    {
        var handler = NativeObject.Owner<AppHandler>(self)._renderProcess!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }
}

/// <summary><c>cef_render_process_handler_t</c>: renderer-process lifecycle.</summary>
public sealed unsafe class RenderProcessHandler
{
    private readonly Action _webKitInitialized;

    /// <param name="webKitInitialized">Runs once the renderer's Blink is up, after CEF has initialised the process.</param>
    public RenderProcessHandler(Action webKitInitialized)
    {
        _webKitInitialized = webKitInitialized;
        Native = NativeObject.Create<cef_render_process_handler_t>(this);
        Native->on_web_kit_initialized = &OnWebKitInitialized;
    }

    public cef_render_process_handler_t* Native { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWebKitInitialized(cef_render_process_handler_t* self)
    {
        var owner = NativeObject.Owner<RenderProcessHandler>(self);
        Guard.Run(owner._webKitInitialized);
    }
}

/// <summary>What patch 0007's <c>on_child_process_gone</c> reports.</summary>
public sealed record ChildProcessGoneInfo(string Type, cef_termination_status_t Status, int ExitCode, string ServiceName, string Name,
    uint SystemErrorCode);

/// <summary><c>cef_browser_process_handler_t</c>: browser-process lifecycle.</summary>
public sealed unsafe class BrowserProcessHandler
{
    public BrowserProcessHandler() : this(CefRuntime.IsPatched)
    {
    }

    // CEF refuses a base.size that is not the configured API version's struct.
    internal BrowserProcessHandler(bool patched)
    {
        if (patched)
        {
            var next = NativeObject.Create<cef_browser_process_handler_next_t>(this);
            next->on_child_process_gone = &OnChildProcessGone;
            next->on_accessibility_mode_changed = &OnAccessibilityModeChanged;
            next->on_before_render_process_launch = &OnBeforeRenderProcessLaunch;
            next->on_gpu_info_update = &OnGpuInfoUpdate;
            Native = &next->@base;
        }
        else Native = NativeObject.Create<cef_browser_process_handler_t>(this);
        Native->on_context_initialized = &OnContextInitialized;
        Native->on_already_running_app_relaunch = &OnAlreadyRunningAppRelaunch;
        Native->on_before_child_process_launch = &OnBeforeChildProcessLaunch;
        Native->on_register_custom_preferences = &OnRegisterCustomPreferences;
    }

    public cef_browser_process_handler_t* Native { get; }

    /// <summary>CEF is initialised; runs on the UI thread.</summary>
    public Action? ContextInitialized { get; init; }

    /// <summary>Another launch of the app found this instance running and handed over its command line
    /// and working directory.</summary>
    public Action<string, string>? AlreadyRunningAppRelaunch { get; init; }

    /// <summary>A child process (zygote, renderer, GPU, utility) is about to launch with this command line.</summary>
    public ChildCommandLineCallback? BeforeChildProcessLaunch { get; init; }

    /// <summary>Preferences to register before CEF reads them from disk; an unregistered one cannot be set.</summary>
    public RegisterPreferencesCallback? RegisterPreferences { get; init; }

    /// <summary>A child process that is not a renderer crashed, was killed or failed to launch (patch 0007; UI thread).</summary>
    public Action<ChildProcessGoneInfo>? ChildProcessGone { get; init; }

    /// <summary>Chromium added flags to the process's accessibility mode by itself; the new mode (patch 0009; UI thread).</summary>
    public Action<int>? AccessibilityModeChanged { get; init; }

    /// <summary>A renderer is about to launch for the profile of the request context (patch 0010; UI thread, after
    /// <see cref="BeforeChildProcessLaunch"/>).</summary>
    public RenderCommandLineCallback? BeforeRenderProcessLaunch { get; init; }

    /// <summary>Chromium's GPU information changed (patch 0013; UI thread).</summary>
    public Action? GpuInfoUpdate { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGpuInfoUpdate(cef_browser_process_handler_t* self)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        if (owner.GpuInfoUpdate is { } callback) Guard.Run(callback);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBeforeRenderProcessLaunch(cef_browser_process_handler_t* self, cef_command_line_t* commandLine,
        cef_request_context_t* context)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        if (owner.BeforeRenderProcessLaunch is { } callback) Guard.Run(() => callback(commandLine, context));
        NativeObject.Release(commandLine);
        NativeObject.Release(context);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChildProcessGone(cef_browser_process_handler_t* self, cef_string_utf16_t* type, cef_termination_status_t status,
        int exitCode, cef_string_utf16_t* serviceName, cef_string_utf16_t* name, uint systemErrorCode)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        var info = new ChildProcessGoneInfo(CefString.Read(type), status, exitCode, CefString.Read(serviceName), CefString.Read(name), systemErrorCode);
        if (owner.ChildProcessGone is { } callback) Guard.Run(() => callback(info));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAccessibilityModeChanged(cef_browser_process_handler_t* self, int mode)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        if (owner.AccessibilityModeChanged is { } callback) Guard.Run(() => callback(mode));
    }

    // The registrar is scoped to the call (cef_base_scoped_t), so it is never released.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegisterCustomPreferences(cef_browser_process_handler_t* self, cef_preferences_type_t type, cef_preference_registrar_t* registrar)
    {
        var owner = NativeObject.Owner<BrowserProcessHandler>(self);
        if (owner.RegisterPreferences is { } callback) Guard.Run(() => callback(type, registrar));
    }

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

/// <summary><c>cef_client_t</c>: per-browser handlers; those left null use CEF's defaults.</summary>
public sealed unsafe class ClientHandler
{
    private readonly DisplayHandler? _display;
    private readonly LoadHandler? _load;
    private readonly JsDialogHandler? _jsDialog;
    private readonly RequestHandler? _request;
    private readonly CommandHandler? _command;
    private readonly ContextMenuHandler? _contextMenu;
    private readonly KeyboardHandler? _keyboard;
    private readonly LifeSpanHandler? _lifeSpan;
    private readonly DragHandler? _drag;

    public ClientHandler(DisplayHandler? display = null, LoadHandler? load = null, JsDialogHandler? jsDialog = null, RequestHandler? request = null,
        CommandHandler? command = null, ContextMenuHandler? contextMenu = null, KeyboardHandler? keyboard = null, LifeSpanHandler? lifeSpan = null,
        DragHandler? drag = null)
    {
        _drag = drag;
        _command = command;
        _contextMenu = contextMenu;
        _keyboard = keyboard;
        _lifeSpan = lifeSpan;
        _display = display;
        _load = load;
        _jsDialog = jsDialog;
        _request = request;
        Native = NativeObject.Create<cef_client_t>(this);
        if (display is not null) Native->get_display_handler = &GetDisplayHandler;
        if (load is not null) Native->get_load_handler = &GetLoadHandler;
        if (jsDialog is not null) Native->get_jsdialog_handler = &GetJsDialogHandler;
        if (request is not null) Native->get_request_handler = &GetRequestHandler;
        if (command is not null) Native->get_command_handler = &GetCommandHandler;
        if (contextMenu is not null) Native->get_context_menu_handler = &GetContextMenuHandler;
        if (keyboard is not null) Native->get_keyboard_handler = &GetKeyboardHandler;
        if (lifeSpan is not null) Native->get_life_span_handler = &GetLifeSpanHandler;
        if (drag is not null) Native->get_drag_handler = &GetDragHandler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_drag_handler_t* GetDragHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._drag!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_life_span_handler_t* GetLifeSpanHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._lifeSpan!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    public cef_client_t* Native { get; }

    /// <summary>Answers permission prompts and media access; null leaves them to CEF.</summary>
    public PermissionHandler? Permission
    {
        get => _permission;
        init
        {
            _permission = value;
            if (value is not null) Native->get_permission_handler = &GetPermissionHandler;
        }
    }

    private readonly PermissionHandler? _permission;

    /// <summary>Reports find results; null leaves them to CEF.</summary>
    public FindHandler? Find
    {
        get => _find;
        init
        {
            _find = value;
            if (value is not null) Native->get_find_handler = &GetFindHandler;
        }
    }

    private readonly FindHandler? _find;

    /// <summary>Reports focus; null leaves it to CEF.</summary>
    public FocusHandler? Focus
    {
        get => _focus;
        init
        {
            _focus = value;
            if (value is not null) Native->get_focus_handler = &GetFocusHandler;
        }
    }

    private readonly FocusHandler? _focus;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_focus_handler_t* GetFocusHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._focus!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_find_handler_t* GetFindHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._find!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    /// <summary>Reports downloads; null leaves them to CEF.</summary>
    public DownloadHandler? Download
    {
        get => _download;
        init
        {
            _download = value;
            if (value is not null) Native->get_download_handler = &GetDownloadHandler;
        }
    }

    private readonly DownloadHandler? _download;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_download_handler_t* GetDownloadHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._download!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_permission_handler_t* GetPermissionHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._permission!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_keyboard_handler_t* GetKeyboardHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._keyboard!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_context_menu_handler_t* GetContextMenuHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._contextMenu!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_command_handler_t* GetCommandHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._command!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_jsdialog_handler_t* GetJsDialogHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._jsDialog!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_display_handler_t* GetDisplayHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._display!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_load_handler_t* GetLoadHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._load!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_request_handler_t* GetRequestHandler(cef_client_t* self)
    {
        var handler = NativeObject.Owner<ClientHandler>(self)._request!.Native;
        NativeObject.AddRef(handler);
        return handler;
    }
}

/// <summary>Whether a request should go through a <see cref="ResourceRequestHandler"/>; null frame for
/// requests with no frame (service workers).</summary>
public unsafe delegate bool ResourceRequestPredicate(cef_frame_t* frame, cef_request_t* request, bool isNavigation);

/// <summary>A request about to be sent; the callback may change it (headers, method, body, referrer).</summary>
public unsafe delegate void BeforeResourceLoadCallback(cef_frame_t* frame, cef_request_t* request);

/// <summary><c>cef_request_handler_t</c>: browser-level request policy. Only resource requests are hooked;
/// navigation policy and certificate prompts keep CEF's defaults.</summary>
public sealed unsafe class RequestHandler
{
    private readonly ResourceRequestHandler _resource;

    public RequestHandler(ResourceRequestHandler resource)
    {
        _resource = resource;
        Native = NativeObject.Create<cef_request_handler_t>(this);
        Native->get_resource_request_handler = &GetResourceRequestHandler;
        Native->on_certificate_error = &OnCertificateError;
        Native->on_select_client_certificate = &OnSelectClientCertificate;
        Native->on_before_browse = &OnBeforeBrowse;
        Native->get_auth_credentials = &GetAuthCredentials;
        Native->on_render_process_terminated = &OnRenderProcessTerminated;
        Native->on_render_process_unresponsive = &OnRenderProcessUnresponsive;
        Native->on_render_process_responsive = &OnRenderProcessResponsive;
    }

    /// <summary>The page's renderer process ended other than by the page closing: CEF's status and exit code. UI thread.</summary>
    public Action<cef_termination_status_t, int>? RenderProcessTerminated { get; init; }

    /// <summary>The renderer stopped answering input. Setting it also suppresses Chrome's "page unresponsive" dialog. UI thread.</summary>
    public Action? RenderProcessUnresponsive { get; init; }

    /// <summary>The renderer answers again after <see cref="RenderProcessUnresponsive"/>. UI thread.</summary>
    public Action? RenderProcessResponsive { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRenderProcessTerminated(cef_request_handler_t* self, cef_browser_t* browser, cef_termination_status_t status, int errorCode, cef_string_utf16_t* errorString)
    {
        if (NativeObject.Owner<RequestHandler>(self).RenderProcessTerminated is { } callback) Guard.Run(() => callback(status, errorCode));
        NativeObject.Release(browser);
    }

    // Claiming the event and telling CEF to keep waiting is Electron's behaviour: it shows no hung-page dialog.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnRenderProcessUnresponsive(cef_request_handler_t* self, cef_browser_t* browser, cef_unresponsive_process_callback_t* callback)
    {
        var handler = NativeObject.Owner<RequestHandler>(self).RenderProcessUnresponsive;
        if (handler is not null) Guard.Run(handler);
        if (handler is not null) callback->wait(callback);
        NativeObject.Release(callback);
        NativeObject.Release(browser);
        return handler is null ? 0 : 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRenderProcessResponsive(cef_request_handler_t* self, cef_browser_t* browser)
    {
        if (NativeObject.Owner<RequestHandler>(self).RenderProcessResponsive is { } callback) Guard.Run(callback);
        NativeObject.Release(browser);
    }

    /// <summary>A frame is about to navigate or follow a redirect; true cancels it. UI thread.</summary>
    public Func<BrowseRequest, bool>? BeforeBrowse { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBeforeBrowse(cef_request_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_request_t* request,
        int userGesture, int isRedirect)
    {
        var owner = NativeObject.Owner<RequestHandler>(self);
        var cancel = false;
        if (owner.BeforeBrowse is { } decide)
        {
            var browse = new BrowseRequest(CefString.ReadUserFree(request->get_url(request)), isRedirect != 0, userGesture != 0,
                (uint)request->get_transition_type(request), HeaderMap.FramePath(frame));
            cancel = Guard.Run(() => decide(browse), false);
        }
        NativeObject.Release(request);
        NativeObject.Release(frame);
        NativeObject.Release(browser);
        return cancel ? 1 : 0;
    }


    /// <summary>A server asks for a client certificate; the task's result is the index of the one to use, or null for
    /// none. Null picks the first. UI thread.</summary>
    public Func<ClientCertificateRequest, Task<int?>>? SelectClientCertificate { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSelectClientCertificate(cef_request_handler_t* self, cef_browser_t* browser, int isProxy, cef_string_utf16_t* host,
        int port, nuint count, cef_x509_certificate_t** certificates, cef_select_client_certificate_callback_t* callback)
        => SecurityHandlers.OnSelectClientCertificate(NativeObject.Owner<RequestHandler>(self).SelectClientCertificate, browser, isProxy, host,
            port, count, certificates, callback);

    /// <summary>A navigation hit an invalid certificate; the task's result trusts it. Null cancels. UI thread.</summary>
    public Func<CertificateErrorInfo, Task<bool>>? CertificateError { get; init; }

    /// <summary>A server or proxy asks for credentials; the task's result answers, null cancels. IO thread.</summary>
    public Func<AuthChallenge, Task<(string User, string Password)?>>? Login { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnCertificateError(cef_request_handler_t* self, cef_browser_t* browser, cef_errorcode_t error,
        cef_string_utf16_t* url, cef_sslinfo_t* ssl, cef_callback_t* callback)
        => SecurityHandlers.OnCertificateError(NativeObject.Owner<RequestHandler>(self).CertificateError, browser, error, url, ssl, callback);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetAuthCredentials(cef_request_handler_t* self, cef_browser_t* browser, cef_string_utf16_t* originUrl, int isProxy,
        cef_string_utf16_t* host, int port, cef_string_utf16_t* realm, cef_string_utf16_t* scheme, cef_auth_callback_t* callback)
        => SecurityHandlers.GetAuthCredentials(NativeObject.Owner<RequestHandler>(self).Login, browser, originUrl, isProxy, host, port, realm, scheme, callback);

    public cef_request_handler_t* Native { get; }

    /// <summary>Which requests reach the resource handler; null for all. Runs on the IO thread.</summary>
    public ResourceRequestPredicate? Wants { get; init; }

    // Returning null keeps CEF's default handling, so requests nobody asked about cost no extra hop.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_resource_request_handler_t* GetResourceRequestHandler(
        cef_request_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_request_t* request,
        int isNavigation, int isDownload, cef_string_utf16_t* requestInitiator, int* disableDefaultHandling)
    {
        var owner = NativeObject.Owner<RequestHandler>(self);
        var wants = owner.Wants is not { } predicate || Guard.Run(() => predicate(frame, request, isNavigation != 0), false);
        if (wants) HeaderMap.NoteInitiator(request, requestInitiator, isNavigation != 0, browser is not null);
        NativeObject.Release(request);
        NativeObject.Release(frame);
        NativeObject.Release(browser);
        if (!wants) return null;
        var handler = owner._resource.Native;
        NativeObject.AddRef(handler);
        return handler;
    }
}

/// <summary><c>cef_resource_request_handler_t</c>: per-request hooks; only <c>on_before_resource_load</c>
/// is implemented.</summary>
public sealed unsafe class ResourceRequestHandler
{
    public ResourceRequestHandler()
    {
        Native = NativeObject.Create<cef_resource_request_handler_t>(this);
        Native->on_before_resource_load = &OnBeforeResourceLoad;
        Native->on_resource_redirect = &OnResourceRedirect;
        Native->on_resource_response = &OnResourceResponse;
        Native->on_resource_load_complete = &OnResourceLoadComplete;
    }

    public cef_resource_request_handler_t* Native { get; }

    /// <summary>A request is about to be sent. Runs on the IO thread.</summary>
    public BeforeResourceLoadCallback? BeforeResourceLoad { get; init; }

    /// <summary>After <see cref="BeforeResourceLoad"/>: decides the request; null sends it unchanged without waiting.
    /// Runs on the IO thread; the task may finish on any thread.</summary>
    public Func<RequestInfo, Task<RequestDecision>?>? Decide { get; init; }

    /// <summary>The request was sent; its headers are final. Runs on the IO thread.</summary>
    public Action<RequestInfo>? Sent { get; init; }

    /// <summary>The server redirected the request to the given URL. Runs on the IO thread.</summary>
    public Action<RequestInfo, ResponseInfo, string>? Redirected { get; init; }

    /// <summary>The response's headers arrived. Runs on the IO thread.</summary>
    public Action<RequestInfo, ResponseInfo>? ResponseStarted { get; init; }

    /// <summary>The request finished, failed or was cancelled. Runs on the IO thread.</summary>
    public Action<RequestInfo, ResponseInfo, cef_urlrequest_status_t>? Completed { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_return_value_t OnBeforeResourceLoad(
        cef_resource_request_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_request_t* request, cef_callback_t* callback)
    {
        var owner = NativeObject.Owner<ResourceRequestHandler>(self);
        if (owner.BeforeResourceLoad is { } handler) Guard.Run(() => handler(frame, request));
        var info = HeaderMap.Info(request, frame);
        var decision = owner.Decide is { } decide ? Guard.Run(() => decide(info), null) : null;
        if (frame is not null) NativeObject.Release(frame);
        if (browser is not null) NativeObject.Release(browser);
        if (decision is null)
        {
            owner.Notify(owner.Sent, request, info.FramePath);
            NativeObject.Release(callback);
            NativeObject.Release(request);
            return cef_return_value_t.RV_CONTINUE;
        }
        // The request and callback are kept until the decision comes; CEF reads the request again on continue.
        decision.ContinueWith(t =>
        {
            var answer = t.IsCompletedSuccessfully ? t.Result : new RequestDecision(Cancel: true);
            if (!t.IsCompletedSuccessfully) Console.Error.WriteLine($"[Riftway] A request decision failed; cancelling the request: {t.Exception?.InnerException}");
            if (answer.Cancel) callback->cancel(callback);
            else
            {
                if (answer.Headers is { } headers) HeaderMap.Write(request, headers);
                if (answer.RedirectUrl is { } url)
                {
                    cef_string_utf16_t native = default;
                    CefString.Set(&native, url);
                    request->set_url(request, &native);
                    CefString.Clear(&native);
                }
                else owner.Notify(owner.Sent, request, info.FramePath);
                callback->cont(callback);
            }
            NativeObject.Release(callback);
            NativeObject.Release(request);
        }, TaskScheduler.Default);
        return cef_return_value_t.RV_CONTINUE_ASYNC;
    }

    // The frame is gone once the decision comes, so its path is the one read before.
    private void Notify(Action<RequestInfo>? action, cef_request_t* request, IReadOnlyList<(string Name, string Url)>? framePath)
    {
        if (action is null) return;
        var info = HeaderMap.Info(request, null) with { FramePath = framePath };
        Guard.Run(() => action(info));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResourceRedirect(cef_resource_request_handler_t* self, cef_browser_t* browser, cef_frame_t* frame,
        cef_request_t* request, cef_response_t* response, cef_string_utf16_t* newUrl)
    {
        var owner = NativeObject.Owner<ResourceRequestHandler>(self);
        if (owner.Redirected is { } redirected)
        {
            var (info, answer, url) = (HeaderMap.Info(request, frame), HeaderMap.Info(response), CefString.Read(newUrl));
            Guard.Run(() => redirected(info, answer, url));
        }
        ReleaseAll(browser, frame, request, response);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnResourceResponse(cef_resource_request_handler_t* self, cef_browser_t* browser, cef_frame_t* frame,
        cef_request_t* request, cef_response_t* response)
    {
        var owner = NativeObject.Owner<ResourceRequestHandler>(self);
        if (owner.ResponseStarted is { } started)
        {
            var (info, answer) = (HeaderMap.Info(request, frame), HeaderMap.Info(response));
            Guard.Run(() => started(info, answer));
        }
        ReleaseAll(browser, frame, request, response);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResourceLoadComplete(cef_resource_request_handler_t* self, cef_browser_t* browser, cef_frame_t* frame,
        cef_request_t* request, cef_response_t* response, cef_urlrequest_status_t status, long receivedContentLength)
    {
        var owner = NativeObject.Owner<ResourceRequestHandler>(self);
        if (owner.Completed is { } completed)
        {
            var (info, answer) = (HeaderMap.Info(request, frame), HeaderMap.Info(response));
            Guard.Run(() => completed(info, answer, status));
        }
        HeaderMap.ForgetInitiator(request->get_identifier(request));
        ReleaseAll(browser, frame, request, response);
    }

    // Browser and frame are null for requests without a page, such as a service worker's.
    private static void ReleaseAll(cef_browser_t* browser, cef_frame_t* frame, cef_request_t* request, cef_response_t* response)
    {
        NativeObject.Release(response);
        NativeObject.Release(request);
        if (frame is not null) NativeObject.Release(frame);
        if (browser is not null) NativeObject.Release(browser);
    }
}

/// <summary><c>cef_command_handler_t</c>: Chrome commands from keyboard shortcuts and menus (Chrome style only).</summary>
public sealed unsafe class CommandHandler
{
    private readonly HashSet<int> _blocked;

    /// <param name="blockedCommands"><c>IDC_*</c> names from cef_command_ids.h; names this build lacks are ignored.</param>
    public CommandHandler(IEnumerable<string> blockedCommands)
    {
        _blocked = [.. blockedCommands.Select(IdOf).Where(id => id > 0)];
        Native = NativeObject.Create<cef_command_handler_t>(this);
        Native->on_chrome_command = &OnChromeCommand;
        // C++ answers true for these; a missing C callback answers 0, and the browser process then segfaults
        // intermittently once this handler is in use.
        Native->is_chrome_app_menu_item_visible = &AppMenuItemAllowed;
        Native->is_chrome_app_menu_item_enabled = &AppMenuItemAllowed;
        Native->is_chrome_page_action_icon_visible = &PageActionIconVisible;
        Native->is_chrome_toolbar_button_visible = &ToolbarButtonVisible;
    }

    public cef_command_handler_t* Native { get; }

    // Numbers shift between Chromium versions; CEF maps the stable names.
    private static int IdOf(string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return CefNative.cef_id_for_command_id_name(p);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnChromeCommand(cef_command_handler_t* self, cef_browser_t* browser, int commandId, cef_window_open_disposition_t disposition)
    {
        NativeObject.Release(browser);
        return NativeObject.Owner<CommandHandler>(self)._blocked.Contains(commandId) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AppMenuItemAllowed(cef_command_handler_t* self, cef_browser_t* browser, int commandId)
    {
        NativeObject.Release(browser);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int PageActionIconVisible(cef_command_handler_t* self, cef_chrome_page_action_icon_type_t iconType) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ToolbarButtonVisible(cef_command_handler_t* self, cef_chrome_toolbar_button_type_t buttonType) => 1;
}

/// <summary><c>cef_jsdialog_handler_t</c>: the page's <c>alert</c>, <c>confirm</c> and <c>prompt</c>.</summary>
/// <summary>The kind of dialog a page asks for: <c>alert</c>, <c>confirm</c> or <c>prompt</c>.</summary>
public enum JsDialogKind
{
    Alert,
    Confirm,
    Prompt,
}

public sealed unsafe class JsDialogHandler
{
    public JsDialogHandler()
    {
        Native = NativeObject.Create<cef_jsdialog_handler_t>(this);
        Native->on_jsdialog = &OnJsDialog;
        Native->on_before_unload_dialog = &OnBeforeUnloadDialog;
        Native->on_reset_dialog_state = &OnResetDialogState;
    }

    /// <summary>A page asks for a dialog: (origin URL, kind, message, answer) → true when the handler shows it and calls
    /// answer later, on any thread: true/false is the user's choice (OK/Cancel), null drops the dialog unanswered. False
    /// suppresses it (<c>alert</c> returns at once, <c>confirm</c> false, <c>prompt</c> null). Null leaves CEF's dialog.</summary>
    public Func<string, JsDialogKind, string, Action<bool?>, bool>? Run { get; init; }

    /// <summary>CEF cancels pending dialogs and resets dialog state (navigation, closing): Electron's <c>CancelDialogs</c>.</summary>
    public Action? ResetDialogState { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResetDialogState(cef_jsdialog_handler_t* self, cef_browser_t* browser)
    {
        if (NativeObject.Owner<JsDialogHandler>(self).ResetDialogState is { } reset) Guard.Run(reset);
        NativeObject.Release(browser);
    }

    /// <summary>A beforeunload handler objects to leaving the page: (isReload) → whether to leave anyway. Answered at
    /// once, with no dialog; null leaves CEF's dialog.</summary>
    public Func<bool, bool>? BeforeUnload { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBeforeUnloadDialog(cef_jsdialog_handler_t* self, cef_browser_t* browser, cef_string_utf16_t* message, int isReload, cef_jsdialog_callback_t* callback)
    {
        var handled = 0;
        if (NativeObject.Owner<JsDialogHandler>(self).BeforeUnload is { } decide)
        {
            var leave = false;
            Guard.Run(() => leave = decide(isReload != 0));
            cef_string_utf16_t none = default;
            callback->cont(callback, leave ? 1 : 0, &none);
            handled = 1;
        }
        NativeObject.Release(callback);
        NativeObject.Release(browser);
        return handled;
    }

    public cef_jsdialog_handler_t* Native { get; }

    /// <summary>Suppresses every dialog: <c>alert</c> returns at once, <c>confirm</c> false, <c>prompt</c> null.</summary>
    public bool SuppressAll { get; set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnJsDialog(cef_jsdialog_handler_t* self, cef_browser_t* browser, cef_string_utf16_t* originUrl,
        cef_jsdialog_type_t type, cef_string_utf16_t* message, cef_string_utf16_t* defaultPrompt, cef_jsdialog_callback_t* callback, int* suppressMessage)
    {
        var handler = NativeObject.Owner<JsDialogHandler>(self);
        NativeObject.Release(browser);
        // Suppressing rather than answering through the callback: CEF treats instant answers as dialog spam.
        if (handler.SuppressAll || handler.Run is not { } run)
        {
            *suppressMessage = handler.SuppressAll ? 1 : 0;
            NativeObject.Release(callback);
            return 0;
        }
        var kind = type switch
        {
            cef_jsdialog_type_t.JSDIALOGTYPE_CONFIRM => JsDialogKind.Confirm,
            cef_jsdialog_type_t.JSDIALOGTYPE_PROMPT => JsDialogKind.Prompt,
            _ => JsDialogKind.Alert,
        };
        // The callback is kept until the answer (or the drop) comes, once.
        var pending = (nint)callback;
        void Answer(bool? ok)
        {
            var held = Interlocked.Exchange(ref pending, 0);
            if (held == 0) return;
            CefThread.PostUi(() =>
            {
                var cb = (cef_jsdialog_callback_t*)held;
                if (ok is { } accepted)
                {
                    cef_string_utf16_t none = default;
                    cb->cont(cb, accepted ? 1 : 0, &none);
                }
                NativeObject.Release(cb);
            });
        }
        var shown = false;
        Guard.Run(() => shown = run(CefString.Read(originUrl), kind, CefString.Read(message), Answer));
        if (shown) return 1;
        if (Interlocked.Exchange(ref pending, 0) != 0) NativeObject.Release(callback);
        *suppressMessage = 1;
        return 0;
    }
}

/// <summary><c>cef_display_handler_t</c>: page-driven display changes.</summary>
public sealed unsafe class DisplayHandler
{
    public DisplayHandler()
    {
        Native = NativeObject.Create<cef_display_handler_t>(this);
        Native->on_title_change = &OnTitleChange;
        Native->on_favicon_urlchange = &OnFaviconUrlChange;
        Native->on_fullscreen_mode_change = &OnFullscreenModeChange;
        Native->on_console_message = &OnConsoleMessage;
        Native->on_status_message = &OnStatusMessage;
        Native->on_cursor_change = &OnCursorChange;
        Native->on_contents_bounds_change = &OnContentsBoundsChange;
    }

    /// <summary>The page asked for new window bounds (<c>window.moveTo</c>, <c>resizeTo</c>), in DIP screen coordinates;
    /// the callback applies them or not, and CEF's own handling is skipped.</summary>
    public Action<cef_rect_t>? ContentsBoundsChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnContentsBoundsChange(cef_display_handler_t* self, cef_browser_t* browser, cef_rect_t* newBounds)
    {
        var handled = 0;
        if (NativeObject.Owner<DisplayHandler>(self).ContentsBoundsChanged is { } callback)
        {
            var bounds = *newBounds;
            Guard.Run(() => callback(bounds));
            handled = 1;
        }
        NativeObject.Release(browser);
        return handled;
    }

    /// <summary>A console message: (severity, message, source, line). CEF still logs it.</summary>
    public Action<cef_log_severity_t, string, string, int>? ConsoleMessage { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnConsoleMessage(cef_display_handler_t* self, cef_browser_t* browser, cef_log_severity_t level, cef_string_utf16_t* message, cef_string_utf16_t* source, int line)
    {
        var text = CefString.Read(message);
        var from = CefString.Read(source);
        if (NativeObject.Owner<DisplayHandler>(self).ConsoleMessage is { } callback) Guard.Run(() => callback(level, text, from, line));
        NativeObject.Release(browser);
        return 0;
    }

    /// <summary>The URL under the pointer changed, or empty when none: CEF's status message is Chromium's target URL.</summary>
    public Action<string>? StatusMessage { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStatusMessage(cef_display_handler_t* self, cef_browser_t* browser, cef_string_utf16_t* value)
    {
        var text = CefString.Read(value);
        if (NativeObject.Owner<DisplayHandler>(self).StatusMessage is { } callback) Guard.Run(() => callback(text));
        NativeObject.Release(browser);
    }

    /// <summary>The page set its cursor; a custom one comes with its image. CEF still shows it.</summary>
    public Action<CursorChange>? CursorChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnCursorChange(cef_display_handler_t* self, cef_browser_t* browser, void* cursor, cef_cursor_type_t type, cef_cursor_info_t* info)
    {
        CursorChange change = new(type, null, 1, 0, 0, 0, 0);
        if (type == cef_cursor_type_t.CT_CUSTOM && info is not null && info->buffer is not null)
        {
            var bytes = new byte[info->size.width * info->size.height * 4];
            new ReadOnlySpan<byte>(info->buffer, bytes.Length).CopyTo(bytes);
            change = new(type, bytes, info->image_scale_factor, info->size.width, info->size.height, info->hotspot.x, info->hotspot.y);
        }
        if (NativeObject.Owner<DisplayHandler>(self).CursorChanged is { } callback) Guard.Run(() => callback(change));
        NativeObject.Release(browser);
        return 0;
    }

    /// <summary>The page entered (true) or left HTML full screen; Chrome style resizes the window itself.</summary>
    public Action<bool>? FullscreenModeChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFullscreenModeChange(cef_display_handler_t* self, cef_browser_t* browser, int fullscreen)
    {
        if (NativeObject.Owner<DisplayHandler>(self).FullscreenModeChanged is { } callback) Guard.Run(() => callback(fullscreen != 0));
        NativeObject.Release(browser);
    }

    /// <summary>The page's favicon URLs changed (all of them, in the page's order).</summary>
    public Action<IReadOnlyList<string>>? FaviconUrlsChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFaviconUrlChange(cef_display_handler_t* self, cef_browser_t* browser, cef_string_list_t* iconUrls)
    {
        var urls = new List<string>();
        for (nuint i = 0; iconUrls is not null && i < CefNative.cef_string_list_size(iconUrls); i++)
        {
            cef_string_utf16_t url = default;
            if (CefNative.cef_string_list_value(iconUrls, i, &url) != 0) urls.Add(CefString.Read(&url));
            CefString.Clear(&url);
        }
        if (NativeObject.Owner<DisplayHandler>(self).FaviconUrlsChanged is { } callback) Guard.Run(() => callback(urls));
        NativeObject.Release(browser);
    }

    public cef_display_handler_t* Native { get; }

    /// <summary>The page's title changed.</summary>
    public Action<string>? TitleChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTitleChange(cef_display_handler_t* self, cef_browser_t* browser, cef_string_utf16_t* title)
    {
        var owner = NativeObject.Owner<DisplayHandler>(self);
        var text = CefString.Read(title);
        if (owner.TitleChanged is { } callback) Guard.Run(() => callback(text));
        NativeObject.Release(browser);
    }
}

/// <summary>A cursor the page set: its type, and for a custom one its BGRA pixels, scale, size and hotspot.</summary>
public sealed record CursorChange(cef_cursor_type_t Type, byte[]? Bgra, float Scale, int Width, int Height, int HotspotX, int HotspotY);

/// <summary><c>cef_focus_handler_t</c>: the page's view took focus. Focus requests are left to CEF.</summary>
public sealed unsafe class FocusHandler
{
    public FocusHandler()
    {
        Native = NativeObject.Create<cef_focus_handler_t>(this);
        Native->on_got_focus = &OnGotFocus;
    }

    public cef_focus_handler_t* Native { get; }

    /// <summary>The page's view took keyboard focus: Chromium's OnWebContentsFocused.</summary>
    public Action? GotFocus { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGotFocus(cef_focus_handler_t* self, cef_browser_t* browser)
    {
        if (NativeObject.Owner<FocusHandler>(self).GotFocus is { } callback) Guard.Run(callback);
        NativeObject.Release(browser);
    }
}

/// <summary><c>cef_load_handler_t</c>: navigation progress. Frame arguments are reduced to whether the
/// frame is the main frame.</summary>
public sealed unsafe class LoadHandler
{
    public LoadHandler()
    {
        Native = NativeObject.Create<cef_load_handler_t>(this);
        Native->on_loading_state_change = &OnLoadingStateChange;
        Native->on_load_start = &OnLoadStart;
        Native->on_load_end = &OnLoadEnd;
        Native->on_load_error = &OnLoadError;
    }

    public cef_load_handler_t* Native { get; }

    /// <summary>Loading started (true) or stopped (false) for the whole browser.</summary>
    public Action<bool>? LoadingStateChanged { get; init; }

    /// <summary>A frame started loading: (isMainFrame).</summary>
    public Action<bool>? LoadStarted { get; init; }

    /// <summary>A frame finished loading: (isMainFrame, httpStatusCode).</summary>
    public Action<bool, int>? LoadEnded { get; init; }

    /// <summary>A frame failed to load: (isMainFrame, errorCode, errorText, failedUrl).</summary>
    public Action<bool, int, string, string>? LoadFailed { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLoadingStateChange(cef_load_handler_t* self, cef_browser_t* browser, int isLoading, int canGoBack, int canGoForward)
    {
        var owner = NativeObject.Owner<LoadHandler>(self);
        if (owner.LoadingStateChanged is { } callback) Guard.Run(() => callback(isLoading != 0));
        NativeObject.Release(browser);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLoadStart(cef_load_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_transition_type_t transition)
    {
        var owner = NativeObject.Owner<LoadHandler>(self);
        var isMain = frame->is_main(frame) != 0;
        if (owner.LoadStarted is { } callback) Guard.Run(() => callback(isMain));
        NativeObject.Release(frame);
        NativeObject.Release(browser);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLoadEnd(cef_load_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, int httpStatusCode)
    {
        var owner = NativeObject.Owner<LoadHandler>(self);
        var isMain = frame->is_main(frame) != 0;
        if (owner.LoadEnded is { } callback) Guard.Run(() => callback(isMain, httpStatusCode));
        NativeObject.Release(frame);
        NativeObject.Release(browser);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLoadError(cef_load_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_errorcode_t errorCode, cef_string_utf16_t* errorText, cef_string_utf16_t* failedUrl)
    {
        var owner = NativeObject.Owner<LoadHandler>(self);
        var isMain = frame->is_main(frame) != 0;
        var text = CefString.Read(errorText);
        var url = CefString.Read(failedUrl);
        if (owner.LoadFailed is { } callback) Guard.Run(() => callback(isMain, (int)errorCode, text, url));
        NativeObject.Release(frame);
        NativeObject.Release(browser);
    }
}

/// <summary><c>cef_browser_view_delegate_t</c>: events for the view hosting a browser.</summary>
public sealed unsafe class BrowserViewDelegate
{
    public BrowserViewDelegate()
    {
        Native = NativeObject.Create<cef_browser_view_delegate_t>(this);
        Native->on_browser_created = &OnBrowserCreated;
        Native->on_browser_destroyed = &OnBrowserDestroyed;
        Native->get_delegate_for_popup_browser_view = &GetDelegateForPopupBrowserView;
        Native->on_popup_browser_view_created = &OnPopupBrowserViewCreated;
        Native->@base.on_theme_changed = &OnThemeChanged;
        Native->get_browser_runtime_style = &GetBrowserRuntimeStyle;
    }

    public cef_browser_view_delegate_t* Native { get; }

    /// <summary>The view's theme was applied: CEF has just cleared any background colour set on it.</summary>
    public Action? ThemeChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnThemeChanged(cef_view_delegate_t* self, cef_view_t* view)
    {
        var owner = NativeObject.Owner<BrowserViewDelegate>(self);
        if (owner.ThemeChanged is { } callback) Guard.Run(callback);
        NativeObject.Release(view);
    }
    /// <summary>The browser's runtime style. A Chrome-style window hosts at most one Chrome-style browser, so
    /// any other browser in it must be Alloy style.</summary>
    public cef_runtime_style_t RuntimeStyle { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_runtime_style_t GetBrowserRuntimeStyle(cef_browser_view_delegate_t* self)
        => NativeObject.Owner<BrowserViewDelegate>(self).RuntimeStyle;

    /// <summary>The delegate for a page popup's view, by the client its <c>on_before_popup</c> chose; null for the
    /// same delegate as this view. Not asked for DevTools.</summary>
    public Func<nint, nint>? PopupDelegate { get; init; }

    /// <summary>A page popup's view exists: host it in a window and return true, or false for CEF's default window.
    /// Arguments: the popup view and its delegate. Not called for DevTools.</summary>
    public Func<nint, nint, bool>? PopupViewCreated { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_browser_view_delegate_t* GetDelegateForPopupBrowserView(cef_browser_view_delegate_t* self, cef_browser_view_t* browserView,
        cef_browser_settings_t* settings, cef_client_t* client, int isDevtools)
    {
        var owner = NativeObject.Owner<BrowserViewDelegate>(self);
        var popupDelegate = isDevtools == 0 && owner.PopupDelegate is { } find ? Guard.Run(() => find((nint)client), 0) : 0;
        NativeObject.Release(client);
        NativeObject.Release(browserView);
        if (popupDelegate == 0) return null;
        NativeObject.AddRef((void*)popupDelegate);
        return (cef_browser_view_delegate_t*)popupDelegate;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPopupBrowserViewCreated(cef_browser_view_delegate_t* self, cef_browser_view_t* browserView, cef_browser_view_t* popupBrowserView, int isDevtools)
    {
        var owner = NativeObject.Owner<BrowserViewDelegate>(self);
        var handled = false;
        if (isDevtools == 0 && owner.PopupViewCreated is { } host)
        {
            var view = &popupBrowserView->@base;
            var popupDelegate = (nint)view->get_delegate(view);
            handled = Guard.Run(() => host((nint)popupBrowserView, popupDelegate), false);
            NativeObject.Release((void*)popupDelegate);
        }
        NativeObject.Release(popupBrowserView);
        NativeObject.Release(browserView);
        return handled ? 1 : 0;
    }


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
    public WindowDelegate() : this(CefRuntime.IsPatched)
    {
    }

    // CEF wraps the delegate at the process's API version and refuses a base.size that is not that version's struct.
    internal WindowDelegate(bool patched)
    {
        if (patched)
        {
            var next = NativeObject.Create<cef_window_delegate_next_t>(this);
            next->is_translucent = &IsTranslucent;
            Native = &next->@base;
        }
        else Native = NativeObject.Create<cef_window_delegate_t>(this);
        Native->on_window_created = &OnWindowCreated;
        Native->on_window_destroyed = &OnWindowDestroyed;
        Native->can_close = &CanClose;

        // A missing C callback answers 0, unlike the C++ defaults (true): without these the window
        // has no traffic-light buttons and cannot be resized.
        Native->with_standard_window_buttons = &WithStandardWindowButtons;
        Native->can_resize = &CanResize;
        Native->can_minimize = &CanMinimize;
        Native->can_maximize = &CanMaximize;

        Native->on_window_activation_changed = &OnActivationChanged;
        Native->on_window_bounds_changed = &OnBoundsChanged;
        Native->on_window_fullscreen_transition = &OnFullscreenTransition;
        Native->is_frameless = &IsFrameless;
        Native->accepts_first_mouse = &AcceptsFirstMouse;
        Native->get_initial_show_state = &GetInitialShowState;
        Native->get_parent_window = &GetParentWindow;
        Native->is_window_modal_dialog = &IsWindowModalDialog;
        Native->on_accelerator = &OnAccelerator;
        Native->on_key_event = &OnKeyEvent;
        // The window delegate is also its root view's delegate, which owns size limits.
        Native->@base.@base.get_minimum_size = &GetMinimumSize;
        Native->@base.@base.get_maximum_size = &GetMaximumSize;
        Native->@base.@base.on_theme_changed = &OnThemeChanged;
    }

    public cef_window_delegate_t* Native { get; }

    /// <summary>The window's theme was applied: CEF has just cleared any background colour set on its root view.</summary>
    public Action? ThemeChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnThemeChanged(cef_view_delegate_t* self, cef_view_t* view)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        if (owner.ThemeChanged is { } callback) Guard.Run(callback);
        NativeObject.Release(view);
    }

    public bool Resizable { get; set; } = true;

    public bool Minimizable { get; set; } = true;

    public bool Maximizable { get; set; } = true;

    public WindowCallback? Created { get; init; }

    public WindowCallback? Destroyed { get; init; }

    /// <summary>Asked when the user or code closes the window; true lets it close.</summary>
    public WindowPredicate? Closing { get; init; }

    /// <summary>The window gained (true) or lost (false) activation.</summary>
    public Action<bool>? ActivationChanged { get; init; }

    /// <summary>The window moved or resized; bounds in screen DIPs.</summary>
    public Action<cef_rect_t>? BoundsChanged { get; init; }

    /// <summary>A full-screen transition started (false) or completed (true).</summary>
    public Action<bool>? FullscreenTransition { get; init; }

    /// <summary>No title bar or border.</summary>
    public bool Frameless { get; init; }

    /// <summary>A click on the inactive window also reaches the page (macOS), asked once at creation.</summary>
    public bool AcceptFirstMouse { get; init; }

    /// <summary>Created translucent (<c>Widget::InitParams::opacity</c>), so that what the window leaves clear shows
    /// the desktop behind it. Only a patched libcef asks (<see cref="CefRuntime.IsPatched"/>).</summary>
    public bool Translucent { get; init; }

    /// <summary>The window this one belongs to, asked once at creation; null for a top-level window.</summary>
    public Func<nint>? Parent { get; init; }

    /// <summary>Disables the parent while shown. Only applies with a <see cref="Parent"/>.</summary>
    public bool Modal { get; init; }

    /// <summary>An accelerator registered with <c>set_accelerator</c> fired: its command id; true if handled.</summary>
    public Func<int, bool>? Accelerator { get; init; }

    /// <summary>A key event nothing else in the window handled; true if handled.</summary>
    public KeyEventPredicate? KeyEvent { get; init; }

    /// <summary>How the window first appears.</summary>
    public cef_show_state_t InitialShowState { get; init; } = cef_show_state_t.CEF_SHOW_STATE_NORMAL;

    /// <summary>The smallest content size, or 0×0 for none.</summary>
    public cef_size_t MinimumSize { get; set; }

    /// <summary>The largest content size, or 0×0 for none.</summary>
    public cef_size_t MaximumSize { get; set; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnActivationChanged(cef_window_delegate_t* self, cef_window_t* window, int active)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        if (owner.ActivationChanged is { } callback) Guard.Run(() => callback(active != 0));
        NativeObject.Release(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBoundsChanged(cef_window_delegate_t* self, cef_window_t* window, cef_rect_t* bounds)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        var value = *bounds;
        if (owner.BoundsChanged is { } callback) Guard.Run(() => callback(value));
        NativeObject.Release(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFullscreenTransition(cef_window_delegate_t* self, cef_window_t* window, int isCompleted)
    {
        var owner = NativeObject.Owner<WindowDelegate>(self);
        if (owner.FullscreenTransition is { } callback) Guard.Run(() => callback(isCompleted != 0));
        NativeObject.Release(window);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsFrameless(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).Frameless ? 1 : 0;
    }

    // Disabled rather than default: CEF's default follows --accept-first-mouse, which Electron does not read.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_state_t AcceptsFirstMouse(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).AcceptFirstMouse ? cef_state_t.STATE_ENABLED : cef_state_t.STATE_DISABLED;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsTranslucent(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).Translucent ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_show_state_t GetInitialShowState(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).InitialShowState;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_window_t* GetParentWindow(cef_window_delegate_t* self, cef_window_t* window, int* isMenu, int* canActivateMenu)
    {
        NativeObject.Release(window);
        var owner = NativeObject.Owner<WindowDelegate>(self);
        var parent = owner.Parent is { } get ? (cef_window_t*)Guard.Run(get, 0) : null;
        if (parent is not null) NativeObject.AddRef(parent);
        return parent;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnAccelerator(cef_window_delegate_t* self, cef_window_t* window, int commandId)
    {
        NativeObject.Release(window);
        var owner = NativeObject.Owner<WindowDelegate>(self);
        return owner.Accelerator is { } callback && Guard.Run(() => callback(commandId), false) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnKeyEvent(cef_window_delegate_t* self, cef_window_t* window, cef_key_event_t* keyEvent)
    {
        NativeObject.Release(window);
        var owner = NativeObject.Owner<WindowDelegate>(self);
        return owner.KeyEvent is { } callback && Guard.Run(() => callback(keyEvent), false) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsWindowModalDialog(cef_window_delegate_t* self, cef_window_t* window)
    {
        NativeObject.Release(window);
        return NativeObject.Owner<WindowDelegate>(self).Modal ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_size_t GetMinimumSize(cef_view_delegate_t* self, cef_view_t* view)
    {
        NativeObject.Release(view);
        return NativeObject.Owner<WindowDelegate>(self).MinimumSize;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_size_t GetMaximumSize(cef_view_delegate_t* self, cef_view_t* view)
    {
        NativeObject.Release(view);
        return NativeObject.Owner<WindowDelegate>(self).MaximumSize;
    }

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
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in a DevTools callback: {e}"); }
        NativeObject.Release(browser);
        return 1;
    }
}
