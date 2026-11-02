namespace Riftway;

/// <summary>What the page offers at the point of a context menu; Electron's <c>MediaType</c> values.</summary>
public enum ContextMenuMediaType
{
    /// <summary>No media.</summary>
    None,

    /// <summary>An image.</summary>
    Image,

    /// <summary>A video.</summary>
    Video,

    /// <summary>An audio element.</summary>
    Audio,

    /// <summary>A canvas.</summary>
    Canvas,

    /// <summary>A file.</summary>
    File,

    /// <summary>A plugin.</summary>
    Plugin,
}

/// <summary>Which editing commands apply where the context menu opened; Electron's <c>editFlags</c>.</summary>
/// <param name="CanUndo">Undo is possible.</param>
/// <param name="CanRedo">Redo is possible.</param>
/// <param name="CanCut">There is an editable selection to cut.</param>
/// <param name="CanCopy">There is a selection to copy.</param>
/// <param name="CanPaste">Pasting is possible here.</param>
/// <param name="CanDelete">There is an editable selection to delete.</param>
/// <param name="CanSelectAll">Select All applies.</param>
/// <param name="CanEditRichly">The field takes rich text.</param>
public sealed record ContextMenuEditFlags(bool CanUndo, bool CanRedo, bool CanCut, bool CanCopy, bool CanPaste, bool CanDelete,
    bool CanSelectAll, bool CanEditRichly);

/// <summary>The state of the media element under the pointer; Electron's <c>mediaFlags</c>.</summary>
public sealed record ContextMenuMediaFlags
{
    /// <summary>The media failed to load. Electron's <c>inError</c>.</summary>
    public bool InError { get; init; }

    /// <summary>Electron's <c>isPaused</c>.</summary>
    public bool IsPaused { get; init; }

    /// <summary>Electron's <c>isMuted</c>.</summary>
    public bool IsMuted { get; init; }

    /// <summary>Electron's <c>canSave</c>.</summary>
    public bool CanSave { get; init; }

    /// <summary>Electron's <c>hasAudio</c>.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Electron's <c>isLooping</c>.</summary>
    public bool IsLooping { get; init; }

    /// <summary>The controls are showing. Electron's <c>isControlsVisible</c>.</summary>
    public bool IsControlsVisible { get; init; }

    /// <summary>Electron's <c>canToggleControls</c>.</summary>
    public bool CanToggleControls { get; init; }

    /// <summary>Electron's <c>canPrint</c>.</summary>
    public bool CanPrint { get; init; }

    /// <summary>Electron's <c>canRotate</c>.</summary>
    public bool CanRotate { get; init; }

    /// <summary>Electron's <c>canShowPictureInPicture</c>.</summary>
    public bool CanShowPictureInPicture { get; init; }

    /// <summary>Electron's <c>isShowingPictureInPicture</c>.</summary>
    public bool IsShowingPictureInPicture { get; init; }

    /// <summary>Electron's <c>canLoop</c>.</summary>
    public bool CanLoop { get; init; }

    /// <summary>The flags from CEF's <c>cef_context_menu_media_state_flags_t</c> bits.</summary>
    internal static ContextMenuMediaFlags From(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t flags)
    {
        bool Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t flag) => (flags & flag) != 0;
        return new ContextMenuMediaFlags
        {
            InError = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_IN_ERROR),
            IsPaused = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_PAUSED),
            IsMuted = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_MUTED),
            IsLooping = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_LOOP),
            CanSave = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_SAVE),
            HasAudio = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_HAS_AUDIO),
            CanToggleControls = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_TOGGLE_CONTROLS),
            IsControlsVisible = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CONTROLS),
            CanPrint = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_PRINT),
            CanRotate = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_ROTATE),
            CanShowPictureInPicture = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_PICTURE_IN_PICTURE),
            IsShowingPictureInPicture = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_PICTURE_IN_PICTURE),
            CanLoop = Has(Riftway.Cef.Interop.cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_LOOP),
        };
    }
}

/// <summary>Where and on what the user asked for a context menu; Electron's <c>context-menu</c> event parameters.</summary>
/// <remarks>Riftway shows no context menu of its own, as Electron: handle <see cref="WebContents.ContextMenu"/>
/// and call <see cref="Menu.Popup"/> to show one.</remarks>
public sealed record ContextMenuParams
{
    /// <summary>Horizontal position in the page, in DIPs.</summary>
    public int X { get; init; }

    /// <summary>Vertical position in the page, in DIPs.</summary>
    public int Y { get; init; }

    /// <summary>The link under the pointer, or empty.</summary>
    public string LinkUrl { get; init; } = string.Empty;

    /// <summary>The source of the image, video or audio under the pointer, or empty. Electron's <c>srcURL</c>.</summary>
    public string SrcUrl { get; init; } = string.Empty;

    /// <summary>The top-level page's URL.</summary>
    public string PageUrl { get; init; } = string.Empty;

    /// <summary>The URL of the frame the menu opened in.</summary>
    public string FrameUrl { get; init; } = string.Empty;

    /// <summary>The frame's character encoding.</summary>
    public string FrameCharset { get; init; } = string.Empty;

    /// <summary>The kind of media under the pointer.</summary>
    public ContextMenuMediaType MediaType { get; init; }

    /// <summary>Whether the image under the pointer has loaded contents.</summary>
    public bool HasImageContents { get; init; }

    /// <summary>The selected text, or empty.</summary>
    public string SelectionText { get; init; } = string.Empty;

    /// <summary>The title text of the element under the pointer.</summary>
    public string TitleText { get; init; } = string.Empty;

    /// <summary>Whether the menu opened on an editable field.</summary>
    public bool IsEditable { get; init; }

    /// <summary>Whether spell checking is on in that field. Electron's <c>spellcheckEnabled</c>.</summary>
    public bool SpellcheckEnabled { get; init; }

    /// <summary>The misspelled word under the pointer, or empty.</summary>
    public string MisspelledWord { get; init; } = string.Empty;

    /// <summary>Spelling suggestions for <see cref="MisspelledWord"/>.</summary>
    public IReadOnlyList<string> DictionarySuggestions { get; init; } = [];

    /// <summary>The state of the media under the pointer, if any. Electron's <c>mediaFlags</c>.</summary>
    public ContextMenuMediaFlags MediaFlags { get; init; } = new();

    /// <summary>Which editing commands apply.</summary>
    public ContextMenuEditFlags EditFlags { get; init; } = new(false, false, false, false, false, false, false, false);
}

/// <summary>Options for <see cref="Menu.Popup"/>; Electron's <c>menu.popup()</c> options.</summary>
public sealed record PopupOptions
{
    /// <summary>The window to show the menu in; defaults to the focused window.</summary>
    public BaseWindow? Window { get; init; }

    /// <summary>Horizontal position in the window's page, in DIPs; with <see cref="Y"/>. Defaults to the pointer.</summary>
    public int? X { get; init; }

    /// <summary>Vertical position in the window's page, in DIPs; with <see cref="X"/>.</summary>
    public int? Y { get; init; }

    /// <summary>Runs on the UI thread when the menu closes, whether or not an item was chosen. Electron's <c>callback</c>.</summary>
    public Action? Closed { get; init; }
}
