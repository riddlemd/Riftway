#pragma warning disable CA1416 // The Windows-only helpers are exercised through a fake on every OS and gated per test otherwise.

using Microsoft.Win32;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class LoginItemTests
{
    private const string Exe = @"C:\oriel-test\app.exe";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    [Fact]
    public void ApprovedData_IsTwelveBytesWithTheStateInTheFirst()
    {
        byte[] on = WindowsAppRegistry.ApprovedData(true);
        byte[] off = WindowsAppRegistry.ApprovedData(false);

        Assert.Equal(12, on.Length);
        Assert.Equal(12, off.Length);
        Assert.Equal(0x02, on[0]);
        Assert.Equal(0x03, off[0]);
        Assert.All(on.Skip(1), b => Assert.Equal(0, b));
        Assert.All(off.Skip(1), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 2 }, true)]
    [InlineData(new byte[] { 6 }, true)]
    [InlineData(new byte[] { 3 }, false)]
    [InlineData(new byte[] { 7 }, false)]
    public void IsApproved_TreatsMissingAndEvenAsEnabled(byte[]? data, bool enabled)
        => Assert.Equal(enabled, WindowsAppRegistry.IsApproved(data));

    [Fact]
    public void Set_WritesTheRunCommandAndAnEnabledMarker()
    {
        FakeAppRegistry fake = new();

        Assert.True(WindowsAppRegistry.SetLoginItem(fake, "n", true, true, Exe, ["--bg", "a b"]));

        Assert.Equal("\"C:\\oriel-test\\app.exe\" --bg \"a b\"", fake.Run["n"]);
        Assert.Equal(0x02, fake.Approved["n"][0]);
    }

    [Fact]
    public void Set_Disabled_WritesTheDisabledMarker()
    {
        FakeAppRegistry fake = new();

        WindowsAppRegistry.SetLoginItem(fake, "n", true, false, Exe, null);

        Assert.Equal(0x03, fake.Approved["n"][0]);
        Assert.True(fake.Run.ContainsKey("n"));
    }

    [Fact]
    public void Set_OpenAtLoginFalse_DeletesBothValues()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetLoginItem(fake, "n", true, true, Exe, null);

        Assert.True(WindowsAppRegistry.SetLoginItem(fake, "n", false, true, Exe, null));

        Assert.Empty(fake.Run);
        Assert.Empty(fake.Approved);
    }

    [Fact]
    public void Set_EmptyName_Fails()
    {
        FakeAppRegistry fake = new();

        Assert.False(WindowsAppRegistry.SetLoginItem(fake, "", true, true, Exe, null));

        Assert.Empty(fake.Run);
    }

    [Fact]
    public void Set_WhenTheRegistryRefuses_ReturnsFalse()
        => Assert.False(WindowsAppRegistry.SetLoginItem(
            new FakeAppRegistry { Failure = new UnauthorizedAccessException() }, "n", true, true, Exe, null));

    [Fact]
    public void Get_ReportsAnEnabledMatchingEntry()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetLoginItem(fake, "n", true, true, Exe, ["--bg"]);

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(fake, Exe, ["--bg"]);

        Assert.True(open);
        Assert.True(will);
        RunItem item = Assert.Single(items);
        Assert.Equal(("n", Exe, true), (item.Name, item.Path, item.Enabled));
        Assert.Equal(["--bg"], item.Args);
    }

    [Fact]
    public void Get_ADisabledEntryIsOpenAtLoginButWillNotLaunch()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetLoginItem(fake, "n", true, false, Exe, null);

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(fake, Exe, null);

        Assert.True(open);
        Assert.False(will);
        Assert.False(Assert.Single(items).Enabled);
    }

    [Fact]
    public void Get_AnEntryWithoutAMarkerIsEnabled()
    {
        FakeAppRegistry fake = new();
        fake.Run["n"] = WindowsAppRegistry.BuildCommand(Exe, null);

        var (_, will, _) = WindowsAppRegistry.GetLoginItems(fake, Exe, null);

        Assert.True(will);
    }

    [Fact]
    public void Get_OtherArgsListTheItemButAreNotOpenAtLogin()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetLoginItem(fake, "n", true, true, Exe, ["--bg"]);

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(fake, Exe, ["--other"]);

        Assert.False(open);
        Assert.False(will);
        Assert.Single(items);
    }

    [Fact]
    public void Get_OtherPathsAreNotListed()
    {
        FakeAppRegistry fake = new();
        fake.Run["other"] = "\"C:\\other.exe\"";
        WindowsAppRegistry.SetLoginItem(fake, "n", true, true, Exe, null);

        var (_, _, items) = WindowsAppRegistry.GetLoginItems(fake, Exe, null);

        Assert.Equal("n", Assert.Single(items).Name);
    }

    [Fact]
    public void Get_MatchesThePathIgnoringCase()
    {
        FakeAppRegistry fake = new();
        fake.Run["n"] = "\"C:\\ORIEL-TEST\\APP.EXE\"";

        var (open, _, items) = WindowsAppRegistry.GetLoginItems(fake, Exe, null);

        Assert.True(open);
        Assert.Single(items);
    }

    [Fact]
    public void Get_NothingRegistered_IsAllFalse()
    {
        var (open, will, items) = WindowsAppRegistry.GetLoginItems(new FakeAppRegistry(), Exe, null);

        Assert.False(open);
        Assert.False(will);
        Assert.Empty(items);
    }

    [WindowsOnlyFact]
    public void RealRegistry_RoundTripsThroughTheRunAndStartupApprovedKeys()
    {
        string name = "oriel-test-" + Guid.NewGuid().ToString("N");
        try
        {
            OrielApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Name = name, Path = Exe, Args = ["--bg"] });

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)!)
            {
                Assert.Equal("\"C:\\oriel-test\\app.exe\" --bg", run.GetValue(name));
                Assert.Equal(RegistryValueKind.String, run.GetValueKind(name));
            }

            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedPath)!)
            {
                byte[] data = Assert.IsType<byte[]>(approved.GetValue(name));
                Assert.Equal(12, data.Length);
                Assert.Equal(0x02, data[0]);
            }

            LoginItemStatus status = OrielApp.ReadLoginItem(new LoginItemQuery { Path = Exe, Args = ["--bg"] });
            Assert.True(status.OpenAtLogin);
            Assert.True(status.ExecutableWillLaunchAtLogin);
            LoginItemLaunchItem item = Assert.Single(status.LaunchItems, i => i.Name == name);
            Assert.Equal(("user", true, Exe), (item.Scope, item.Enabled, item.Path));
            Assert.Equal(["--bg"], item.Args);

            OrielApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Enabled = false, Name = name, Path = Exe, Args = ["--bg"] });

            status = OrielApp.ReadLoginItem(new LoginItemQuery { Path = Exe, Args = ["--bg"] });
            Assert.True(status.OpenAtLogin);
            Assert.False(status.ExecutableWillLaunchAtLogin);

            OrielApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = false, Name = name });

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)!)
            {
                Assert.Null(run.GetValue(name));
            }

            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedPath)!)
            {
                Assert.Null(approved.GetValue(name));
            }

            Assert.False(OrielApp.ReadLoginItem(new LoginItemQuery { Path = Exe, Args = ["--bg"] }).OpenAtLogin);
        }
        finally
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            run?.DeleteValue(name, throwOnMissingValue: false);
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
            approved?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [Fact]
    public void DefaultName_IsOrielBeforeAnAppExists()
    {
        if (OrielApp.CurrentOrNull is null)
        {
            Assert.Equal("Oriel", OrielApp.DefaultLoginItemName());
        }
    }

    [WindowsOnlyFact]
    public void RealRegistry_AnEmptyNameThrowsInsteadOfSilentlyDoingNothing()
        => Assert.Throws<InvalidOperationException>(
            () => OrielApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Name = "", Path = Exe }));

    [WindowsOnlyFact]
    public void RealRegistry_NameDefaultsToTheAppNameWhenNoneIsGiven()
    {
        string name = OrielApp.DefaultLoginItemName();
        using RegistryKey? before = Registry.CurrentUser.OpenSubKey(RunPath);
        Assert.Null(before?.GetValue(name));
        string path = @"C:\oriel-test\" + Guid.NewGuid().ToString("N") + ".exe";
        try
        {
            OrielApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Path = path });

            using RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)!;
            Assert.Equal("\"" + path + "\"", run.GetValue(name));
        }
        finally
        {
            OrielApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = false });
        }
    }
}
