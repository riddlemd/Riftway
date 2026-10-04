using Oriel.Cef.Interop;

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
    public void ShowSaveDialogAsync_RejectsUnsupportedOptionsFirst()
    {
        Assert.Throws<NotSupportedException>(() => { _ = Dialog.ShowSaveDialogAsync(new SaveDialogOptions { ButtonLabel = "Keep" }); });
        Assert.Throws<NotSupportedException>(() => { _ = Dialog.ShowSaveDialogAsync(new SaveDialogOptions { Message = "m" }); });
        Assert.Throws<NotSupportedException>(() => { _ = Dialog.ShowSaveDialogAsync(new SaveDialogOptions { NameFieldLabel = "n" }); });
        Assert.Throws<NotSupportedException>(() => { _ = Dialog.ShowSaveDialogAsync(new SaveDialogOptions { ShowsTagField = true }); });
    }

    [Fact]
    public void ShowOpenDialogAsync_NeedsTheAppReady()
        => Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => { _ = Dialog.ShowOpenDialogAsync(new OpenDialogOptions()); }).Message, StringComparison.Ordinal);
}
