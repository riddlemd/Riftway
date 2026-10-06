namespace Oriel.Tests;

public sealed class AppCommandLineTests
{
    [Fact]
    public void AppendSwitch_IsFoundWithItsValue()
    {
        var commandLine = new AppCommandLine();
        commandLine.AppendSwitch("oriel-test-flag");
        commandLine.AppendSwitch("oriel-test-value", "42");
        Assert.True(commandLine.HasSwitch("oriel-test-flag"));
        Assert.Equal("", commandLine.GetSwitchValue("oriel-test-flag"));
        Assert.Equal("42", commandLine.GetSwitchValue("oriel-test-value"));
    }

    [Fact]
    public void AppendSwitch_TheLastValueWins()
    {
        var commandLine = new AppCommandLine();
        commandLine.AppendSwitch("oriel-test-value", "1");
        commandLine.AppendSwitch("oriel-test-value", "2");
        Assert.Equal("2", commandLine.GetSwitchValue("oriel-test-value"));
    }

    [Fact]
    public void AnAbsentSwitchIsNotThere()
    {
        var commandLine = new AppCommandLine();
        Assert.False(commandLine.HasSwitch("oriel-test-absent"));
        Assert.Equal("", commandLine.GetSwitchValue("oriel-test-absent"));
    }

    [Fact]
    public void AppendSwitch_RefusesAnEmptyName() => Assert.Throws<ArgumentException>(() => new AppCommandLine().AppendSwitch(""));
}
