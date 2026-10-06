using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Custom schemes and their handlers; Electron's <c>protocol</c>.</summary>
public static unsafe class Protocol
{
    private static readonly Lock s_gate = new();
    private static readonly List<PrivilegedScheme> s_privileged = [];
    private static readonly Dictionary<string, SchemeHandlerFactory> s_handlers = new(StringComparer.Ordinal);
    private static bool s_started;
    private static bool s_ready;

    /// <summary>Registers schemes with privileges. Must run before <see cref="OrielApp.Run"/>: every
    /// Chromium process registers them at startup. Electron's <c>protocol.registerSchemesAsPrivileged()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app has already started.</exception>
    /// <exception cref="ArgumentException">A scheme name is not a valid lowercase scheme.</exception>
    /// <exception cref="NotSupportedException">A privilege CEF cannot provide was requested.</exception>
    public static void RegisterSchemesAsPrivileged(params PrivilegedScheme[] schemes)
    {
        ArgumentNullException.ThrowIfNull(schemes);
        foreach (var scheme in schemes)
        {
            if (!SchemeSwitch.IsValidName(scheme.Scheme))
                throw new ArgumentException($"'{scheme.Scheme}' is not a valid lowercase scheme name.", nameof(schemes));
            if (scheme.Privileges.AllowServiceWorkers)
                throw new NotSupportedException("CEF does not let custom schemes register service workers.");
            if (scheme.Privileges.CodeCache)
                throw new NotSupportedException("CEF does not expose V8's code cache for custom schemes.");
        }

        lock (s_gate)
        {
            if (s_started) throw new InvalidOperationException("Register privileged schemes before OrielApp.Run().");
            s_privileged.AddRange(schemes);
        }
    }

    /// <summary>Serves requests to <paramref name="scheme"/>. Electron's <c>protocol.handle()</c>.</summary>
    /// <remarks>The handler runs on a thread-pool thread; <paramref name="handler"/>'s token is cancelled when
    /// the request is abandoned. Built-in schemes (http, https) can be intercepted too.</remarks>
    /// <exception cref="InvalidOperationException">The scheme is already handled.</exception>
    public static void Handle(string scheme, Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(handler);

        var factory = new SchemeHandlerFactory(handler);
        lock (s_gate)
        {
            if (!s_handlers.TryAdd(scheme, factory)) throw new InvalidOperationException($"'{scheme}' is already handled.");
            if (s_ready) RegisterFactory(scheme, factory);
        }
    }

    /// <summary>Serves requests to <paramref name="scheme"/> synchronously.</summary>
    /// <exception cref="InvalidOperationException">The scheme is already handled.</exception>
    public static void Handle(string scheme, Func<ProtocolRequest, ProtocolResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Handle(scheme, (request, _) => Task.FromResult(handler(request)));
    }

    /// <summary>Stops serving <paramref name="scheme"/>. Electron's <c>protocol.unhandle()</c>.</summary>
    public static void Unhandle(string scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        lock (s_gate)
        {
            if (s_handlers.Remove(scheme) && s_ready) RegisterFactory(scheme, null);
        }
    }

    /// <summary>Whether <paramref name="scheme"/> has a handler. Electron's <c>protocol.isProtocolHandled()</c>.</summary>
    public static bool IsProtocolHandled(string scheme)
    {
        lock (s_gate) return s_handlers.ContainsKey(scheme);
    }

    /// <summary>The privileged schemes as CEF registration flags; closes registration.</summary>
    internal static IReadOnlyList<CustomScheme> TakePrivilegedSchemes()
    {
        lock (s_gate)
        {
            s_started = true;
            return [.. s_privileged.Select(s => new CustomScheme(s.Scheme, ToCefOptions(s.Privileges))), SyncIpc.Registration];
        }
    }

    /// <summary>CEF is initialised: handlers added before now are registered.</summary>
    internal static void OnContextInitialized()
    {
        lock (s_gate)
        {
            s_ready = true;
            RegisterFactory(SyncIpc.Scheme, SyncIpc.Factory);
            foreach (var (scheme, factory) in s_handlers) RegisterFactory(scheme, factory);
        }
    }

    internal static int ToCefOptions(SchemePrivileges privileges)
    {
        var options = cef_scheme_options_t.CEF_SCHEME_OPTION_NONE;
        if (privileges.Standard) options |= cef_scheme_options_t.CEF_SCHEME_OPTION_STANDARD;
        if (privileges.Secure) options |= cef_scheme_options_t.CEF_SCHEME_OPTION_SECURE;
        if (privileges.BypassCsp) options |= cef_scheme_options_t.CEF_SCHEME_OPTION_CSP_BYPASSING;
        if (privileges.CorsEnabled) options |= cef_scheme_options_t.CEF_SCHEME_OPTION_CORS_ENABLED;
        if (privileges.SupportFetchApi) options |= cef_scheme_options_t.CEF_SCHEME_OPTION_FETCH_ENABLED;
        return (int)options;
    }

    // A null factory unregisters the scheme.
    private static void RegisterFactory(string scheme, SchemeHandlerFactory? factory)
    {
        cef_string_utf16_t name = default;
        cef_string_utf16_t domain = default;
        CefString.Set(&name, scheme);
        if (factory is not null) NativeObject.AddRef(factory.Native);
        if (CefNative.cef_register_scheme_handler_factory(&name, &domain, factory is null ? null : factory.Native) == 0)
            Console.Error.WriteLine($"[Oriel] CEF refused a handler for scheme '{scheme}'.");
        CefString.Clear(&name);
    }
}
