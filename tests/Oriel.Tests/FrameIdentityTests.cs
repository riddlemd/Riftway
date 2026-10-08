namespace Oriel.Tests;

public sealed class FrameIdentityTests
{
    private const string Token = "A7E45C4B2B4D1E9F59C692726EFD13EA";

    [Fact]
    public void Parse_ReadsTheProcessIdInHex_AndTheToken()
    {
        Assert.Equal((6, Token), FrameIdentity.Parse("6-" + Token));
        Assert.Equal((10, Token), FrameIdentity.Parse("A-" + Token));
        Assert.Equal((19, Token), FrameIdentity.Parse("13-" + Token));
        Assert.Equal((0, Token), FrameIdentity.Parse("0-" + Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Token)]
    [InlineData("-" + Token)]
    [InlineData("6-")]
    [InlineData("6-A7E45C4B2B4D1E9F59C692726EFD13E")]
    [InlineData("6-A7E45C4B2B4D1E9F59C692726EFD13EAB")]
    [InlineData("6-a7e45c4b2b4d1e9f59c692726efd13ea")]
    [InlineData("6-G7E45C4B2B4D1E9F59C692726EFD13EA")]
    [InlineData("G-" + Token)]
    [InlineData("80000000-" + Token)]
    public void Parse_RejectsWhatIsNotAnIdentifier(string? identifier) => Assert.Null(FrameIdentity.Parse(identifier));

    [Fact]
    public void RendererClientId_IsReadFromARenderersArguments()
    {
        Assert.Equal(12, FrameIdentity.RendererClientId(["helper", "--type=renderer", "--lang=en", "--renderer-client-id=12"]));
        Assert.Null(FrameIdentity.RendererClientId(["helper", "--type=gpu-process", "--renderer-client-id=12"]));
        Assert.Null(FrameIdentity.RendererClientId(["helper", "--type=renderer"]));
        Assert.Null(FrameIdentity.RendererClientId(["helper", "--type=renderer", "--renderer-client-id=x"]));
        Assert.Null(FrameIdentity.RendererClientId(["helper", "--type=renderer", "--renderer-client-id=-1"]));
    }

    [Fact]
    public void UnnamedSlot_IsTheFramesCreationSlot()
    {
        Assert.Equal(0, FrameIdentity.UnnamedSlot("<!--framePath //<!--frame0-->-->"));
        Assert.Equal(12, FrameIdentity.UnnamedSlot("<!--framePath //<!--frame1-->/<!--frame12-->-->"));
        Assert.Null(FrameIdentity.UnnamedSlot("n1"));
        Assert.Null(FrameIdentity.UnnamedSlot("named<!--frame3-->"));
        Assert.Null(FrameIdentity.UnnamedSlot("<!--framePath //-->"));
    }

    private static CefFrameInfo Child(string id, string name, string url) => new(id, "p", name, url, false);

    [Fact]
    public void MatchChild_FindsTheFrameByUrlAndName()
    {
        var children = new[] { Child("1", "<!--framePath //<!--frame0-->-->", "about:srcdoc"), Child("2", "n1", "about:blank"), Child("3", "n2", "about:blank") };
        Assert.Equal("1", FrameIdentity.MatchChild(children, "", "about:srcdoc", 0));
        Assert.Equal("3", FrameIdentity.MatchChild(children, "n2", "about:blank", 0));
        Assert.Null(FrameIdentity.MatchChild(children, "n3", "about:blank", 0));
        Assert.Null(FrameIdentity.MatchChild(children, "", "about:blank", 0));
        Assert.Null(FrameIdentity.MatchChild(children, "n1", "about:srcdoc", 0));
    }

    [Fact]
    public void MatchChild_TellsLookAlikesApartByCreationSlot()
    {
        // Listed out of creation order, as CEF's identifier list may be.
        var children = new[]
        {
            Child("b", "<!--framePath //<!--frame2-->-->", "about:srcdoc"),
            Child("a", "<!--framePath //<!--frame0-->-->", "about:srcdoc"),
            Child("x", "n", "about:srcdoc"),
        };
        Assert.Equal("a", FrameIdentity.MatchChild(children, "", "about:srcdoc", 0));
        Assert.Equal("b", FrameIdentity.MatchChild(children, "", "about:srcdoc", 1));
        Assert.Null(FrameIdentity.MatchChild(children, "", "about:srcdoc", 2));
        Assert.Null(FrameIdentity.MatchChild(children, "", "about:srcdoc", -1));
    }

    [Fact]
    public void MatchUnseen_TakesTheUntakenChildrenInOtherProcesses_InCreationOrder()
    {
        const string Seen = "6-" + Token, Late = "B-" + Token, Early = "C-" + Token, Uncommitted = "6-0123456789ABCDEF0123456789ABCDEF";
        const string SeenElsewhere = "D-" + Token;
        var children = new[]
        {
            Child(Seen, "<!--framePath //<!--frame0-->-->", "about:srcdoc"),
            Child(SeenElsewhere, "<!--framePath //<!--frame0-->-->", "https://c.test/"),
            Child(Late, "<!--framePath //<!--frame3-->-->", "https://b.test/"),
            Child(Uncommitted, "<!--framePath //<!--frame2-->-->", "about:blank"),
            Child(Early, "<!--framePath //<!--frame1-->-->", "https://a.test/"),
        };
        var taken = new HashSet<string> { Seen, SeenElsewhere };
        Assert.Equal(Early, FrameIdentity.MatchUnseen(children, taken, 6, 0));
        Assert.Equal(Late, FrameIdentity.MatchUnseen(children, taken, 6, 1));
        Assert.Null(FrameIdentity.MatchUnseen(children, taken, 6, 2));
        Assert.Null(FrameIdentity.MatchUnseen(children, taken, 6, -1));
        Assert.Equal(Seen, FrameIdentity.MatchUnseen(children, new HashSet<string>(), 12, 0));
    }

    [Fact]
    public void MatchChild_TakesAnEmptyOrGeneratedCefNameAsUnnamed()
    {
        Assert.Equal("1", FrameIdentity.MatchChild([Child("1", "", "about:blank")], "", "about:blank", 0));
        Assert.Equal("2", FrameIdentity.MatchChild([Child("2", "<!--dynamicFrame12AB-->", "about:blank")], "", "about:blank", 0));
        Assert.Null(FrameIdentity.MatchChild([Child("3", "<!-", "about:blank")], "", "about:blank", 0));
    }
}
