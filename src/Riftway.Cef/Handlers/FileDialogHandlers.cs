using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary><c>cef_run_file_dialog_callback_t</c>: the paths a file dialog returned; empty when cancelled.</summary>
public sealed unsafe class RunFileDialogCallback
{
    public RunFileDialogCallback(Action<IReadOnlyList<string>> dismissed)
    {
        Dismissed = dismissed;
        Native = NativeObject.Create<cef_run_file_dialog_callback_t>(this);
        Native->on_file_dialog_dismissed = &OnFileDialogDismissed;
    }

    public cef_run_file_dialog_callback_t* Native { get; }

    public Action<IReadOnlyList<string>> Dismissed { get; }

    // The list belongs to CEF and lives only for the call.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFileDialogDismissed(cef_run_file_dialog_callback_t* self, cef_string_list_t* filePaths)
    {
        var owner = NativeObject.Owner<RunFileDialogCallback>(self);
        var paths = new List<string>();
        if (filePaths is not null)
        {
            for (nuint i = 0; i < CefNative.cef_string_list_size(filePaths); i++)
            {
                cef_string_utf16_t path = default;
                if (CefNative.cef_string_list_value(filePaths, i, &path) != 0) paths.Add(CefString.Read(&path));
                CefString.Clear(&path);
            }
        }
        Guard.Run(() => owner.Dismissed(paths));
    }
}
