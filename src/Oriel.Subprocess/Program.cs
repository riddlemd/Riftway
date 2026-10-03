using Oriel.Cef;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

// CEF launches this exe for every renderer, GPU and utility process; a separate NativeAOT binary
// keeps the app's own entry point and the .NET JIT out of them.
CefRuntime.Load(CefLayout.SubprocessLibrary());

return Run();

static unsafe int Run()
{
    var args = CefLayout.MainArgs();
    return CefNative.cef_execute_process((cef_main_args_t*)&args, null, null);
}
