using System.Runtime.Versioning;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

// The plan functions make no GTK call, so they run anywhere.
[SupportedOSPlatform("linux")]
public sealed class LinuxMessageBoxTests
{
    private static GtkMessageBoxSettings Settings(params string[] buttons)
        => new(0, buttons, -1, 0, "", "Message", "", "", false, null, 0);

    [Theory]
    [InlineData(0, LinuxMessageBox.MessageOther)]
    [InlineData(1, LinuxMessageBox.MessageInfo)]
    [InlineData(2, LinuxMessageBox.MessageWarning)]
    [InlineData(3, LinuxMessageBox.MessageError)]
    [InlineData(4, LinuxMessageBox.MessageQuestion)]
    public void MessageTypeOf_FollowsElectronsGetMessageType(int type, int expected)
        => Assert.Equal(expected, LinuxMessageBox.MessageTypeOf(type));

    [Fact]
    public void MessageTypes_AreGtksValues()
        => Assert.Equal((0, 1, 2, 3, 4), (LinuxMessageBox.MessageInfo, LinuxMessageBox.MessageWarning, LinuxMessageBox.MessageQuestion,
            LinuxMessageBox.MessageError, LinuxMessageBox.MessageOther));

    [Theory]
    [InlineData("Cancel", "_Cancel")]
    [InlineData("CANCEL", "_Cancel")]
    [InlineData("no", "_No")]
    [InlineData("No", "_No")]
    [InlineData("ok", "_Ok")]
    [InlineData("OK", "_Ok")]
    [InlineData("YeS", "_Yes")]
    [InlineData("Save", null)]
    [InlineData("Okay", null)]
    [InlineData("", null)]
    // ToLowerASCII leaves non-ASCII letters alone, so the Kelvin sign does not make "ok" as a full lowering would.
    [InlineData("OK", null)]
    public void StockMsgIdOf_FollowsElectronsTranslateToStock(string label, string? expected)
        => Assert.Equal(expected, LinuxMessageBox.StockMsgIdOf(label));

    [Fact]
    public void PlanOf_AddsTheButtonsInOrderWithTheirIndexAsResponse()
    {
        var plan = LinuxMessageBox.PlanOf(Settings("Save", "cancel", "Don't Save"));
        Assert.Equal([new("Save", false, 0), new("_Cancel", true, 1), new("Don't Save", false, 2)], plan.Buttons);
    }

    [Fact]
    public void PlanOf_WithoutButtonsAddsAStockOkAnsweringZero()
        => Assert.Equal([new GtkDialogButton("_Ok", true, 0)], LinuxMessageBox.PlanOf(Settings()).Buttons);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(7)]
    public void PlanOf_PassesDefaultIdToGtkAsIs(int defaultId)
        => Assert.Equal(defaultId, LinuxMessageBox.PlanOf(Settings("A", "B") with { DefaultId = defaultId }).DefaultResponse);

    [Fact]
    public void PlanOf_LeavesAnEmptyTitleDetailAndCheckboxUnset()
    {
        var plan = LinuxMessageBox.PlanOf(Settings("A"));
        Assert.Equal((null, null, null, false), (plan.Title, plan.SecondaryText, plan.CheckboxLabel, plan.Transient));
    }

    [Fact]
    public void PlanOf_SetsTheTitleDetailCheckboxAndParent()
    {
        var plan = LinuxMessageBox.PlanOf(Settings("A") with
        {
            Type = 2, Title = "T", Detail = "D", CheckboxLabel = "C", CheckboxChecked = true, Parent = 42,
        });
        Assert.Equal((LinuxMessageBox.MessageWarning, "T", "D", "C", true, true),
            (plan.MessageType, plan.Title, plan.SecondaryText, plan.CheckboxLabel, plan.CheckboxChecked, plan.Transient));
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(2, 3, 2)]
    [InlineData(-1, 3, 3)]
    [InlineData(-4, 1, 1)]
    public void ResponseOf_MapsGtksNegativeResponsesToTheCancelId(int response, int cancelId, int expected)
        => Assert.Equal(expected, LinuxMessageBox.ResponseOf(response, cancelId));

    [Fact]
    public void ErrorBoxSettingsOf_IsElectronsShowErrorBox()
    {
        var settings = LinuxMessageBox.ErrorBoxSettingsOf("Bad", "Broke");
        var plan = LinuxMessageBox.PlanOf(settings);
        Assert.Equal((LinuxMessageBox.MessageError, "Error", "Bad", "Broke", 0), (plan.MessageType, plan.Title, settings.Message, plan.SecondaryText, settings.Parent));
        Assert.Equal([new GtkDialogButton("_Ok", true, 0)], plan.Buttons);
    }

    [Fact]
    public void ErrorBoxSettingsOf_TreatsNullAsEmpty()
    {
        var settings = LinuxMessageBox.ErrorBoxSettingsOf(null, null);
        Assert.Equal(("", ""), (settings.Message, settings.Detail));
    }

    [Fact]
    public void ErrorBoxConsoleText_IsElectronsAnsiOutput()
        => Assert.Equal("\u001b[1m\u001b[47m\u001b[31mBad\n\u001b[30mBroke\n\u001b[0m", LinuxMessageBox.ErrorBoxConsoleText("Bad", "Broke"));
}
