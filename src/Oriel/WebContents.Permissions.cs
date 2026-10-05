using Oriel.Cef.Handlers;

namespace Oriel;

// Outside the unsafe part: C# does not allow await in an unsafe context.
public sealed partial class WebContents
{
    // Every distinct permission in a prompt must be granted for it to be accepted.
    private async Task<bool> DecidePromptAsync(PermissionPrompt prompt)
    {
        foreach (var name in PermissionNames.Of(prompt.Permissions))
            if (!await Session.DecidePermissionAsync(this, name, new PermissionRequestDetails(prompt.Origin, true)).ConfigureAwait(false)) return false;
        return true;
    }

    private async Task<uint> DecideMediaAsync(MediaAccessRequest request)
    {
        var details = new PermissionRequestDetails(request.FrameUrl, request.IsMainFrame)
        {
            MediaTypes = PermissionNames.MediaTypesOf(request.Media),
            SecurityOrigin = request.Origin,
        };
        return await Session.DecidePermissionAsync(this, "media", details).ConfigureAwait(false) ? request.Media : 0;
    }
}
