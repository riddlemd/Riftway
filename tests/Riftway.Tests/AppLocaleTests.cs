using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class AppLocaleTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
        => key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void LinuxLanguages_ExpandLikeGLib()
    {
        Assert.Equal(["en-US", "en-US", "en", "en"], RiftwayApp.LinuxLanguages(Env(("LANG", "en_US.UTF-8"))));
        Assert.Equal(["de-DE", "de", "fr"], RiftwayApp.LinuxLanguages(Env(("LANGUAGE", "de_DE:fr"), ("LC_ALL", "en_US"))));
        Assert.Equal(["sr-RS", "sr", "sr-RS", "sr"], RiftwayApp.LinuxLanguages(Env(("LC_ALL", "sr_RS@latin"), ("LANG", "en_US"))));
        Assert.Equal(["pt-BR", "pt"], RiftwayApp.LinuxLanguages(Env(("LANG", "en_US"), ("LC_MESSAGES", "pt_BR"))));
        Assert.Empty(RiftwayApp.LinuxLanguages(Env(("LANG", "C"))));
        Assert.Empty(RiftwayApp.LinuxLanguages(Env()));
    }

    [Fact]
    public void WithLinuxLang_StandsInForLcAllOnly()
    {
        Assert.Equal(["de-DE"], RiftwayApp.LinuxLanguages(RiftwayApp.WithLinuxLang(Env(("LC_ALL", "en_US"), ("LANG", "fr_FR")), "de-DE")));
        Assert.Equal(["fr-FR", "fr"], RiftwayApp.LinuxLanguages(RiftwayApp.WithLinuxLang(Env(("LANGUAGE", "fr_FR")), "de-DE")));
        Assert.Equal(["en-US", "en"], RiftwayApp.LinuxLanguages(RiftwayApp.WithLinuxLang(Env(("LC_ALL", "en_US")), "")));
        Assert.Equal("fr_FR", RiftwayApp.WithLinuxLang(Env(("LANG", "fr_FR")), "de-DE")("LANG"));
    }

    [Fact]
    public void LinuxConfiguredLocale_PrintsIcuDefault()
    {
        Assert.Equal("en-US", RiftwayApp.LinuxConfiguredLocale(Env(("LANG", "en_US.UTF-8"))));
        Assert.Equal("fr-CA", RiftwayApp.LinuxConfiguredLocale(Env(("LANG", "en_US"), ("LC_MESSAGES", "fr_CA"))));
        Assert.Equal("ja-JP", RiftwayApp.LinuxConfiguredLocale(Env(("LC_ALL", "ja_JP"), ("LC_MESSAGES", "fr_CA"))));
        Assert.Equal("de-DE@euro", RiftwayApp.LinuxConfiguredLocale(Env(("LANG", "de_DE@EURO"))));
        Assert.Equal("en-US@posix", RiftwayApp.LinuxConfiguredLocale(Env(("LANG", "C.UTF-8"))));
        Assert.Equal("en-US@posix", RiftwayApp.LinuxConfiguredLocale(Env(("LANG", "POSIX"))));
        Assert.Equal("en-US@posix", RiftwayApp.LinuxConfiguredLocale(Env()));
        Assert.Equal("de", RiftwayApp.LinuxConfiguredLocale(Env(("LANG", "de"))));
    }

    [Fact]
    public void LinuxTimeLocale_ReadsLcAllThenLcTimeThenLang()
    {
        Assert.Equal("en_US.UTF-8", RiftwayApp.LinuxTimeLocale(Env(("LANG", "en_US.UTF-8"))));
        Assert.Equal("en_GB", RiftwayApp.LinuxTimeLocale(Env(("LANG", "en_US"), ("LC_TIME", "en_GB"))));
        Assert.Equal("ja_JP", RiftwayApp.LinuxTimeLocale(Env(("LC_ALL", "ja_JP"), ("LC_TIME", "en_GB"))));
        Assert.Equal("C", RiftwayApp.LinuxTimeLocale(Env()));
    }

    [Fact]
    public void LinuxRegion_IsTheTerritory()
    {
        Assert.Equal("US", RiftwayApp.LinuxRegion("en_US.UTF-8"));
        Assert.Equal("GB", RiftwayApp.LinuxRegion("en_GB"));
        Assert.Equal("", RiftwayApp.LinuxRegion("C"));
        Assert.Equal("", RiftwayApp.LinuxRegion("C.UTF-8"));
        Assert.Equal("", RiftwayApp.LinuxRegion("en_"));
    }

    [Fact]
    public void LinuxLanguages_UnaliasEachEntryFirst()
    {
        var aliases = new Dictionary<string, string> { ["english"] = "en_US.ISO-8859-1", ["deutsch"] = "de_DE.ISO-8859-1" };
        Assert.Equal(["en-US", "en-US", "en", "en", "fr"],
            RiftwayApp.LinuxLanguages(Env(("LANGUAGE", "english:fr")), a => aliases.GetValueOrDefault(a)));
        Assert.Equal(["de-DE", "de-DE", "de", "de"], RiftwayApp.LinuxLanguages(Env(("LANG", "deutsch")), a => aliases.GetValueOrDefault(a)));
    }

    [Fact]
    public void Unalias_FollowsTheChainButStopsOnALoop()
    {
        var chain = new Dictionary<string, string> { ["a"] = "b", ["b"] = "c", ["c"] = "c" };
        Assert.Equal("c", RiftwayApp.Unalias("a", k => chain.GetValueOrDefault(k)));
        Assert.Equal("z", RiftwayApp.Unalias("z", k => chain.GetValueOrDefault(k)));
        var loop = new Dictionary<string, string> { ["x"] = "y", ["y"] = "x" };
        Assert.Contains(RiftwayApp.Unalias("x", k => loop.GetValueOrDefault(k)), new[] { "x", "y" });
    }

    [Fact]
    public void ReadAliases_TakesTheFirstEntryAndSkipsComments()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "# comment\nenglish\ten_US.ISO-8859-1\n\nenglish en_GB\nbad\nfrench   fr_FR.ISO-8859-1  \n");
            var aliases = RiftwayApp.ReadAliases(path);
            Assert.Equal(new Dictionary<string, string> { ["english"] = "en_US.ISO-8859-1", ["french"] = "fr_FR.ISO-8859-1" }, aliases);
        }
        finally { File.Delete(path); }
        Assert.Empty(RiftwayApp.ReadAliases(path));
    }

    [Fact]
    public void CountryCodeOrEmpty_NeedsExactlyTwoCharacters()
    {
        Assert.Equal("US", RiftwayApp.CountryCodeOrEmpty("US"));
        Assert.Equal("", RiftwayApp.CountryCodeOrEmpty(""));
        Assert.Equal("", RiftwayApp.CountryCodeOrEmpty("U"));
        Assert.Equal("", RiftwayApp.CountryCodeOrEmpty("USA"));
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
