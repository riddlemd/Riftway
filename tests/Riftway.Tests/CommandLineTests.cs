namespace Riftway.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--a", "a", "")]
    [InlineData("--a=1", "a", "1")]
    [InlineData("--a=x=y", "a", "x=y")]
    [InlineData("--a=", "a", "")]
    [InlineData("-a", "a", "")]
    [InlineData("-a=1", "a", "1")]
    public void ParseSwitch_ReadsNameAndValue(string argument, string name, string value)
        => Assert.Equal((name, value), CommandLine.ParseSwitch(argument));

    [Theory]
    [InlineData("a")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("file.txt")]
    public void ParseSwitch_LeavesArguments(string argument) => Assert.Null(CommandLine.ParseSwitch(argument));

    [Fact]
    public void SlashSwitches_OnWindowsOnly() => Assert.Equal(OperatingSystem.IsWindows(), CommandLine.ParseSwitch("/a") is not null);

    [Fact]
    public void AfterBareDoubleDash_EverythingIsAnArgument()
    {
        var line = new CommandLine(["--a", "--", "--b"]);
        Assert.True(line.HasSwitch("a"));
        Assert.False(line.HasSwitch("b"));
    }

    [Fact]
    public void ProcessSwitches_AreReadable_TheLastValueWins()
    {
        var line = new CommandLine(["--a=1", "--b", "file.txt", "--a=2"]);
        Assert.True(line.HasSwitch("a"));
        Assert.Equal("2", line.GetSwitchValue("a"));
        Assert.True(line.HasSwitch("b"));
        Assert.Equal("", line.GetSwitchValue("b"));
        Assert.False(line.HasSwitch("file.txt"));
        Assert.False(line.HasSwitch("missing"));
        Assert.Equal("", line.GetSwitchValue("missing"));
    }

    [Fact]
    public void AppendSwitch_AddsOrReplaces()
    {
        var line = new CommandLine(["--a=1"]);
        line.AppendSwitch("a", "3");
        line.AppendSwitch("c");
        Assert.Equal("3", line.GetSwitchValue("a"));
        Assert.True(line.HasSwitch("c"));
        Assert.Equal("", line.GetSwitchValue("c"));
    }

    [Fact]
    public void AppendSwitch_DropsALeadingPrefix_AsChromium()
    {
        var line = new CommandLine([]);
        line.AppendSwitch("--a", "1");
        line.AppendSwitch("-b");
        Assert.Equal("1", line.GetSwitchValue("a"));
        Assert.True(line.HasSwitch("b"));
        Assert.False(line.HasSwitch("--a"));
    }

    [Fact]
    public void Names_AreLowercased_AsElectron()
    {
        var line = new CommandLine([]);
        line.AppendSwitch("Mixed-Case", "v");
        Assert.True(line.HasSwitch("mixed-case"));
        Assert.Equal("v", line.GetSwitchValue("MIXED-case"));
        line.RemoveSwitch("MIXED-CASE");
        Assert.False(line.HasSwitch("mixed-case"));
    }

    [Fact]
    public void ProcessSwitchNames_KeepTheirCase_OffWindows()
    {
        // Chromium lowercases parsed names on Windows only; Electron lowercases every name it is asked about.
        var line = new CommandLine(["--Name=1"]);
        Assert.Equal(OperatingSystem.IsWindows(), line.HasSwitch("name"));
        Assert.Equal(OperatingSystem.IsWindows(), line.HasSwitch("Name"));
    }

    [Fact]
    public void RemoveSwitch_RemovesProcessAndAppendedSwitches()
    {
        var line = new CommandLine(["--a=1"]);
        line.AppendSwitch("b", "2");
        line.RemoveSwitch("a");
        line.RemoveSwitch("b");
        Assert.False(line.HasSwitch("a"));
        Assert.False(line.HasSwitch("b"));
        line.AppendSwitch("a", "4");
        Assert.Equal("4", line.GetSwitchValue("a"));
    }

    [Fact]
    public void AppendArgument_IsNotASwitch()
    {
        var line = new CommandLine([]);
        line.AppendArgument("--looks-like-one");
        Assert.False(line.HasSwitch("looks-like-one"));
    }

    [Fact]
    public void AppendSwitch_RefusesAnEmptyName() => Assert.Throws<ArgumentException>(() => new CommandLine([]).AppendSwitch(""));

    private static Dictionary<string, string> Logging(string[] arguments, Dictionary<string, string>? environment = null)
        => new CommandLine(arguments).ElectronLoggingSwitches(name => environment?.GetValueOrDefault(name), "/data")
            .ToDictionary(s => s.Name, s => s.Value);

    [Fact]
    public void Logging_OffByDefault() => Assert.Empty(Logging([]));

    [Theory]
    [InlineData(null, "info")]
    [InlineData("0", "info")]
    [InlineData("1", "warning")]
    [InlineData("2", "error")]
    [InlineData("3", "fatal")]
    [InlineData("4", "info")]
    [InlineData("x", "info")]
    public void EnableLogging_GoesToStderrAtTheLogLevel(string? level, string severity)
    {
        var switches = Logging(level is null ? ["--enable-logging"] : ["--enable-logging", "--log-level=" + level]);
        Assert.Equal("stderr", switches["enable-logging"]);
        Assert.Equal(severity, switches["log-severity"]);
        Assert.Equal("pid,timestamp", switches["log-items"]);
        Assert.False(switches.ContainsKey("log-file"));
    }

    [Fact]
    public void LogLevel_AppliesWithoutLogging_AndBadLevelsAreIgnored()
    {
        Assert.Equal(new() { ["log-severity"] = "error" }, Logging(["--log-level=2"]));
        Assert.Empty(Logging(["--log-level=-1"]));
    }

    [Fact]
    public void ExplicitSeverityAndItems_Win()
    {
        var switches = Logging(["--enable-logging", "--log-severity=verbose", "--log-items=none"]);
        Assert.False(switches.ContainsKey("log-severity"));
        Assert.False(switches.ContainsKey("log-items"));
    }

    [Fact]
    public void EnableLoggingFile_DefaultsToUserData()
    {
        var switches = Logging(["--enable-logging=file"]);
        Assert.Equal("", switches["enable-logging"]);
        Assert.Equal(Path.Combine("/data", "riftway_debug.log"), switches["log-file"]);
    }

    [Fact]
    public void AnExplicitLogFile_MeansAFile_WhateverTheDestination()
    {
        Assert.Equal("/a.log", Logging(["--enable-logging", "--log-file=/a.log"])["log-file"]);
        Assert.Equal("", Logging(["--enable-logging=stderr", "--log-file=/a.log"])["enable-logging"]);
        Assert.Equal("/e.log", Logging(["--enable-logging"], new() { ["ELECTRON_LOG_FILE"] = "/e.log" })["log-file"]);
    }

    [Fact]
    public void LogFile_SwitchThenElectronThenRiftwayVariable()
    {
        var both = new Dictionary<string, string> { ["ELECTRON_LOG_FILE"] = "/e.log", ["RIFTWAY_LOG_FILE"] = "/o.log" };
        Assert.Equal("/a.log", Logging(["--enable-logging=file", "--log-file=/a.log"], both)["log-file"]);
        Assert.Equal("/e.log", Logging(["--enable-logging=file"], both)["log-file"]);
        Assert.Equal("/o.log", Logging(["--enable-logging=file"], new() { ["RIFTWAY_LOG_FILE"] = "/o.log" })["log-file"]);
    }

    [Fact]
    public void AnEmptyLogFileVariable_IsNoFile()
        => Assert.Equal("stderr", Logging(["--enable-logging"], new() { ["ELECTRON_LOG_FILE"] = "" })["enable-logging"]);

    [Fact]
    public void LogFile_AloneDoesNotEnableLogging() => Assert.Empty(Logging(["--log-file=/a.log"], new() { ["ELECTRON_LOG_FILE"] = "/e.log" }));

    [Fact]
    public void Variables_EnableLogging_EvenWhenEmpty()
    {
        Assert.Equal("stderr", Logging([], new() { ["ELECTRON_ENABLE_LOGGING"] = "" })["enable-logging"]);
        Assert.Equal("stderr", Logging([], new() { ["RIFTWAY_ENABLE_LOGGING"] = "1" })["enable-logging"]);
        Assert.Equal(Path.Combine("/data", "riftway_debug.log"), Logging([], new() { ["RIFTWAY_ENABLE_LOGGING"] = "file" })["log-file"]);
    }

    [Fact]
    public void TheSwitch_OverridesTheVariable()
    {
        var switches = Logging(["--enable-logging"], new() { ["ELECTRON_ENABLE_LOGGING"] = "file" });
        Assert.Equal("stderr", switches["enable-logging"]);
        // The Electron name wins over the alias: "file" here, stderr if the alias were read.
        Assert.Equal("", Logging([], new() { ["ELECTRON_ENABLE_LOGGING"] = "file", ["RIFTWAY_ENABLE_LOGGING"] = "1" })["enable-logging"]);
    }
}
