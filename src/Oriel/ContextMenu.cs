namespace Oriel;

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

/// <summary>Where and on what the user asked for a context menu; Electron's <c>context-menu</c> event parameters.</summary>
/// <remarks>Oriel shows no context menu of its own, as Electron: handle <see cref="WebContents.ContextMenu"/>
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

    /// <summary>Which editing commands apply.</summary>
    public ContextMenuEditFlags EditFlags { get; init; } = new(false, false, false, false, false, false, false, false);
}

/// <summary>Options for <see cref="Menu.Popup"/>; Electron's <c>menu.popup()</c> options.</summary>
public sealed record PopupOptions
{
    /// <summary>The window to show the menu in; defaults to the focused window.</summary>
    public BrowserWindow? Window { get; init; }

    /// <summary>Horizontal position in the window's page, in DIPs; with <see cref="Y"/>. Defaults to where the
    /// window's last context menu was asked for (Electron uses the pointer), else the page's top-left.</summary>
    public int? X { get; init; }

    /// <summary>Vertical position in the window's page, in DIPs; with <see cref="X"/>.</summary>
    public int? Y { get; init; }

    /// <summary>Runs on the UI thread when the menu closes, whether or not an item was chosen. Electron's <c>callback</c>.</summary>
    public Action? Closed { get; init; }
}
