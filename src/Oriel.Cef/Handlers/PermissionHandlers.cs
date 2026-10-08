using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A permission prompt CEF would show: the origin asking and the permissions (CEF's bit mask).</summary>
public sealed record PermissionPrompt(string Origin, uint Permissions);

/// <summary>A camera, microphone or screen request: the frame's URL, whether it is the main frame, the origin and the
/// media wanted (CEF's bit mask).</summary>
public sealed record MediaAccessRequest(string FrameUrl, bool IsMainFrame, string Origin, uint Media);

/// <summary><c>cef_permission_handler_t</c>: answers permission prompts and media access without CEF's own UI.</summary>
public sealed unsafe class PermissionHandler
{
    public PermissionHandler()
    {
        Native = NativeObject.Create<cef_permission_handler_t>(this);
        Native->on_show_permission_prompt = &OnShowPermissionPrompt;
        Native->on_request_media_access_permission = &OnRequestMediaAccessPermission;
    }

    public cef_permission_handler_t* Native { get; }

    /// <summary>UI thread: decides a prompt; the task's result is whether it is accepted.</summary>
    public Func<PermissionPrompt, Task<bool>>? Prompt { get; init; }

    /// <summary>UI thread: a prompt's answer has reached Chromium; the flag is whether it was accepted.</summary>
    public Action<PermissionPrompt, bool>? PromptSettled { get; init; }

    /// <summary>UI thread: decides a media request; the task's result is the media allowed (CEF's mask), 0 for none.</summary>
    public Func<MediaAccessRequest, Task<uint>>? MediaAccess { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnShowPermissionPrompt(cef_permission_handler_t* self, cef_browser_t* browser, ulong promptId,
        cef_string_utf16_t* requestingOrigin, uint requestedPermissions, cef_permission_prompt_callback_t* callback)
    {
        var owner = NativeObject.Owner<PermissionHandler>(self);
        NativeObject.Release(browser);
        if (owner.Prompt is not { } decide)
        {
            NativeObject.Release(callback);
            return 0;
        }
        var request = new PermissionPrompt(CefString.Read(requestingOrigin), requestedPermissions);
        var answer = Guard.Run(() => decide(request), Task.FromResult(false));
        // The callback is kept until the answer comes, then released.
        answer.ContinueWith(t => CefThread.RunOnUi(() =>
        {
            var accepted = t.IsCompletedSuccessfully && t.Result;
            callback->cont(callback, accepted ? cef_permission_request_result_t.CEF_PERMISSION_RESULT_ACCEPT : cef_permission_request_result_t.CEF_PERMISSION_RESULT_DENY);
            NativeObject.Release(callback);
            Guard.Run(() => owner.PromptSettled?.Invoke(request, accepted));
        }), TaskScheduler.Default);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnRequestMediaAccessPermission(cef_permission_handler_t* self, cef_browser_t* browser, cef_frame_t* frame,
        cef_string_utf16_t* requestingOrigin, uint requestedPermissions, cef_media_access_callback_t* callback)
    {
        var owner = NativeObject.Owner<PermissionHandler>(self);
        var request = new MediaAccessRequest(CefString.ReadUserFree(frame->get_url(frame)), frame->is_main(frame) != 0,
            CefString.Read(requestingOrigin), requestedPermissions);
        NativeObject.Release(frame);
        NativeObject.Release(browser);
        if (owner.MediaAccess is not { } decide)
        {
            NativeObject.Release(callback);
            return 0;
        }
        var answer = Guard.Run(() => decide(request), Task.FromResult(0u));
        answer.ContinueWith(t => CefThread.RunOnUi(() =>
        {
            var allowed = t.IsCompletedSuccessfully ? t.Result : 0;
            if (allowed == 0) callback->cancel(callback);
            else callback->cont(callback, allowed);
            NativeObject.Release(callback);
        }), TaskScheduler.Default);
        return 1;
    }
}
