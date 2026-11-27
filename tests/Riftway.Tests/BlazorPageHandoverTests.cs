using Riftway.Blazor;
using Xunit;

namespace Riftway.Tests;

/// <summary>A reloaded page takes over a view whose old page left JS calls behind: those are answered by the view,
/// never sent to the new page, and the new page's own calls go out as usual.</summary>
public class BlazorPageHandoverTests
{
    private static string Begin(long handle, string identifier = "dispose", long target = 3)
        => $"__bwv:[\"BeginInvokeJS\",{handle},\"{identifier}\",\"[]\",0,{target},0]";

    private static string End(long handle) => $"__bwv:[\"EndInvokeJS\",{handle},true,\"[{handle},true,null]\"]";

    private const string AttachPage = "__bwv:[\"AttachPage\",\"app://localhost/\",\"app://localhost/settings\"]";
    private const string AttachToDocument = "__bwv:[\"AttachToDocument\",0,\"#app\"]";
    private const string RenderBatch = "__bwv:[\"RenderBatch\",7,\"AAAA\"]";

    private static PageHandover Attached()
    {
        var handover = new PageHandover();
        handover.FromPage(AttachPage);
        handover.ToPage(AttachToDocument, out _);
        return handover;
    }

    [Fact]
    public void A_live_page_gets_every_call()
    {
        var handover = Attached();

        Assert.True(handover.ToPage(Begin(1), out var answer));
        Assert.Null(answer);
        Assert.True(handover.ToPage(RenderBatch, out _));
    }

    [Fact]
    public void Calls_the_old_page_makes_while_tidying_up_are_answered_here_and_not_sent()
    {
        var handover = Attached();

        Assert.Empty(handover.FromPage(AttachPage));

        Assert.False(handover.ToPage(Begin(5), out var answer));
        Assert.Equal(End(5), answer);
    }

    [Fact]
    public void A_call_that_wants_no_answer_is_dropped_without_one()
    {
        var handover = Attached();
        handover.FromPage(AttachPage);

        Assert.False(handover.ToPage(Begin(0), out var answer));
        Assert.Null(answer);
    }

    [Fact]
    public void Calls_the_old_page_left_unanswered_are_answered_when_the_new_page_attaches()
    {
        var handover = Attached();
        handover.ToPage(Begin(1), out _);
        handover.ToPage(Begin(2), out _);
        handover.ToPage(Begin(3), out _);
        handover.FromPage(End(2));

        Assert.Equal([End(1), End(3)], handover.FromPage(AttachPage));
    }

    [Fact]
    public void The_new_pages_calls_go_out_once_its_renderer_attaches()
    {
        var handover = Attached();
        handover.FromPage(AttachPage);
        Assert.True(handover.HandingOver);

        Assert.True(handover.ToPage(AttachToDocument, out _));

        Assert.False(handover.HandingOver);
        Assert.True(handover.ToPage(Begin(1), out var answer));
        Assert.Null(answer);
    }

    /// <summary>Handles restart at 1 on each page's runtime, so a handle the old page used is the new page's too.</summary>
    [Fact]
    public void An_answered_old_call_is_not_answered_again_at_a_later_reload()
    {
        var handover = Attached();
        handover.ToPage(Begin(1), out _);
        handover.FromPage(AttachPage);
        handover.ToPage(AttachToDocument, out _);

        Assert.Empty(handover.FromPage(AttachPage));
    }

    /// <summary>The new renderer's first call, made before any component is attached, must reach the page: without
    /// it the page's events never reach .NET.</summary>
    [Fact]
    public void The_new_renderers_first_call_goes_out_and_ends_the_handover()
    {
        var handover = Attached();
        handover.FromPage(AttachPage);

        Assert.True(handover.ToPage(Begin(2, "Blazor._internal.attachWebRendererInterop", 0), out var answer));

        Assert.Null(answer);
        Assert.False(handover.HandingOver);
        Assert.True(handover.ToPage(Begin(3, "eval"), out _));
    }

    [Fact]
    public void The_first_page_load_attaches_like_a_reload()
    {
        var handover = new PageHandover();

        Assert.Empty(handover.FromPage(AttachPage));
        Assert.True(handover.HandingOver);
        Assert.True(handover.ToPage(AttachToDocument, out _));
        Assert.False(handover.HandingOver);
    }

    [Theory]
    [InlineData("not a blazor message")]
    [InlineData("__bwv:[\"BeginInvokeJS\"")]
    [InlineData("__bwv:[\"BeginInvokeJS\",\"x\"]")]
    public void A_message_it_cannot_read_is_sent_as_it_is(string message)
    {
        var handover = Attached();
        handover.FromPage(AttachPage);

        Assert.True(handover.ToPage(message, out var answer));
        Assert.Null(answer);
    }
}
