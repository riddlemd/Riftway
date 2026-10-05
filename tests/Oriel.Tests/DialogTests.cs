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
    public void CheckCefSaveOptions_RejectsWhatCefCannotDo()
    {
        Dialog.CheckCefSaveOptions(new SaveDialogOptions { Title = "t", DefaultPath = "p" });
        Assert.Throws<NotSupportedException>(() => Dialog.CheckCefSaveOptions(new SaveDialogOptions { ButtonLabel = "Keep" }));
        Assert.Throws<NotSupportedException>(() => Dialog.CheckCefSaveOptions(new SaveDialogOptions { Message = "m" }));
        Assert.Throws<NotSupportedException>(() => Dialog.CheckCefSaveOptions(new SaveDialogOptions { NameFieldLabel = "n" }));
        Assert.Throws<NotSupportedException>(() => Dialog.CheckCefSaveOptions(new SaveDialogOptions { ShowsTagField = true }));
        Assert.Throws<NotSupportedException>(() => Dialog.CheckCefSaveOptions(new SaveDialogOptions { Properties = SaveDialogProperties.ShowHiddenFiles }));
    }

    [Fact]
    public void ShowOpenDialogAsync_NeedsTheAppReady()
        => Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => { _ = Dialog.ShowOpenDialogAsync(new OpenDialogOptions()); }).Message, StringComparison.Ordinal);

    [Fact]
    public void Windows_AcceptsWhatElectronAcceptsThere()
    {
        if (!OperatingSystem.IsWindows()) return;
        var open = new OpenDialogOptions
        {
            ButtonLabel = "Pick",
            Message = "m",
            Properties = OpenDialogProperties.OpenFile | OpenDialogProperties.OpenDirectory | OpenDialogProperties.MultiSelections
                | OpenDialogProperties.CreateDirectory | OpenDialogProperties.NoResolveAliases | OpenDialogProperties.TreatPackageAsDirectory,
        };
        var save = new SaveDialogOptions { ButtonLabel = "Keep", Message = "m", NameFieldLabel = "n", ShowsTagField = true, Properties = (SaveDialogProperties)31 };
        // Not ready yet, so the only refusal left is the app's state, as Electron's checkAppInitialized.
        Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => { _ = Dialog.ShowOpenDialogAsync(open); }).Message, StringComparison.Ordinal);
        Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => { _ = Dialog.ShowSaveDialogAsync(save); }).Message, StringComparison.Ordinal);
        Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => Dialog.ShowOpenDialogSync(null, open)).Message, StringComparison.Ordinal);
        Assert.Contains("Ready", Assert.Throws<InvalidOperationException>(() => Dialog.ShowSaveDialogSync(null, save)).Message, StringComparison.Ordinal);
    }

    private const uint OpenBase = Fos.ForceFileSystem | Fos.FileMustExist;
    private const uint SaveBase = Fos.ForceFileSystem | Fos.PathMustExist | Fos.OverwritePrompt;

    [Theory]
    [InlineData(OpenDialogProperties.None, OpenBase)]
    [InlineData(OpenDialogProperties.OpenFile, OpenBase)]
    [InlineData(OpenDialogProperties.OpenDirectory, OpenBase | Fos.PickFolders)]
    [InlineData(OpenDialogProperties.OpenFile | OpenDialogProperties.OpenDirectory, OpenBase | Fos.PickFolders)]
    [InlineData(OpenDialogProperties.MultiSelections, OpenBase | Fos.AllowMultiSelect)]
    [InlineData(OpenDialogProperties.ShowHiddenFiles, OpenBase | Fos.ForceShowHidden)]
    [InlineData(OpenDialogProperties.PromptToCreate, OpenBase | Fos.CreatePrompt)]
    [InlineData(OpenDialogProperties.DontAddToRecent, OpenBase | Fos.DontAddToRecent)]
    [InlineData(OpenDialogProperties.CreateDirectory | OpenDialogProperties.NoResolveAliases | OpenDialogProperties.TreatPackageAsDirectory, OpenBase)]
    public void OpenOptionsOf_SetsElectronsFlags(OpenDialogProperties properties, uint options)
        => Assert.Equal($"{options:X}", $"{Dialog.OpenOptionsOf(properties):X}");

    [Theory]
    [InlineData(SaveDialogProperties.None, SaveBase)]
    [InlineData(SaveDialogProperties.ShowHiddenFiles, SaveBase | Fos.ForceShowHidden)]
    [InlineData(SaveDialogProperties.DontAddToRecent, SaveBase | Fos.DontAddToRecent)]
    [InlineData(SaveDialogProperties.CreateDirectory | SaveDialogProperties.TreatPackageAsDirectory | SaveDialogProperties.ShowOverwriteConfirmation, SaveBase)]
    public void SaveOptionsOf_SetsElectronsFlags(SaveDialogProperties properties, uint options)
        => Assert.Equal($"{options:X}", $"{Dialog.SaveOptionsOf(properties):X}");

    [Fact]
    public void FileTypesOf_NoFiltersIsAllFiles()
        => Assert.Equal([("All Files (*.*)", "*.*")], Dialog.FileTypesOf([]));

    [Fact]
    public void FileTypesOf_PrefixesEachExtensionAsGiven()
        => Assert.Equal([("Images", "*.PNG;*..jpg"), ("Any", "*.*"), ("None", "*.*"), ("Mixed", "*.*;*.txt")],
            Dialog.FileTypesOf([new FileFilter("Images", "PNG", ".jpg"), new FileFilter("Any", "*"), new FileFilter("None"), new FileFilter("Mixed", "*", "txt")]));

    [Fact]
    public void DefaultExtensionOf_TakesTheFirstTypeThatIsNotAllFiles()
    {
        Assert.Equal((2, "png"), Dialog.DefaultExtensionOf([("Any", "*.*"), ("Images", "*.png;*.jpg"), ("Text", "*.txt")]));
        Assert.Equal((1, "txt"), Dialog.DefaultExtensionOf([("Text", "*.txt")]));
        Assert.Equal((1, "*"), Dialog.DefaultExtensionOf([("Mixed", "*.*;*.txt")]));
        Assert.Equal((0, (string?)null), Dialog.DefaultExtensionOf([("Any", "*.*"), ("All", "*.*")]));
    }

    [Fact]
    public void DefaultPathOf_SplitsLikeElectron()
    {
        // Windows path rules: other platforms never take this path.
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("oriel-dialog-unit-").FullName;
        try
        {
            var file = Path.Combine(dir, "real.txt");
            File.WriteAllText(file, "");
            var calls = 0;
            string Fallback() { calls++; return dir; }

            Assert.Equal((string.Empty, dir), Dialog.DefaultPathOf(dir, Fallback));
            Assert.Equal(("real.txt", dir), Dialog.DefaultPathOf(file, Fallback));
            Assert.Equal(("new.txt", dir), Dialog.DefaultPathOf(Path.Combine(dir, "new.txt"), Fallback));
            var missing = Path.Combine(dir, "missing") + Path.DirectorySeparatorChar;
            Assert.Equal((string.Empty, missing), Dialog.DefaultPathOf(missing, Fallback));
            Assert.Equal(("b.txt", (string?)null), Dialog.DefaultPathOf(Path.Combine("nowhere-a", "b.txt"), Fallback));
            Assert.Equal(0, calls);
            Assert.Equal((string.Empty, dir), Dialog.DefaultPathOf(null, Fallback));
            Assert.Equal((string.Empty, dir), Dialog.DefaultPathOf("", Fallback));
            Assert.Equal(2, calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(@"C:\a\b.txt", true, @"C:\a", "b.txt")]
    [InlineData(@"C:\b.txt", true, @"C:\", "b.txt")]
    [InlineData(@"C:/a/b", true, @"C:/a", "b")]
    [InlineData(@"C:\dir\\", true, @"C:\", "dir")]
    [InlineData(@"C:\", true, @"C:\", @"\")]
    [InlineData(@"C:a.txt", false, "C:", "a.txt")]
    [InlineData(@"C:", false, "C:", "")]
    [InlineData(@"\\server\share\x", true, @"\\server\share", "x")]
    [InlineData(@"\\server", true, @"\\", "server")]
    [InlineData(@"\\", true, @"\\", @"\\")]
    [InlineData(@"\\\", true, @"\", @"\")]
    [InlineData(@"\a", false, @"\", "a")]
    [InlineData(@"a.txt", false, ".", "a.txt")]
    [InlineData(@"a\b\", false, "a", "b")]
    [InlineData(@"1:\x", false, @"1:", "x")]
    public void ChromiumPath_MatchesBaseFilePath(string path, bool absolute, string dirName, string baseName)
    {
        Assert.Equal(absolute, ChromiumPath.IsAbsolute(path));
        Assert.Equal(dirName, ChromiumPath.DirName(path));
        Assert.Equal(baseName, ChromiumPath.BaseName(path));
    }
}
