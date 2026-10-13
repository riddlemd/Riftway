using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class AppLocaleTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
        => key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void LinuxLanguages_ExpandLikeGLib()
    {
        Assert.Equal(["en-US", "en-US", "en", "en"], OrielApp.LinuxLanguages(Env(("LANG", "en_US.UTF-8"))));
        Assert.Equal(["de-DE", "de", "fr"], OrielApp.LinuxLanguages(Env(("LANGUAGE", "de_DE:fr"), ("LC_ALL", "en_US"))));
        Assert.Equal(["sr-RS", "sr", "sr-RS", "sr"], OrielApp.LinuxLanguages(Env(("LC_ALL", "sr_RS@latin"), ("LANG", "en_US"))));
        Assert.Equal(["pt-BR", "pt"], OrielApp.LinuxLanguages(Env(("LANG", "en_US"), ("LC_MESSAGES", "pt_BR"))));
        Assert.Empty(OrielApp.LinuxLanguages(Env(("LANG", "C"))));
        Assert.Empty(OrielApp.LinuxLanguages(Env()));
    }

    [Fact]
    public void LinuxConfiguredLocale_PrintsIcuDefault()
    {
        Assert.Equal("en-US", OrielApp.LinuxConfiguredLocale(Env(("LANG", "en_US.UTF-8"))));
        Assert.Equal("fr-CA", OrielApp.LinuxConfiguredLocale(Env(("LANG", "en_US"), ("LC_MESSAGES", "fr_CA"))));
        Assert.Equal("ja-JP", OrielApp.LinuxConfiguredLocale(Env(("LC_ALL", "ja_JP"), ("LC_MESSAGES", "fr_CA"))));
        Assert.Equal("de-DE@euro", OrielApp.LinuxConfiguredLocale(Env(("LANG", "de_DE@EURO"))));
        Assert.Equal("en-US@posix", OrielApp.LinuxConfiguredLocale(Env(("LANG", "C.UTF-8"))));
        Assert.Equal("en-US@posix", OrielApp.LinuxConfiguredLocale(Env(("LANG", "POSIX"))));
        Assert.Equal("en-US@posix", OrielApp.LinuxConfiguredLocale(Env()));
        Assert.Equal("de", OrielApp.LinuxConfiguredLocale(Env(("LANG", "de"))));
    }

    [Fact]
    public void LinuxTimeLocale_ReadsLcAllThenLcTimeThenLang()
    {
        Assert.Equal("en_US.UTF-8", OrielApp.LinuxTimeLocale(Env(("LANG", "en_US.UTF-8"))));
        Assert.Equal("en_GB", OrielApp.LinuxTimeLocale(Env(("LANG", "en_US"), ("LC_TIME", "en_GB"))));
        Assert.Equal("ja_JP", OrielApp.LinuxTimeLocale(Env(("LC_ALL", "ja_JP"), ("LC_TIME", "en_GB"))));
        Assert.Equal("C", OrielApp.LinuxTimeLocale(Env()));
    }

    [Fact]
    public void LinuxRegion_IsTheTerritory()
    {
        Assert.Equal("US", OrielApp.LinuxRegion("en_US.UTF-8"));
        Assert.Equal("GB", OrielApp.LinuxRegion("en_GB"));
        Assert.Equal("", OrielApp.LinuxRegion("C"));
        Assert.Equal("", OrielApp.LinuxRegion("C.UTF-8"));
        Assert.Equal("", OrielApp.LinuxRegion("en_"));
    }

    [Fact]
    public void LinuxLanguages_UnaliasEachEntryFirst()
    {
        var aliases = new Dictionary<string, string> { ["english"] = "en_US.ISO-8859-1", ["deutsch"] = "de_DE.ISO-8859-1" };
        Assert.Equal(["en-US", "en-US", "en", "en", "fr"],
            OrielApp.LinuxLanguages(Env(("LANGUAGE", "english:fr")), a => aliases.GetValueOrDefault(a)));
        Assert.Equal(["de-DE", "de-DE", "de", "de"], OrielApp.LinuxLanguages(Env(("LANG", "deutsch")), a => aliases.GetValueOrDefault(a)));
    }

    [Fact]
    public void Unalias_FollowsTheChainButStopsOnALoop()
    {
        var chain = new Dictionary<string, string> { ["a"] = "b", ["b"] = "c", ["c"] = "c" };
        Assert.Equal("c", OrielApp.Unalias("a", k => chain.GetValueOrDefault(k)));
        Assert.Equal("z", OrielApp.Unalias("z", k => chain.GetValueOrDefault(k)));
        var loop = new Dictionary<string, string> { ["x"] = "y", ["y"] = "x" };
        Assert.Contains(OrielApp.Unalias("x", k => loop.GetValueOrDefault(k)), new[] { "x", "y" });
    }

    [Fact]
    public void ReadAliases_TakesTheFirstEntryAndSkipsComments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# comment\nenglish\ten_US.ISO-8859-1\n\nenglish en_GB\nbad\nfrench   fr_FR.ISO-8859-1  \n");
            var aliases = OrielApp.ReadAliases(path);
            Assert.Equal(new Dictionary<string, string> { ["english"] = "en_US.ISO-8859-1", ["french"] = "fr_FR.ISO-8859-1" }, aliases);
        }
        finally { File.Delete(path); }
        Assert.Empty(OrielApp.ReadAliases(path));
    }

    [Fact]
    public void CountryCodeOrEmpty_NeedsExactlyTwoCharacters()
    {
        Assert.Equal("US", OrielApp.CountryCodeOrEmpty("US"));
        Assert.Equal("", OrielApp.CountryCodeOrEmpty(""));
        Assert.Equal("", OrielApp.CountryCodeOrEmpty("U"));
        Assert.Equal("", OrielApp.CountryCodeOrEmpty("USA"));
    }

    [Fact]
    public void ParseMultiString_SplitsOnNulAndDropsTheTerminator()
    {
        Assert.Equal(["en-US", "de-DE"], WindowsLocale.ParseMultiString("en-US\0de-DE\0\0"));
        Assert.Equal(["fr"], WindowsLocale.ParseMultiString("fr\0\0"));
        Assert.Empty(WindowsLocale.ParseMultiString("\0\0"));
    }

    [Theory]
    [InlineData("en-US", "en-US")]
    [InlineData("zh-CN", "zh-CN")]
    [InlineData("sr-Latn-RS", "sr-Latn-RS")]
    [InlineData("SR-latn-rs", "sr-Latn-RS")]
    [InlineData("es-419", "es-419")]
    [InlineData("de-DE_phoneb", "de-DE@phoneb")]
    [InlineData("de-DE_PHONEB", "de-DE@phoneb")]
    [InlineData("ca-ES-valencia", "ca-ES@valencia")]
    [InlineData("fr", "fr")]
    [InlineData("", "en-US")]
    public void IcuLocale_PrintsLanguageScriptRegionVariant(string windows, string expected)
        => Assert.Equal(expected, WindowsLocale.IcuLocale(windows));
}
