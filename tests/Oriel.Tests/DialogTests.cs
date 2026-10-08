using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class DialogTests
{
    [Theory]
    [InlineData(OpenDialogProperties.None, cef_file_dialog_mode_t.FILE_DIALOG_OPEN)]
    [InlineData(OpenDialogProperties.OpenFile, cef_file_dialog_mode_t.FILE_DIALOG_OPEN)]
    [InlineData(OpenDialogProperties.OpenFile | OpenDialogProperties.MultiSelections, cef_file_dialog_mode_t.FILE_DIALOG_OPEN_MULTIPLE)]
    [InlineData(OpenDialogProperties.MultiSelections, cef_file_dialog_mode_t.FILE_DIALOG_OPEN_MULTIPLE)]
    [InlineData(OpenDialogProperties.OpenDirectory, cef_file_dialog_mode_t.FILE_DIALOG_OPEN_FOLDER)]
    public void OpenModeOf_MapsProperties(OpenDialogProperties properties, cef_file_dialog_mode_t mode)
        => Assert.Equal(mode, Dialog.OpenModeOf(new OpenDialogOptions { Properties = properties }));

    [Theory]
    [InlineData(OpenDialogProperties.OpenDirectory | OpenDialogProperties.OpenFile)]
    [InlineData(OpenDialogProperties.OpenDirectory | OpenDialogProperties.MultiSelections)]
    [InlineData(OpenDialogProperties.OpenFile | OpenDialogProperties.ShowHiddenFiles)]
    [InlineData(OpenDialogProperties.DontAddToRecent)]
    public void OpenModeOf_RejectsWhatCefCannotDo(OpenDialogProperties properties)
        => Assert.Throws<NotSupportedException>(() => Dialog.OpenModeOf(new OpenDialogOptions { Properties = properties }));

    [Fact]
    public void OpenModeOf_RejectsButtonLabelAndMessage()
    {
        Assert.Throws<NotSupportedException>(() => Dialog.OpenModeOf(new OpenDialogOptions { ButtonLabel = "Pick" }));
        Assert.Throws<NotSupportedException>(() => Dialog.OpenModeOf(new OpenDialogOptions { Message = "Choose a file" }));
    }

    [Fact]
    public void AcceptFiltersOf_FormatsNameAndDottedLowercaseExtensions()
        => Assert.Equal(["Images|.png;.jpg", "Text|.txt"],
            Dialog.AcceptFiltersOf([new FileFilter("Images", "PNG", ".jpg"), new FileFilter("Text", "txt")]));

    [Fact]
    public void AcceptFiltersOf_DropsAnyFileAndEmptyFilters()
        => Assert.Equal(["Text|.txt"],
            Dialog.AcceptFiltersOf([new FileFilter("All Files", "*"), new FileFilter("Nothing"), new FileFilter("Text", "txt")]));

    [Fact]
    public void RejectCefUnsupported_RejectsWhatCefCannotShow()
    {
        Assert.Throws<NotSupportedException>(() => Dialog.RejectCefUnsupported(new SaveDialogOptions { ButtonLabel = "Keep" }));
        Assert.Throws<NotSupportedException>(() => Dialog.RejectCefUnsupported(new SaveDialogOptions { Message = "m" }));
        Assert.Throws<NotSupportedException>(() => Dialog.RejectCefUnsupported(new SaveDialogOptions { NameFieldLabel = "n" }));
        Assert.Throws<NotSupportedException>(() => Dialog.RejectCefUnsupported(new SaveDialogOptions { Properties = SaveDialogProperties.ShowHiddenFiles }));
    }

    [Fact]
    public void CefUnsupportedOf_NamesWhatCefCannotShow_ButNotTheMacOnlyTagsOrBookmarks()
        => Assert.Equal(["ButtonLabel", "Message", "NameFieldLabel", "ShowHiddenFiles"], Dialog.CefUnsupportedOf(new SaveDialogOptions
        {
            ButtonLabel = "Keep", Message = "m", NameFieldLabel = "n", Properties = SaveDialogProperties.ShowHiddenFiles,
            ShowsTagField = true, SecurityScopedBookmarks = true,
        }));

    [Fact]
    public void SaveDialogOptions_ShowsTheTagFieldByDefault_AsElectron()
        => Assert.True(new SaveDialogOptions().ShowsTagField);

    [Fact]
    public void ShowOpenDialogAsync_NeedsTheAppReady()
        => Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => { _ = Dialog.ShowOpenDialogAsync(new OpenDialogOptions()); }).Message, StringComparison.Ordinal);

    private static readonly HashSet<string> s_directories = ["/Users/u/Downloads", "/Users/u/docs", "docs"];

    [Theory]
    [InlineData("/Users/u/docs", "/Users/u/docs", null)]
    [InlineData("docs", "docs", null)]
    [InlineData("/Users/u/docs/report.txt", "/Users/u/docs", "report.txt")]
    [InlineData("/Users/u/docs/new/", "/Users/u/docs", "new")]
    [InlineData("/Users/u//docs//report.txt", "/Users/u//docs", "report.txt")]
    [InlineData("/report.txt", "/", "report.txt")]
    [InlineData("/", "/", "/")]
    [InlineData("report.txt", null, "report.txt")]
    [InlineData("sub/report.txt", null, "report.txt")]
    [InlineData(null, "/Users/u/Downloads", null)]
    [InlineData("", "/Users/u/Downloads", null)]
    public void MacDefaultPathOf_SplitsAsElectron(string? defaultPath, string? directory, string? fileName)
        => Assert.Equal((directory, fileName), Dialog.MacDefaultPathOf(defaultPath, s_directories.Contains, () => "/Users/u/Downloads"));

    [Fact]
    public void MacDefaultPathOf_MissingFallback_SplitsTheEmptyPath_AsElectron()
        => Assert.Equal((".", ""), Dialog.MacDefaultPathOf("", s_directories.Contains, () => "/nowhere"));

    [Fact]
    public void MacDefaultPathOf_NoFallback_SetsNothing()
        => Assert.Equal((null, null), Dialog.MacDefaultPathOf(null, s_directories.Contains, () => ""));

    [Fact]
    public void MacFiltersOf_KeepsTheLastExtensionPart_DroppingDuplicatesInOrder()
    {
        var filters = Dialog.MacFiltersOf([new FileFilter("Images", "PNG", ".jpg", "pkg.tar.gz", "png", "jpg"), new FileFilter("All", "*"), new FileFilter("None")]);
        Assert.Equal(["Images", "All", "None"], filters.Select(f => f.Name));
        Assert.Equal(["PNG", "jpg", "gz", "png"], filters[0].Types);
        Assert.Equal(["*"], filters[1].Types);
        Assert.Empty(filters[2].Types);
    }

    [Fact]
    public void AllowedTypesOf_AllowsEveryTypeForEmptyOrStar()
    {
        Assert.Null(MacPanelSettings.AllowedTypesOf([]));
        Assert.Null(MacPanelSettings.AllowedTypesOf(["txt", "*"]));
        Assert.Equal(["txt", "md"], MacPanelSettings.AllowedTypesOf(["txt", "md"]));
    }

    [Theory]
    [InlineData(OpenDialogProperties.OpenFile, nameof(MacPanelSettings.CanChooseFiles))]
    [InlineData(OpenDialogProperties.OpenDirectory, nameof(MacPanelSettings.CanChooseDirectories))]
    [InlineData(OpenDialogProperties.MultiSelections, nameof(MacPanelSettings.AllowsMultipleSelection))]
    [InlineData(OpenDialogProperties.ShowHiddenFiles, nameof(MacPanelSettings.ShowsHiddenFiles))]
    [InlineData(OpenDialogProperties.CreateDirectory, nameof(MacPanelSettings.CanCreateDirectories))]
    [InlineData(OpenDialogProperties.NoResolveAliases, nameof(MacPanelSettings.NoResolveAliases))]
    [InlineData(OpenDialogProperties.TreatPackageAsDirectory, nameof(MacPanelSettings.TreatsFilePackagesAsDirectories))]
    [InlineData(OpenDialogProperties.PromptToCreate, null)]
    [InlineData(OpenDialogProperties.DontAddToRecent, null)]
    [InlineData(OpenDialogProperties.None, null)]
    public void MacOpenSettingsOf_SetsExactlyThePropertysPanelFlag(OpenDialogProperties property, string? flag)
    {
        var settings = Dialog.MacOpenSettingsOf(new OpenDialogOptions { Properties = property }, _ => false, () => "");
        Assert.Equal(flag is null ? [] : [flag], SetFlags(settings));
    }

    [Theory]
    [InlineData(SaveDialogProperties.CreateDirectory, nameof(MacPanelSettings.CanCreateDirectories))]
    [InlineData(SaveDialogProperties.ShowHiddenFiles, nameof(MacPanelSettings.ShowsHiddenFiles))]
    [InlineData(SaveDialogProperties.TreatPackageAsDirectory, nameof(MacPanelSettings.TreatsFilePackagesAsDirectories))]
    [InlineData(SaveDialogProperties.ShowOverwriteConfirmation, null)]
    [InlineData(SaveDialogProperties.DontAddToRecent, null)]
    public void MacSaveSettingsOf_SetsExactlyThePropertysPanelFlag(SaveDialogProperties property, string? flag)
    {
        var settings = Dialog.MacSaveSettingsOf(new SaveDialogOptions { Properties = property }, _ => false, () => "", async: false);
        Assert.Equal(flag is null ? [] : [flag], SetFlags(settings));
    }

    [Fact]
    public void MacOpenSettingsOf_CopiesTextAndKeepsTheTagField()
    {
        var settings = Dialog.MacOpenSettingsOf(new OpenDialogOptions { Title = "T", Message = "M", ButtonLabel = "B", DefaultPath = "/a/b.txt", Filters = [new("Text", ".txt")] },
            _ => false, () => "");
        Assert.Equal(("T", "M", "B", "", true, "/a", "b.txt"), (settings.Title, settings.Message, settings.ButtonLabel, settings.NameFieldLabel, settings.ShowsTagField, settings.Directory, settings.FileName));
        Assert.Equal(["txt"], Assert.Single(settings.Filters).Types);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MacSaveSettingsOf_CopiesText_AndOnlyAsyncHidesExtensions(bool async)
    {
        var settings = Dialog.MacSaveSettingsOf(new SaveDialogOptions { Title = "T", Message = "M", ButtonLabel = "B", NameFieldLabel = "N", ShowsTagField = false },
            _ => false, () => "", async);
        Assert.Equal(("T", "M", "B", "N", false, async), (settings.Title, settings.Message, settings.ButtonLabel, settings.NameFieldLabel, settings.ShowsTagField, settings.CanSelectHiddenExtension));
    }

    private static string[] SetFlags(MacPanelSettings settings) =>
    [
        .. new (string Name, bool Set)[]
        {
            (nameof(MacPanelSettings.CanChooseFiles), settings.CanChooseFiles),
            (nameof(MacPanelSettings.CanChooseDirectories), settings.CanChooseDirectories),
            (nameof(MacPanelSettings.AllowsMultipleSelection), settings.AllowsMultipleSelection),
            (nameof(MacPanelSettings.ShowsHiddenFiles), settings.ShowsHiddenFiles),
            (nameof(MacPanelSettings.CanCreateDirectories), settings.CanCreateDirectories),
            (nameof(MacPanelSettings.NoResolveAliases), settings.NoResolveAliases),
            (nameof(MacPanelSettings.TreatsFilePackagesAsDirectories), settings.TreatsFilePackagesAsDirectories),
            (nameof(MacPanelSettings.CanSelectHiddenExtension), settings.CanSelectHiddenExtension),
        }.Where(f => f.Set).Select(f => f.Name),
    ];
}
