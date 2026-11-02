#pragma warning disable CA1416 // The Windows-only helpers are exercised through a fake on every OS and gated per test otherwise.

using Microsoft.Win32;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class LoginItemTests
{
    private const string Exe = @"C:\riftway-test\app.exe";
    private const string AppId = "electron.app.Test";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    [Fact]
    public void DisabledMarker_Is03ThenElevenZeros()
        => Assert.Equal([0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], WindowsAppRegistry.DisabledMarker());

    public static TheoryData<byte[]?, bool> ApprovedData => new()
    {
        { null, true },
        { new byte[0], true },
        { new byte[12], true },
        { new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true },
        { new byte[] { 2 }, true },
        { new byte[] { 0, 3 }, true },
        { new byte[] { 2, 1, 0 }, false },
        { new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false },
        { new byte[] { 6, 0 }, false },
        { Enumerable.Repeat((byte)3, 24).ToArray(), false },
        { Enumerable.Repeat((byte)3, 25).ToArray(), true },
    };

    [Theory]
    [MemberData(nameof(ApprovedData))]
    public void IsApproved_ComparesTheBytesAsCStringsAgainstEmptyAnd02(byte[]? data, bool enabled)
        => Assert.Equal(enabled, WindowsAppRegistry.IsApproved(data));

    [Fact]
    public void FormatCommandLine_QuotesOnlyWhatNeedsItAndDefaultsAnEmptyPathToTheProcess()
    {
        Assert.Equal("\"C:\\riftway-test\\app.exe\" --bg \"a b\" plain", WindowsAppRegistry.FormatCommandLine(Exe, ["--bg", "a b", "plain"]));
        Assert.Equal("\"C:\\Program Files\\a.exe\"", WindowsAppRegistry.FormatCommandLine("\"C:\\Program Files\\a.exe\"", null));
        Assert.Equal("app.exe", WindowsAppRegistry.FormatCommandLine("app.exe", []));
        Assert.Equal(WindowsAppRegistry.AddQuoteForArg(Environment.ProcessPath!), WindowsAppRegistry.FormatCommandLine("", null));
    }

    [Fact]
    public void Set_EnabledWritesTheRunCommandAndDeletesTheMarker()
    {
        FakeAppRegistry fake = new();
        fake.Approved["n"] = WindowsAppRegistry.DisabledMarker();

        WindowsAppRegistry.SetLoginItem(fake, "n", true, true, Exe, ["--bg", "a b"]);

        Assert.Equal("\"C:\\riftway-test\\app.exe\" --bg \"a b\"", fake.Run["n"]);
        Assert.Equal(["set-run:n", "delete-approved:n"], fake.Calls);
        Assert.Empty(fake.Approved);
    }

    [Fact]
    public void Set_DisabledWritesTheDisabledMarker()
    {
        FakeAppRegistry fake = new();

        WindowsAppRegistry.SetLoginItem(fake, "n", true, false, Exe, null);

        Assert.Equal(WindowsAppRegistry.DisabledMarker(), fake.Approved["n"]);
        Assert.Equal("\"C:\\riftway-test\\app.exe\"", fake.Run["n"]);
    }

    [Fact]
    public void Set_OpenAtLoginFalseDeletesTheMarkerThenTheRunValue()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetLoginItem(fake, "n", true, false, Exe, null);
        fake.Calls.Clear();

        WindowsAppRegistry.SetLoginItem(fake, "n", false, true, Exe, null);

        Assert.Equal(["delete-approved:n", "delete-run:n"], fake.Calls);
        Assert.Empty(fake.Run);
        Assert.Empty(fake.Approved);
    }

    [Fact]
    public void Set_ARefusedRunWriteIsSilentAndTheMarkerStepStillRuns()
    {
        FakeAppRegistry fake = new() { Failure = new UnauthorizedAccessException() };

        WindowsAppRegistry.SetLoginItem(fake, "n", true, false, Exe, null);

        Assert.Equal(["set-run:n", "set-approved:n"], fake.Calls);
    }

    [WindowsOnlyFact]
    public void Get_OpenAtLoginReadsTheValueNamedByTheAppIdOnly()
    {
        FakeAppRegistry fake = new();
        fake.Run["custom-name"] = WindowsAppRegistry.FormatCommandLine(Exe, null)!;

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(fake, AppId, Exe, null);

        Assert.False(open);
        Assert.True(will);
        Assert.Equal("custom-name", Assert.Single(items).Name);

        fake.Run[AppId] = WindowsAppRegistry.FormatCommandLine(Exe, null)!;
        Assert.True(WindowsAppRegistry.GetLoginItems(fake, AppId, Exe, null).OpenAtLogin);
    }

    [WindowsOnlyFact]
    public void Get_OpenAtLoginIsAnExactCaseSensitiveMatch()
    {
        FakeAppRegistry fake = new();
        fake.Run[AppId] = "\"C:\\RIFTWAY-TEST\\APP.EXE\" --bg";

        var (open, _, items) = WindowsAppRegistry.GetLoginItems(fake, AppId, Exe, ["--bg"]);

        Assert.False(open);
        Assert.Single(items);
        Assert.False(WindowsAppRegistry.GetLoginItems(fake, AppId, @"C:\RIFTWAY-TEST\APP.EXE", ["--other"]).OpenAtLogin);
        Assert.True(WindowsAppRegistry.GetLoginItems(fake, AppId, @"C:\RIFTWAY-TEST\APP.EXE", ["--bg"]).OpenAtLogin);
    }

    [WindowsOnlyFact]
    public void Get_WillLaunchIsAnyEnabledItemWhateverItsArgs()
    {
        FakeAppRegistry fake = new();
        fake.Run["a"] = "\"C:\\riftway-test\\app.exe\" --one";
        fake.Run["b"] = "\"C:\\riftway-test\\app.exe\" --two";
        fake.Approved["a"] = WindowsAppRegistry.DisabledMarker();

        var (open, will, items) = WindowsAppRegistry.GetLoginItems(fake, AppId, Exe, ["--three"]);

        Assert.False(open);
        Assert.True(will);
        Assert.Equal([("a", false), ("b", true)], items.Select(i => (i.Name, i.Enabled)));

        fake.Approved["b"] = WindowsAppRegistry.DisabledMarker();
        Assert.False(WindowsAppRegistry.GetLoginItems(fake, AppId, Exe, null).WillLaunch);
    }

    [WindowsOnlyFact]
    public void Get_ListsUserThenMachineItemsWithTheirScopeAndArgsWithoutSwitches()
    {
        FakeAppRegistry fake = new();
        fake.Run["u"] = "\"C:\\RIFTWAY-TEST\\APP.EXE\" --bg file.txt -- --after";
        fake.Run["other"] = "\"C:\\other.exe\"";
        fake.MachineRun.Add(("m", "C:\\riftway-test\\app.exe /x y"));
        fake.MachineApproved["m"] = WindowsAppRegistry.DisabledMarker();

        var (_, _, items) = WindowsAppRegistry.GetLoginItems(fake, AppId, Exe, null);

        Assert.Equal(2, items.Count);
        Assert.Equal(("u", @"C:\RIFTWAY-TEST\APP.EXE", "user", true), (items[0].Name, items[0].Path, items[0].Scope, items[0].Enabled));
        Assert.Equal(["file.txt", "--after"], items[0].Args);
        Assert.Equal(("m", Exe, "machine", false), (items[1].Name, items[1].Path, items[1].Scope, items[1].Enabled));
        Assert.Equal(["y"], items[1].Args);
    }

    [WindowsOnlyFact]
    public void Get_AnUnquotedPathWithSpacesIsCutAtTheFirstSpaceAsInElectron()
    {
        FakeAppRegistry fake = new();
        fake.Run["quoted"] = "\"C:\\Program Files\\app.exe\"";
        fake.Run["bare"] = "C:\\Program Files\\app.exe";

        var (_, _, items) = WindowsAppRegistry.GetLoginItems(fake, AppId, @"C:\Program Files\app.exe", null);

        Assert.Equal(("bare", @"C:\Program"), (Assert.Single(items).Name, items[0].Path));
        Assert.Equal(["Files\\app.exe"], items[0].Args);
    }

    [WindowsOnlyFact]
    public void Get_NothingRegisteredIsAllFalse()
    {
        var (open, will, items) = WindowsAppRegistry.GetLoginItems(new FakeAppRegistry(), AppId, Exe, null);

        Assert.False(open);
        Assert.False(will);
        Assert.Empty(items);
    }

    [WindowsOnlyTheory]
    [InlineData("\"C:\\a b\\x.exe\" one two", "C:\\a b\\x.exe", new[] { "one", "two" })]
    [InlineData("x.exe --flag -f /f value", "x.exe", new[] { "value" })]
    [InlineData("x.exe a -- --b -- c", "x.exe", new[] { "a", "--b", "--", "c" })]
    [InlineData("x.exe - / -- ", "x.exe", new[] { "-", "/" })]
    [InlineData("x.exe a --single-argument b \"c d\"", "x.exe", new[] { "b \"c d\"" })]
    [InlineData("   ", "", new string[0])]
    [InlineData("x.exe \"a\\\"b\" c\\\\", "x.exe", new[] { "a\"b", "c\\\\" })]
    public void ParseCommandLine_MatchesChromiumsProgramAndArgs(string commandLine, string program, string[] args)
    {
        var parsed = WindowsAppRegistry.ParseCommandLine(commandLine);

        Assert.Equal(program, parsed.Program);
        Assert.Equal(args, parsed.Args);
    }

    [WindowsOnlyFact]
    public void RealRegistry_RoundTripsThroughTheRunAndStartupApprovedKeys()
    {
        string name = "riftway-test-" + Guid.NewGuid().ToString("N");
        try
        {
            RiftwayApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Name = name, Path = Exe, Args = ["--bg", "x y"] });

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)!)
            {
                Assert.Equal("\"C:\\riftway-test\\app.exe\" --bg \"x y\"", run.GetValue(name));
                Assert.Equal(RegistryValueKind.String, run.GetValueKind(name));
            }

            using (RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedPath))
            {
                Assert.Null(approved?.GetValue(name));
            }

            LoginItemStatus status = RiftwayApp.ReadLoginItem(new LoginItemQuery { Path = Exe, Args = ["--bg", "x y"] });
            Assert.False(status.OpenAtLogin);
            Assert.False(status.WasOpenedAtLogin);
            Assert.True(status.ExecutableWillLaunchAtLogin);
            LoginItemLaunchItem item = Assert.Single(status.LaunchItems, i => i.Name == name);
            Assert.Equal(("user", true, Exe), (item.Scope, item.Enabled, item.Path));
            Assert.Equal(["x y"], item.Args);

            RiftwayApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Enabled = false, Name = name, Path = Exe, Args = ["--bg"] });

            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedPath)!)
            {
                Assert.Equal(WindowsAppRegistry.DisabledMarker(), approved.GetValue(name));
                Assert.Equal(RegistryValueKind.Binary, approved.GetValueKind(name));
            }

            Assert.False(Assert.Single(RiftwayApp.ReadLoginItem(new LoginItemQuery { Path = Exe }).LaunchItems, i => i.Name == name).Enabled);

            RiftwayApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = false, Name = name });

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)!)
            {
                Assert.Null(run.GetValue(name));
            }

            using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedPath)!)
            {
                Assert.Null(approved.GetValue(name));
            }
        }
        finally
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            run?.DeleteValue(name, throwOnMissingValue: false);
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true);
            approved?.DeleteValue(name, throwOnMissingValue: false);
        }
    }

    [WindowsOnlyFact]
    public void RealRegistry_NameDefaultsToTheAppUserModelIdWhichAlsoDecidesOpenAtLogin()
    {
        string appId = WindowsAppRegistry.AppUserModelId();
        using (RegistryKey? before = Registry.CurrentUser.OpenSubKey(RunPath))
        {
            Assert.Null(before?.GetValue(appId));
        }

        string path = @"C:\riftway-test\" + Guid.NewGuid().ToString("N") + ".exe";
        try
        {
            RiftwayApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = true, Path = path, Name = "" });

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunPath)!)
            {
                Assert.Equal("\"" + path + "\"", run.GetValue(appId));
            }

            Assert.True(RiftwayApp.ReadLoginItem(new LoginItemQuery { Path = path }).OpenAtLogin);
        }
        finally
        {
            RiftwayApp.ApplyLoginItem(new LoginItemSettings { OpenAtLogin = false });
        }

        using RegistryKey? after = Registry.CurrentUser.OpenSubKey(RunPath);
        Assert.Null(after?.GetValue(appId));
    }

    [WindowsOnlyFact]
    public void RealAppUserModelId_IsSetOnFirstUseAndStable()
    {
        string appId = WindowsAppRegistry.AppUserModelId();

        Assert.Equal(appId, WindowsJumpList.ExplicitAppUserModelId());
        Assert.Equal(appId, WindowsAppRegistry.AppUserModelId());
        Assert.StartsWith("electron.app.", appId);
    }
}
