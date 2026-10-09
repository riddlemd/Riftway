using System.Text;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

// CEF launches this exe for every renderer, GPU and utility process; a separate NativeAOT binary
// keeps the app's own entry point and the .NET JIT out of them. (macOS only: Linux needs the
// single-threaded native subprocess for its zygote.)
CefRuntime.Load(CefLayout.SubprocessLibrary());

// Without the browser's custom schemes registered here too, origins on them fail to cross IPC.
var schemes = SchemeSwitch.FromArgs(args);
var app = new AppHandler(customSchemes: schemes, renderProcess: new RenderProcessHandler(CrashKeys.SetFromCommandLine));

return Run(app);

static unsafe int Run(AppHandler app)
{
    var args = CefLayout.MainArgs();
    return CefNative.cef_execute_process((cef_main_args_t*)&args, app.Native, null);
}

/// <summary>CrashReporter's global annotations for this child: the hex of UTF-8 "key\0value\0..." in
/// <c>--oriel-crash-keys</c> (CrashReporterConfig.EncodePairs).</summary>
internal static unsafe class CrashKeys
{
    // cef_set_crash_key_value does nothing until cef_execute_process has started crash reporting, and a
    // renderer's WebKit start is the first callback after that.
    public static void SetFromCommandLine()
    {
        var line = CefNative.cef_command_line_get_global();
        if (line == null) return;
        cef_string_utf16_t name = default;
        CefString.Set(&name, "oriel-crash-keys");
        var hex = CefString.ReadUserFree(line->get_switch_value(line, &name));
        CefString.Clear(&name);
        NativeObject.Release(line);
        if (string.IsNullOrEmpty(hex)) return;

        var parts = Encoding.UTF8.GetString(Convert.FromHexString(hex)).Split('\0');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            cef_string_utf16_t key = default;
            cef_string_utf16_t value = default;
            CefString.Set(&key, parts[i]);
            CefString.Set(&value, parts[i + 1]);
            CefNative.cef_set_crash_key_value(&key, &value);
            CefString.Clear(&key);
            CefString.Clear(&value);
        }
    }
}
