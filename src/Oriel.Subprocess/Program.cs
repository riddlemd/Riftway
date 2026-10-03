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
var app = schemes.Count > 0 ? new AppHandler(customSchemes: schemes) : null;

return Run(app);

static unsafe int Run(AppHandler? app)
{
    var args = CefLayout.MainArgs();
    return CefNative.cef_execute_process((cef_main_args_t*)&args, app is null ? null : app.Native, null);
}
