using System.Text;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class LinuxShellTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars)
        => name => vars.FirstOrDefault(v => v.Name == name) is { Name: not null } v ? v.Value : null;

    [Theory]
    [InlineData("kioclient5", "kioclient5|move|/x y|trash:/")]
    [InlineData("kioclient", "kioclient|move|/x y|trash:/")]
    [InlineData("trash-cli", "trash-put|/x y")]
    [InlineData("gvfs-trash", "gvfs-trash|/x y")]
    [InlineData("anything", "gio|trash|/x y")]
    public void TrashArgv_follows_ELECTRON_TRASH(string tool, string argv)
        => Assert.Equal(argv, string.Join('|', LinuxShellRules.TrashArgv("/x y", Env(("ELECTRON_TRASH", tool), ("XDG_CURRENT_DESKTOP", "KDE")))));

    [Theory]
    [InlineData("KDE", "5", "kioclient5")]
    [InlineData("KDE", "4", "kioclient5")]
    [InlineData("KDE", "6", "kioclient")]
    [InlineData("GNOME", "5", "gio")]
    public void TrashArgv_without_ELECTRON_TRASH_follows_the_desktop(string desktop, string kde, string tool)
        => Assert.Equal(tool, LinuxShellRules.TrashArgv("/f", Env(("ELECTRON_TRASH", ""), ("XDG_CURRENT_DESKTOP", desktop), ("KDE_SESSION_VERSION", kde)))[0]);

    [Fact]
    public void TrashArgv_on_KDE3_is_kioclient()
        => Assert.Equal(["kioclient", "move", "/f", "trash:/"], LinuxShellRules.TrashArgv("/f", Env(("DESKTOP_SESSION", "kde"))));

    [Theory]
    [InlineData("Unity", null, LinuxDesktopEnvironment.Unity)]
    [InlineData("Unity", "gnome-fallback-compiz", LinuxDesktopEnvironment.Gnome)]
    [InlineData("Deepin", null, LinuxDesktopEnvironment.Deepin)]
    [InlineData("ubuntu:GNOME", null, LinuxDesktopEnvironment.Gnome)]
    [InlineData(" X-Cinnamon ", null, LinuxDesktopEnvironment.Cinnamon)]
    [InlineData("Pantheon", null, LinuxDesktopEnvironment.Pantheon)]
    [InlineData("XFCE", null, LinuxDesktopEnvironment.Xfce)]
    [InlineData("UKUI", null, LinuxDesktopEnvironment.Ukui)]
    [InlineData("LXQt", null, LinuxDesktopEnvironment.Lxqt)]
    [InlineData("COSMIC", null, LinuxDesktopEnvironment.Cosmic)]
    [InlineData("Sway", "gnome", LinuxDesktopEnvironment.Gnome)]
    public void DesktopEnvironment_reads_XDG_CURRENT_DESKTOP_first(string current, string? session, LinuxDesktopEnvironment expected)
    {
        var vars = new List<(string, string)> { ("XDG_CURRENT_DESKTOP", current) };
        if (session is not null) vars.Add(("DESKTOP_SESSION", session));
        Assert.Equal(expected, LinuxShellRules.DesktopEnvironment(Env([.. vars])));
    }

    [Theory]
    [InlineData(null, LinuxDesktopEnvironment.Kde4)]
    [InlineData("5", LinuxDesktopEnvironment.Kde5)]
    [InlineData("6", LinuxDesktopEnvironment.Kde6)]
    public void DesktopEnvironment_KDE_by_session_version(string? version, LinuxDesktopEnvironment expected)
    {
        var vars = new List<(string, string)> { ("XDG_CURRENT_DESKTOP", "KDE") };
        if (version is not null) vars.Add(("KDE_SESSION_VERSION", version));
        Assert.Equal(expected, LinuxShellRules.DesktopEnvironment(Env([.. vars])));
    }

    [Theory]
    [InlineData("deepin", LinuxDesktopEnvironment.Deepin)]
    [InlineData("mate", LinuxDesktopEnvironment.Gnome)]
    [InlineData("kde-plasma", LinuxDesktopEnvironment.Kde4)]
    [InlineData("kde", LinuxDesktopEnvironment.Kde3)]
    [InlineData("xubuntu", LinuxDesktopEnvironment.Xfce)]
    [InlineData("my-xfce4", LinuxDesktopEnvironment.Xfce)]
    [InlineData("ukui", LinuxDesktopEnvironment.Ukui)]
    [InlineData("default", LinuxDesktopEnvironment.Other)]
    public void DesktopEnvironment_falls_back_on_DESKTOP_SESSION(string session, LinuxDesktopEnvironment expected)
        => Assert.Equal(expected, LinuxShellRules.DesktopEnvironment(Env(("DESKTOP_SESSION", session))));

    [Fact]
    public void DesktopEnvironment_older_variables()
    {
        Assert.Equal(LinuxDesktopEnvironment.Kde4, LinuxShellRules.DesktopEnvironment(Env(("DESKTOP_SESSION", "kde"), ("KDE_SESSION_VERSION", ""))));
        Assert.Equal(LinuxDesktopEnvironment.Gnome, LinuxShellRules.DesktopEnvironment(Env(("GNOME_DESKTOP_SESSION_ID", ""))));
        Assert.Equal(LinuxDesktopEnvironment.Kde3, LinuxShellRules.DesktopEnvironment(Env(("KDE_FULL_SESSION", "true"))));
        Assert.Equal(LinuxDesktopEnvironment.Kde4, LinuxShellRules.DesktopEnvironment(Env(("KDE_FULL_SESSION", "true"), ("KDE_SESSION_VERSION", "4"))));
        Assert.Equal(LinuxDesktopEnvironment.Other, LinuxShellRules.DesktopEnvironment(Env()));
    }

    [Fact]
    public void AlterEnvironment_replaces_in_place_of_the_old_line_and_appends()
    {
        byte[][] env = [B("A=1"), B("MM_NOTTTY=0"), B("NOEQUALS"), B("B=x=y")];
        var result = LinuxShellRules.AlterEnvironment(env, [("MM_NOTTTY", "1")]).Select(Encoding.UTF8.GetString);
        Assert.Equal(["A=1", "NOEQUALS", "B=x=y", "MM_NOTTTY=1"], result);
    }

    [Fact]
    public void AlterEnvironment_an_empty_value_unsets()
    {
        var result = LinuxShellRules.AlterEnvironment([B("A=1"), B("Z=2")], [("Z", ""), ("C", "3")]).Select(Encoding.UTF8.GetString);
        Assert.Equal(["A=1", "C=3"], result);
    }

    [Fact]
    public void AlterEnvironment_keeps_bytes_that_are_not_UTF8()
    {
        byte[] odd = [(byte)'X', (byte)'=', 0xff, 0xfe];
        Assert.Equal(odd, LinuxShellRules.AlterEnvironment([odd], [("MM_NOTTTY", "1")])[0]);
    }

    [Theory]
    [InlineData("/home/u/a b.txt", "file:///home/u/a%20b.txt")]
    [InlineData("/x/\"#%:<>?[\\]^`{|}\x7f", "file:///x/%22%23%25%3A%3C%3E%3F%5B%5C%5D%5E%60%7B%7C%7D%7F")]
    [InlineData("/x/!$&'()*+,;=@~-._", "file:///x/!$&'()*+,;=@~-._")]
    [InlineData("/x/\té", "file:///x/%09%C3%A9")]
    public void FileUri_escapes_as_EscapePath(string path, string uri) => Assert.Equal(uri, LinuxShellRules.FileUri(path));

    [Theory]
    [InlineData(":1.42", "TOK", "/org/freedesktop/portal/desktop/request/1_42/TOK")]
    [InlineData("name.with.dots", "T", "/org/freedesktop/portal/desktop/request/name_with_dots/T")]
    public void PortalRequestPath_as_Chromium(string sender, string token, string path)
        => Assert.Equal(path, LinuxShellRules.PortalRequestPath(sender, token));

    // Chromium's FilePathTest.DirName vectors for POSIX.
    [Theory]
    [InlineData("", ".")]
    [InlineData(".", ".")]
    [InlineData("aa", ".")]
    [InlineData("/aa/bb", "/aa")]
    [InlineData("/aa/bb/", "/aa")]
    [InlineData("/aa/bb//", "/aa")]
    [InlineData("/aa/bb/ccc", "/aa/bb")]
    [InlineData("/aa", "/")]
    [InlineData("/aa/", "/")]
    [InlineData("/", "/")]
    [InlineData("//", "//")]
    [InlineData("///", "/")]
    [InlineData("aa/", ".")]
    [InlineData("aa/bb", "aa")]
    [InlineData("aa//bb//", "aa")]
    [InlineData("//aa/bb", "//aa")]
    [InlineData("//aa/", "//")]
    [InlineData("//aa", "//")]
    [InlineData("///aa/", "/")]
    [InlineData("///aa", "/")]
    [InlineData("0:", ".")]
    [InlineData("/aa/../bb/cc", "/aa/../bb")]
    public void DirName_as_FilePath(string path, string dir) => Assert.Equal(dir, LinuxShellRules.DirName(path));

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
}
