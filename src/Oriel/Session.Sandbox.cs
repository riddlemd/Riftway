using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>How <see cref="Session.FromPartition(string, SessionOptions)"/> or
/// <see cref="Session.FromPath(string, SessionOptions)"/> creates a session.</summary>
public sealed record SessionOptions
{
    /// <summary>Whether the session's renderers run in Chromium's sandbox when the app's children do
    /// (<see cref="OrielApp.IsSandboxed"/>). False runs every renderer of the session unsandboxed: Electron's
    /// <c>webPreferences.sandbox: false</c>, which Oriel decides per session rather than per window (drift W13).</summary>
    /// <remarks>False needs Oriel's CEF patch 0010 while the app is sandboxed: stock CEF sandboxes every renderer or
    /// none.</remarks>
    public bool Sandbox { get; init; } = true;
}

public sealed unsafe partial class Session
{
    /// <summary>Whether the session's renderers run sandboxed when the app's children do; false for a session created
    /// with <see cref="SessionOptions.Sandbox"/> false.</summary>
    public bool Sandbox { get; private init; } = true;

    // Read by the renderer launch callback on the UI thread, which must not wait for s_gate: Create holds s_gate while
    // it waits for the UI thread.
    private static nint[] s_unsandboxed = [];
    private static readonly Lock s_unsandboxedGate = new();

    private static void AddUnsandboxed(cef_request_context_t* context)
    {
        lock (s_unsandboxedGate) Volatile.Write(ref s_unsandboxed, [.. s_unsandboxed, (nint)context]);
    }

    /// <summary>Whether a session may be created with <paramref name="sandbox"/>.</summary>
    /// <exception cref="PlatformNotSupportedException">An unsandboxed session in a sandboxed app without patch 0010.</exception>
    internal static void CheckSandboxOption(bool sandbox, bool patched, bool appSandboxed)
    {
        if (!sandbox && appSandboxed && !patched)
            throw new PlatformNotSupportedException(
                "SessionOptions.Sandbox = false needs Oriel's CEF patch 0010: stock CEF sandboxes every renderer or none (drift W13).");
    }

    /// <summary>The error for a window whose <see cref="WebPreferences.Sandbox"/> contradicts its session's; null when
    /// they agree or the window asks nothing.</summary>
    internal static string? SandboxMismatch(bool? requested, bool sessionSandbox) => requested switch
    {
        false when sessionSandbox => "webPreferences.sandbox false needs a session created with SessionOptions.Sandbox = false: Oriel decides the sandbox per session (drift W13).",
        true when !sessionSandbox => "webPreferences.sandbox true cannot use a session created with SessionOptions.Sandbox = false.",
        _ => null,
    };

    /// <summary>The error for asking an existing session for other options; null when they agree.</summary>
    internal static string? OptionsMismatch(bool existingSandbox, SessionOptions? options)
        => options is not null && options.Sandbox != existingSandbox
            ? $"The session already exists with SessionOptions.Sandbox = {(existingSandbox ? "true" : "false")}."
            : null;

    /// <summary>Patch 0010, UI thread: a renderer of an unsandboxed session's profile launches without Chromium's sandbox.
    /// On Linux it also skips the zygote, which sandboxes what it forks, as Electron does.</summary>
    internal static void OnBeforeRenderProcessLaunch(cef_command_line_t* commandLine, cef_request_context_t* context)
        => OnBeforeRenderProcessLaunch(commandLine, context, Volatile.Read(ref s_unsandboxed), OperatingSystem.IsLinux());

    internal static void OnBeforeRenderProcessLaunch(cef_command_line_t* commandLine, cef_request_context_t* context, nint[] unsandboxed,
        bool linux)
    {
        if (!IsUnsandboxed(context, unsandboxed)) return;
        AppendSwitch(commandLine, "no-sandbox");
        if (linux) AppendSwitch(commandLine, "no-zygote");
    }

    // A session made from a partition or path is its profile's only CEF context, so 0010 hands back that same object;
    // is_sharing_with cannot match two initialized contexts of one in-memory profile.
    internal static bool IsUnsandboxed(cef_request_context_t* context, nint[] unsandboxed)
    {
        foreach (var native in unsandboxed)
        {
            var session = (cef_request_context_t*)native;
            // CEF unwraps the argument by taking over the caller's reference.
            NativeObject.AddRef(context);
            if (session->is_sharing_with(session, context) != 0) return true;
        }
        return false;
    }

    // CEF copies a const string argument, so a pinned managed string needs no CEF allocation.
    private static void AppendSwitch(cef_command_line_t* commandLine, string name)
    {
        fixed (char* chars = name)
        {
            var value = new cef_string_utf16_t { str = chars, length = (nuint)name.Length };
            commandLine->append_switch(commandLine, &value);
        }
    }
}
