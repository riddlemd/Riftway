using Riftway.Cef.Platform;
using Xunit;

namespace Riftway.Tests;

// Electron's wWinMain: base::RouteStdioToConsole(false) unless ELECTRON_NO_ATTACH_CONSOLE is set; Chromium's
// RouteStdioToConsole leaves a process alone whose stdout or stderr is already a valid stream.
public sealed class WindowsConsoleTests
{
    [Fact]
    public void NoStreams_Attaches() => Assert.True(WindowsConsole.ShouldAttach(stdoutValid: false, stderrValid: false, _ => null));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AValidStream_IsLeftAlone(bool stdoutValid, bool stderrValid)
        => Assert.False(WindowsConsole.ShouldAttach(stdoutValid, stderrValid, _ => null));

    [Theory]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("0")]
    public void NoAttachConsole_AnyValueSet_Stays(string value)
        => Assert.False(WindowsConsole.ShouldAttach(false, false, name => name == "ELECTRON_NO_ATTACH_CONSOLE" ? value : null));

    [Fact]
    public void RiftwayName_StandsIn()
        => Assert.False(WindowsConsole.ShouldAttach(false, false, name => name == "RIFTWAY_NO_ATTACH_CONSOLE" ? "1" : null));

    [Fact]
    public void OtherVariables_DoNotCount()
        => Assert.True(WindowsConsole.ShouldAttach(false, false, name => name == "ELECTRON_ENABLE_LOGGING" ? "1" : null));
}
