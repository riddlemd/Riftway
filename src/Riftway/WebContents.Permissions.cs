using System.Text.Json;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

// Outside the unsafe part: C# does not allow await in an unsafe context.
public sealed partial class WebContents
{
    private const string PermissionCheckBinding = "__riftwayPermissionCheck";
    private static readonly string s_permissionCheckScript = ReadResource("Riftway.Ipc.permission-check.js");
    private bool _permissionCheckWired;

    // UI thread until the first await: the frame lookup reads the frame tree.
    // Electron asks for each permission of a prompt at once; the prompt is accepted when every answer is yes.
    private async Task<bool> DecidePromptAsync(PermissionPrompt prompt)
    {
        var (url, isMainFrame) = RequestingFrameOf(prompt.Origin);
        var details = new PermissionRequestDetails(url, isMainFrame);
        var answers = await Task.WhenAll(PermissionNames.Of(prompt.Permissions)
            .Select(name => Session.DecidePermissionAsync(this, name, details))).ConfigureAwait(false);
        return answers.All(granted => granted);
    }

    private void OnPromptSettled(PermissionPrompt prompt, bool accepted)
    {
        if (accepted) return;
        var topLevel = MainFrameState()?.Url;
        // Chromium stores the refusal while the answer is delivered; this runs after it.
        CefThread.PostUi(() => Session.ForgetRefusal(prompt.Origin, topLevel, prompt.Permissions));
    }

    // UI thread until the first await. Screen capture asks the request handler for media first, then the display media
    // handler, as Electron's MediaAccessAllowed does.
    private async Task<uint> DecideMediaAsync(MediaAccessRequest request)
    {
        var details = new PermissionRequestDetails(request.FrameUrl, request.IsMainFrame)
        {
            MediaTypes = PermissionNames.MediaTypesOf(request.Media),
            SecurityOrigin = request.Origin,
        };
        var desktop = PermissionNames.DesktopOf(request.Media);
        var display = desktop == 0 ? null : new DisplayMediaRequest(FrameAt(request.FrameUrl, request.IsMainFrame) is { } frame ? WrapperOf(frame) : null,
            request.Origin,
            (desktop & (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE) != 0,
            (desktop & (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE) != 0,
            UserGesture: false);
        if (!await Session.DecidePermissionAsync(this, "media", details).ConfigureAwait(false)) return 0;
        if (display is null) return request.Media;
        var allowed = await Session.ChooseDisplayMediaAsync(display, desktop).ConfigureAwait(false);
        return allowed == 0 ? 0 : allowed | (request.Media & ~desktop);
    }

    /// <summary>UI thread, before the first navigation: Riftway's <c>navigator.permissions.query()</c>, answered by the
    /// session's check handler.</summary>
    private void AddPermissionCheckScript()
    {
        if (!_permissionCheckWired)
        {
            _permissionCheckWired = true;
            _devTools.EventReceived += OnPermissionCheckEvent;
        }
        _devTools.Send("Runtime.addBinding", w => w.WriteString("name", PermissionCheckBinding));
        _devTools.Send("Page.addScriptToEvaluateOnNewDocument", w => w.WriteString("source", s_permissionCheckScript));
    }

    private void OnPermissionCheckEvent(string method, JsonElement parameters)
    {
        if (method != "Runtime.bindingCalled" || parameters.GetProperty("name").GetString() != PermissionCheckBinding) return;
        var context = parameters.GetProperty("executionContextId").GetInt32();
        int id;
        string? state;
        try
        {
            using var payload = JsonDocument.Parse(parameters.GetProperty("payload").GetString()!);
            var call = payload.RootElement;
            id = call.GetProperty("id").GetInt32();
            state = AnswerCheck(context, call);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return;
        }
        _devTools.Send("Runtime.evaluate", w =>
        {
            w.WriteString("expression", $"globalThis[Symbol.for('riftway.permissionCheck')]?.({id}, {(state is null ? "null" : $"'{state}'")})");
            w.WriteNumber("contextId", context);
        });
    }

    // The frame comes from the context when Riftway knows it; the page's own word is the fallback.
    private string? AnswerCheck(int context, JsonElement call)
    {
        var query = new PermissionQuery(call.GetProperty("name").GetString()!,
            call.GetProperty("sysex").GetBoolean(), call.GetProperty("allowWithoutSanitization").GetBoolean());
        if (PermissionNames.ForQuery(query) is not { } mapped) return null;
        var frame = _contextFrames.TryGetValue(context, out var frameId) && _frames.TryGetValue(frameId, out var known) ? known : null;
        var url = frame?.Url ?? call.GetProperty("url").GetString()!;
        var origin = frame?.Origin ?? (Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.GetLeftPart(UriPartial.Authority) : "null");
        var details = new PermissionCheckDetails(url, frame is null ? call.GetProperty("mainFrame").GetBoolean() : frame.Id == _mainFrameId)
        {
            EmbeddingOrigin = MainFrameState() is { } main ? PermissionNames.OriginUrl(main.Origin) : null,
            MediaType = mapped.MediaType,
        };
        return Session.CheckPermission(this, mapped.Name, PermissionNames.OriginUrl(origin), details) ? "granted" : "denied";
    }

    private FrameState? MainFrameState() => _mainFrameId is { } id && _frames.TryGetValue(id, out var main) ? main : null;

    // CEF's prompt names only the origin: the main frame when it is that origin, else a frame that is.
    private (string Url, bool IsMainFrame) RequestingFrameOf(string origin)
    {
        var target = PermissionNames.OriginUrl(origin);
        if (MainFrameState() is { } main && PermissionNames.OriginUrl(main.Origin) == target) return (main.Url, true);
        foreach (var frame in _frames.Values)
            if (!frame.Detached && PermissionNames.OriginUrl(frame.Origin) == target) return (frame.Url, false);
        return (origin, false);
    }

    private FrameState? FrameAt(string url, bool isMainFrame)
        => isMainFrame ? MainFrameState() : _frames.Values.FirstOrDefault(f => !f.Detached && f.Id != _mainFrameId && f.Url == url);
}
