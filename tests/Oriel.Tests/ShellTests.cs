using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class ShellTests
{
    [Theory]
    [InlineData(ShortcutLinkOperation.Create, WindowsShortcutOperation.CreateAlways)]
    [InlineData(ShortcutLinkOperation.Update, WindowsShortcutOperation.UpdateExisting)]
    [InlineData(ShortcutLinkOperation.Replace, WindowsShortcutOperation.ReplaceExisting)]
    public void Operation_maps_to_chromiums(ShortcutLinkOperation operation, WindowsShortcutOperation windows)
        => Assert.Equal(windows, ShortcutRules.ToWindows(operation));

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
            // Electron has no shortcut methods off Windows, so there the call fails before any argument check.
            if (OperatingSystem.IsWindows())
                Assert.Throws<ArgumentException>(() => Shell.WriteShortcutLink(path, operation, new ShortcutDetails { Description = "x" }));
            else
                Assert.Throws<PlatformNotSupportedException>(() => Shell.WriteShortcutLink(path, operation, new ShortcutDetails { Description = "x" }));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WriteShortcutLink_WithAnEmptyTarget_DoesNotThrow()
    {
        // Chromium only requires the target to be given; an empty one goes to IShellLink::SetPath.
        var path = Path.Combine(Path.GetTempPath(), $"oriel-empty-target-{Guid.NewGuid():N}.lnk");
        // Shortcut links are Windows-only in Electron; elsewhere the call reaches the unsupported backend.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => Shell.WriteShortcutLink(path, ShortcutLinkOperation.Replace, new ShortcutDetails { Target = "" }));
            return;
        }
        try { _ = Shell.WriteShortcutLink(path, ShortcutLinkOperation.Replace, new ShortcutDetails { Target = "" }); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OpenExternalOptions_DefaultToElectrons()
    {
        var options = new OpenExternalOptions();
        Assert.True(options.Activate);
        Assert.False(options.LogUsage);
        Assert.Null(options.WorkingDirectory);
    }

    [Fact]
    public void EscapeExternalHandlerValue_keeps_alphanumerics_and_the_reserved_set()
    {
        const string Kept = "azAZ09;/?:@&=+$,!'()*-._~#[]";
        Assert.Equal(Kept, ShellRules.EscapeExternalHandlerValue(Kept));
    }

    [Theory]
    [InlineData(" ", "%20")]
    [InlineData("\"", "%22")]
    [InlineData("<>", "%3C%3E")]
    [InlineData("\\^`", "%5C%5E%60")]
    [InlineData("{|}", "%7B%7C%7D")]
    [InlineData("\u007F\u0001", "%7F%01")]
    [InlineData("é", "%C3%A9")]
    [InlineData("%41%zz", "%41%25zz")]
    [InlineData("%4", "%254")]
    [InlineData("a%", "a%25")]
    [InlineData("x%41", "x%41")]
    [InlineData("%4z", "%254z")]
    public void EscapeExternalHandlerValue_escapes_everything_else(string text, string escaped)
        => Assert.Equal(escaped, ShellRules.EscapeExternalHandlerValue(text));

    [Theory]
    [InlineData("https://Example.com/a b", "\"https://example.com/a%20b\"")]
    [InlineData("myapp:x|y", "\"myapp:x%7Cy\"")]
    [InlineData("not a url", "\"\"")]
    public void ExternalFile_is_the_quoted_escaped_spec(string url, string file) => Assert.Equal(file, ShellRules.ExternalFile(url));

    [Theory]
    [InlineData(@"C:\a\b.txt", @"C:\a")]
    [InlineData(@"C:\a\b\\", @"C:\a")]
    [InlineData(@"C:\a", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:a", "C:")]
    [InlineData("a", ".")]
    [InlineData("", ".")]
    [InlineData(@"\a", @"\")]
    [InlineData(@"\\host\share", @"\\host")]
    [InlineData(@"\\host", @"\\")]
    [InlineData(@"\\\host", @"\\")]
    [InlineData("a/b", "a")]
    [InlineData(@"a\\b", "a")]
    [InlineData(@"1:\x", "1:")]
    [InlineData(@"\\\", @"\")]
    [InlineData(@"a\", ".")]
    public void DirName_follows_chromiums_FilePath(string path, string dir) => Assert.Equal(dir, WindowsPaths.DirName(path));

    [Fact]
    public void Separators_normalize_and_end()
    {
        Assert.Equal(@"C:\a\b", WindowsPaths.NormalizeSeparators("C:/a/b"));
        Assert.Equal(@"C:\a\", WindowsPaths.AsEndingWithSeparator(@"C:\a"));
        Assert.Equal(@"C:\", WindowsPaths.AsEndingWithSeparator(@"C:\"));
        Assert.Equal("", WindowsPaths.AsEndingWithSeparator(""));
    }

    [Theory]
    [InlineData("The system cannot find the file specified.\r\n", "The system cannot find the file specified.")]
    [InlineData("  a  b  ", "a b")]
    [InlineData("a \r\n b", "ab")]
    [InlineData("a\tb\n", "a b")]
    [InlineData("a\rb", "ab")]
    public void CollapseWhitespace_matches_chromiums(string text, string collapsed) => Assert.Equal(collapsed, WindowsPaths.CollapseWhitespace(text));

    [Fact]
    public void SystemErrorCodeToString_appends_the_code_in_hex()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Matches(@"^\S.*\S \(0x2\)$", WindowsShellOps.SystemErrorCodeToString(2));
        Assert.EndsWith(" (0x483)", WindowsShellOps.SystemErrorCodeToString(0x483));
    }
}
