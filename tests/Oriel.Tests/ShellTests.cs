namespace Oriel.Tests;

public sealed class ShellTests
{
    [Theory]
    [InlineData(ShortcutLinkOperation.Create, false, true)]
    [InlineData(ShortcutLinkOperation.Create, true, true)]
    [InlineData(ShortcutLinkOperation.Update, false, false)]
    [InlineData(ShortcutLinkOperation.Update, true, true)]
    [InlineData(ShortcutLinkOperation.Replace, false, false)]
    [InlineData(ShortcutLinkOperation.Replace, true, true)]
    public void MayWrite_UpdateAndReplaceNeedAnExistingShortcut(ShortcutLinkOperation operation, bool exists, bool mayWrite)
        => Assert.Equal(mayWrite, ShortcutRules.MayWrite(operation, exists));

    [Theory]
    [InlineData(ShortcutLinkOperation.Create, false)]
    [InlineData(ShortcutLinkOperation.Update, true)]
    [InlineData(ShortcutLinkOperation.Replace, false)]
    public void LoadsExisting_OnlyForUpdate(ShortcutLinkOperation operation, bool loads)
        => Assert.Equal(loads, ShortcutRules.LoadsExisting(operation));

    [Fact]
    public void Windows_MapsEveryField_BothWays()
    {
        var details = new ShortcutDetails
        {
            Target = "t", Cwd = "c", Args = "a", Description = "d", Icon = "i", IconIndex = 3, AppUserModelId = "id", ToastActivatorClsid = Guid.NewGuid(),
        };
        var windows = ShortcutRules.ToWindows(details);
        Assert.Equal(("t", "c", "a", "d", "i", (int?)3, "id"), (windows.Target, windows.WorkingDirectory, windows.Arguments, windows.Description, windows.Icon, windows.IconIndex, windows.AppUserModelId));
        Assert.Equal(details.ToastActivatorClsid, windows.ToastActivatorClsid);
        Assert.Equal(details, ShortcutRules.FromWindows(windows));
        Assert.Equal(new ShortcutDetails(), ShortcutRules.FromWindows(ShortcutRules.ToWindows(new ShortcutDetails())));
    }

    [Theory]
    [InlineData(ShortcutLinkOperation.Create)]
    [InlineData(ShortcutLinkOperation.Replace)]
    public void WriteShortcutLink_WithoutTarget_Throws_ExceptForUpdate(ShortcutLinkOperation operation)
    {
        // A unique path, so a mutant that does write cannot leave a file behind for the next run.
        var path = Path.Combine(Path.GetTempPath(), $"oriel-never-written-{Guid.NewGuid():N}.lnk");
        try
        {
            Assert.Throws<ArgumentException>(() => Shell.WriteShortcutLink(path, operation, new ShortcutDetails { Description = "x" }));
            Assert.Throws<ArgumentException>(() => Shell.WriteShortcutLink(path, operation, new ShortcutDetails { Target = "" }));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OpenExternalOptions_DefaultToElectrons() => Assert.True(new OpenExternalOptions().Activate);
}
