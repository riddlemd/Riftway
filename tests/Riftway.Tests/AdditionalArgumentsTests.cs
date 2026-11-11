using System.Text.Json.Nodes;

namespace Riftway.Tests;

// Electron's additionalArguments: strings appended to process.argv where the page's preload scripts run. Riftway's
// preloads get a `process` whose argv is the app's executable, then those strings (drift W66).
public sealed class AdditionalArgumentsTests
{
    [Fact]
    public void ThereAreNoneByDefault() => Assert.Null(new WebPreferences().AdditionalArguments);

    [Fact]
    public void ArgvIsTheExecutable_ThenTheArguments_InOrder()
    {
        var argv = ParseArgv(WebContents.PreloadProcessScript("C:\\app\\App.exe", ["--a=1", "two words", "--a=1"]));
        Assert.Equal(["C:\\app\\App.exe", "--a=1", "two words", "--a=1"], argv);
    }

    [Fact]
    public void WithoutArguments_ArgvIsTheExecutableAlone()
        => Assert.Equal(["/opt/app/App"], ParseArgv(WebContents.PreloadProcessScript("/opt/app/App", null)));

    [Fact]
    public void QuotesAndScriptTerminators_StayInsideTheStrings()
    {
        var tricky = "\"'`); alert(1); //\u2028</script>";
        Assert.Equal(["x", tricky], ParseArgv(WebContents.PreloadProcessScript("x", [tricky])));
    }

    [Fact]
    public void APageWithANullArgument_IsRefused_BeforeItLoads()
        => Assert.Throws<ArgumentException>(() => new WebPreferences { AdditionalArguments = ["ok", null!] }.Validate("options"));

    [Fact]
    public void AnArgumentCannotBeNull()
        => Assert.Throws<ArgumentException>(() => WebContents.PreloadProcessScript("x", ["ok", null!]));

    [Fact]
    public void EachPreloadIsCalledWithTheProcess_AsElectronsWrapperPassesIt()
    {
        var wrapped = WebContents.PreloadWrapper("p.js", "process.argv");
        Assert.Contains("new Function(\"process\", ", wrapped, StringComparison.Ordinal);
        Assert.Contains(")(riftwayPreloadProcess);", wrapped, StringComparison.Ordinal);
    }

    // The script is `const riftwayPreloadProcess = Object.freeze({ argv: Object.freeze(<JSON array>) });`.
    private static string[] ParseArgv(string script)
    {
        const string start = "argv: Object.freeze(";
        var from = script.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var to = script.LastIndexOf(") })", StringComparison.Ordinal);
        return JsonNode.Parse(script[from..to])!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    }
}
