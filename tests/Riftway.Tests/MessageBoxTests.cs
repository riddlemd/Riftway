using System.Runtime.Versioning;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Pure option logic only; the plan functions touch no window, so they run anywhere.
[SupportedOSPlatform("windows")]
public sealed unsafe class MessageBoxTests
{
    private static WindowsMessageBoxSettings Settings(params string[] buttons) => new() { Buttons = buttons, AppName = "App" };

    private static WindowsMessageBoxSettings Of(MessageBoxOptions options, string platform = "win32") => Dialog.MessageBoxSettingsOf(options, platform, "App");

    [Theory]
    [InlineData(new[] { "OK", "Cancel" }, -1, 1)]
    [InlineData(new[] { "Yes", "NO", "Cancel" }, -1, 1)]
    [InlineData(new[] { "CANCEL", "no" }, -1, 0)]
    [InlineData(new[] { "Save", "Discard" }, -1, 0)]
    [InlineData(new[] { "Save", "Discard" }, 0, 1)]
    [InlineData(new[] { "Save" }, 0, 0)]
    [InlineData(new string[0], 0, 0)]
    [InlineData(new[] { "Save", "Discard" }, 1, 0)]
    [InlineData(new[] { "No way", "Cancel it" }, 0, 1)]
    public void CancelIdOf_FollowsElectron(string[] buttons, int defaultId, int expected)
        => Assert.Equal(expected, Dialog.CancelIdOf(buttons, defaultId));

    [Fact]
    public void SettingsOf_KeepsAnExplicitCancelId()
        => Assert.Equal(0, Of(new MessageBoxOptions { Buttons = ["OK", "Cancel"], CancelId = 0 }).CancelId);

    [Fact]
    public void SettingsOf_DetectsCancelIdWhenNoneIsGiven()
        => Assert.Equal(1, Of(new MessageBoxOptions { Buttons = ["OK", "Cancel"] }).CancelId);

    [Theory]
    [InlineData("darwin", "&Save", "Save")]
    [InlineData("darwin", "Fish && Chips", "Fish & Chips")]
    [InlineData("darwin", "a&&&b", "a&b")]
    [InlineData("linux", "&Save", "_Save")]
    [InlineData("linux", "Fish && Chips", "Fish & Chips")]
    [InlineData("linux", "snake_case", "snake__case")]
    [InlineData("linux", "end&", "end_")]
    [InlineData("win32", "&Save && _x", "&Save && _x")]
    public void NormalizeAccessKey_PerPlatform(string platform, string text, string expected)
        => Assert.Equal(expected, Dialog.NormalizeAccessKey(text, platform));

    [Fact]
    public void SettingsOf_NormalizesOnlyWhenAsked()
    {
        Assert.Equal(["&Save"], Of(new MessageBoxOptions { Buttons = ["&Save"] }, "darwin").Buttons);
        Assert.Equal(["Save"], Of(new MessageBoxOptions { Buttons = ["&Save"], NormalizeAccessKeys = true }, "darwin").Buttons);
    }

    [Fact]
    public void SettingsOf_DetectsCancelIdAfterNormalizing()
        => Assert.Equal(1, Of(new MessageBoxOptions { Buttons = ["OK", "&Cancel"], NormalizeAccessKeys = true }, "darwin").CancelId);

    [Theory]
    [InlineData(MessageBoxType.None, WindowsMessageBoxType.None)]
    [InlineData(MessageBoxType.Info, WindowsMessageBoxType.Information)]
    [InlineData(MessageBoxType.Warning, WindowsMessageBoxType.Warning)]
    [InlineData(MessageBoxType.Error, WindowsMessageBoxType.Error)]
    [InlineData(MessageBoxType.Question, WindowsMessageBoxType.Question)]
    public void SettingsOf_MapsType(MessageBoxType type, WindowsMessageBoxType expected)
        => Assert.Equal(expected, Of(new MessageBoxOptions { Type = type }).Type);

    [Fact]
    public void SettingsOf_CarriesTheRest()
    {
        var s = Of(new MessageBoxOptions { Message = "m", Detail = "d", Title = "t", CheckboxLabel = "c", CheckboxChecked = true, NoLink = true, DefaultId = 2, Buttons = ["a", "b", "c"] });
        Assert.Equal(("m", "d", "t", "App", "c", true, true, 2), (s.Message, s.Detail, s.Title, s.AppName, s.CheckboxLabel, s.CheckboxChecked, s.NoLink, s.DefaultId));
    }

    [Fact]
    public void SettingsOf_RejectsWhatElectronRejects()
    {
        Assert.Equal("checkboxChecked requires that checkboxLabel also be passed (Parameter 'options')",
            Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { CheckboxChecked = true })).Message);
        Assert.StartsWith("Invalid message box type", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { Type = (MessageBoxType)5 })).Message, StringComparison.Ordinal);
        Assert.StartsWith("Buttons must be an array", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { Buttons = null! })).Message, StringComparison.Ordinal);
        Assert.StartsWith("Buttons must be strings", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { Buttons = ["a", null!] })).Message, StringComparison.Ordinal);
        Assert.StartsWith("Title must be a string", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { Title = null! })).Message, StringComparison.Ordinal);
        Assert.StartsWith("Message must be a string", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { Message = null! })).Message, StringComparison.Ordinal);
        Assert.StartsWith("Detail must be a string", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { Detail = null! })).Message, StringComparison.Ordinal);
        Assert.StartsWith("checkboxLabel must be a string", Assert.Throws<ArgumentException>(() => Of(new MessageBoxOptions { CheckboxLabel = null! })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOf_AllowsACheckedBoxWithALabel()
        => Assert.True(Of(new MessageBoxOptions { CheckboxChecked = true, CheckboxLabel = "c" }).CheckboxChecked);

    [Fact]
    public void PublicApi_NeedsTheAppReadyFirst()
    {
        // Electron checks readiness before validating, so even invalid options report it.
        var invalid = new MessageBoxOptions { CheckboxChecked = true };
        Assert.Equal("dialog module can only be used after app is ready", Assert.Throws<InvalidOperationException>(() => { _ = Dialog.ShowMessageBoxAsync(invalid); }).Message);
        Assert.Equal("dialog module can only be used after app is ready", Assert.Throws<InvalidOperationException>(() => Dialog.ShowMessageBoxSync(invalid)).Message);
        Assert.Throws<ArgumentNullException>(() => { _ = Dialog.ShowMessageBoxAsync(null!); });
    }

    [Theory]
    [InlineData("OK", WindowsMessageBox.OkButton, WindowsMessageBox.IdOk)]
    [InlineData("oK", WindowsMessageBox.OkButton, WindowsMessageBox.IdOk)]
    [InlineData("Yes", WindowsMessageBox.YesButton, WindowsMessageBox.IdYes)]
    [InlineData("NO", WindowsMessageBox.NoButton, WindowsMessageBox.IdNo)]
    [InlineData("cancel", WindowsMessageBox.CancelButton, WindowsMessageBox.IdCancel)]
    [InlineData("Retry", WindowsMessageBox.RetryButton, WindowsMessageBox.IdRetry)]
    [InlineData("Close", WindowsMessageBox.CloseButton, WindowsMessageBox.IdClose)]
    [InlineData("&OK", 0u, -1)]
    [InlineData("OK", 0u, -1)] // Kelvin sign: lowercases to k in Unicode, not in ASCII
    [InlineData("Ignore", 0u, -1)]
    public void CommonIdOf_MatchesAsciiCaseInsensitively(string label, uint button, int id)
        => Assert.Equal((button, id), WindowsMessageBox.CommonIdOf(label));

    [Fact]
    public void PlanOf_BaseFlags()
    {
        var plan = WindowsMessageBox.PlanOf(Settings(), parentEnabled: false, hasIcon: false);
        Assert.Equal(WindowsMessageBox.TdfSizeToContent | WindowsMessageBox.TdfAllowDialogCancellation, plan.Flags);
    }

    [Fact]
    public void PlanOf_PositionsOverAnEnabledParent()
        => Assert.Equal(WindowsMessageBox.TdfPositionRelativeToWindow,
            WindowsMessageBox.PlanOf(Settings(), parentEnabled: true, hasIcon: false).Flags & WindowsMessageBox.TdfPositionRelativeToWindow);

    [Fact]
    public void PlanOf_SplitsCommonAndCustomButtons()
    {
        var plan = WindowsMessageBox.PlanOf(Settings("Yes", "Maybe", "no", "Later"), false, false);
        Assert.Equal(WindowsMessageBox.YesButton | WindowsMessageBox.NoButton, plan.CommonButtons);
        Assert.Equal([new TaskDialogButton(101, "Maybe"), new TaskDialogButton(103, "Later")], plan.Buttons);
        Assert.Equal(WindowsMessageBox.TdfUseCommandLinks, plan.Flags & WindowsMessageBox.TdfUseCommandLinks);
        Assert.Equal(new Dictionary<int, int> { [WindowsMessageBox.IdYes] = 0, [WindowsMessageBox.IdNo] = 2 }, plan.IdMap);
        Assert.Equal(new HashSet<int> { WindowsMessageBox.IdYes, WindowsMessageBox.IdNo, 101, 103 }, plan.ButtonIds);
    }

    [Fact]
    public void PlanOf_OnlyCommonButtonsNeedNoCommandLinks()
    {
        var plan = WindowsMessageBox.PlanOf(Settings("OK", "Cancel"), false, false);
        Assert.Empty(plan.Buttons);
        Assert.Equal(0u, plan.Flags & WindowsMessageBox.TdfUseCommandLinks);
    }

    [Fact]
    public void PlanOf_NoLinkMakesEveryButtonPlainAndCustom()
    {
        var plan = WindowsMessageBox.PlanOf(Settings("OK", "Maybe") with { NoLink = true }, false, false);
        Assert.Equal(0u, plan.CommonButtons);
        Assert.Equal([new TaskDialogButton(100, "OK"), new TaskDialogButton(101, "Maybe")], plan.Buttons);
        Assert.Equal(0u, plan.Flags & WindowsMessageBox.TdfUseCommandLinks);
        Assert.Empty(plan.IdMap);
    }

    [Theory]
    [InlineData(new[] { "OK", "Maybe" }, 0, false, WindowsMessageBox.IdOk)]
    [InlineData(new[] { "OK", "Maybe" }, 1, false, 101)]
    [InlineData(new[] { "OK", "Maybe" }, 0, true, 100)]
    [InlineData(new[] { "OK", "Maybe" }, 2, false, 0)]
    [InlineData(new[] { "OK", "Maybe" }, -1, false, 0)]
    [InlineData(new string[0], 0, false, 0)]
    public void PlanOf_DefaultButton(string[] buttons, int defaultId, bool noLink, int expected)
        => Assert.Equal(expected, WindowsMessageBox.PlanOf(Settings(buttons) with { DefaultId = defaultId, NoLink = noLink }, false, false).DefaultButton);

    [Theory]
    [InlineData(WindowsMessageBoxType.None, 0)]
    [InlineData(WindowsMessageBoxType.Information, 0xFFFD)]
    [InlineData(WindowsMessageBoxType.Question, 0xFFFD)]
    [InlineData(WindowsMessageBoxType.Warning, 0xFFFF)]
    [InlineData(WindowsMessageBoxType.Error, 0xFFFE)]
    public void PlanOf_IconPerType(WindowsMessageBoxType type, int icon)
    {
        var plan = WindowsMessageBox.PlanOf(Settings() with { Type = type }, false, false);
        Assert.Equal((nint)icon, plan.MainIcon);
        Assert.Equal(0u, plan.Flags & WindowsMessageBox.TdfUseHiconMain);
    }

    [Fact]
    public void PlanOf_ACustomIconReplacesTheTypes()
    {
        var plan = WindowsMessageBox.PlanOf(Settings() with { Type = WindowsMessageBoxType.Error }, false, hasIcon: true);
        Assert.Equal((nint)0, plan.MainIcon);
        Assert.Equal(WindowsMessageBox.TdfUseHiconMain, plan.Flags & WindowsMessageBox.TdfUseHiconMain);
    }

    [Fact]
    public void PlanOf_MessageIsContentWithoutDetail()
    {
        var plan = WindowsMessageBox.PlanOf(Settings() with { Message = "m" }, false, false);
        Assert.Equal((null, "m"), (plan.MainInstruction, plan.Content));
    }

    [Fact]
    public void PlanOf_DetailMakesMessageTheMainInstruction()
    {
        var plan = WindowsMessageBox.PlanOf(Settings() with { Message = "m", Detail = "d" }, false, false);
        Assert.Equal(("m", "d"), (plan.MainInstruction, plan.Content));
    }

    [Fact]
    public void PlanOf_Checkbox()
    {
        Assert.Null(WindowsMessageBox.PlanOf(Settings() with { CheckboxChecked = true }, false, false).VerificationText);
        Assert.Equal(0u, WindowsMessageBox.PlanOf(Settings() with { CheckboxChecked = true }, false, false).Flags & WindowsMessageBox.TdfVerificationFlagChecked);
        var unchecked_ = WindowsMessageBox.PlanOf(Settings() with { CheckboxLabel = "c" }, false, false);
        Assert.Equal(("c", 0u), (unchecked_.VerificationText, unchecked_.Flags & WindowsMessageBox.TdfVerificationFlagChecked));
        var checked_ = WindowsMessageBox.PlanOf(Settings() with { CheckboxLabel = "c", CheckboxChecked = true }, false, false);
        Assert.Equal(WindowsMessageBox.TdfVerificationFlagChecked, checked_.Flags & WindowsMessageBox.TdfVerificationFlagChecked);
    }

    [Fact]
    public void PlanOf_TitleFallsBackToTheAppName()
    {
        Assert.Equal("App", WindowsMessageBox.PlanOf(Settings(), false, false).WindowTitle);
        Assert.Equal("T", WindowsMessageBox.PlanOf(Settings() with { Title = "T" }, false, false).WindowTitle);
    }

    [Fact]
    public void PlanOf_NoButtonsAcceptsTheDialogsOwnOk()
        => Assert.Equal(new HashSet<int> { WindowsMessageBox.IdOk }, WindowsMessageBox.PlanOf(Settings(), false, false).ButtonIds);

    [Theory]
    [InlineData(WindowsMessageBox.IdYes, 0)]
    [InlineData(WindowsMessageBox.IdNo, 2)]
    [InlineData(101, 1)]
    [InlineData(103, 3)]
    [InlineData(102, 9)] // not a button of this box
    [InlineData(WindowsMessageBox.IdCancel, 9)]
    [InlineData(WindowsMessageBox.IdOk, 9)]
    [InlineData(0, 9)] // the call failed
    public void ResponseOf_MapsIdsBackToIndexes(int id, int expected)
        => Assert.Equal(expected, WindowsMessageBox.ResponseOf(WindowsMessageBox.PlanOf(Settings("Yes", "Maybe", "No", "Later"), false, false), id, cancelId: 9));

    [Fact]
    public void ResponseOf_ACancelButtonAnswersIdCancel()
        => Assert.Equal(1, WindowsMessageBox.ResponseOf(WindowsMessageBox.PlanOf(Settings("OK", "Cancel"), false, false), WindowsMessageBox.IdCancel, cancelId: 0));

    [Fact]
    public void ResponseOf_DuplicateCommonLabelsAnswerTheLast()
        => Assert.Equal(2, WindowsMessageBox.ResponseOf(WindowsMessageBox.PlanOf(Settings("OK", "x", "ok"), false, false), WindowsMessageBox.IdOk, cancelId: 9));

    [Theory]
    [InlineData(WindowsMessageBox.IdCancel, true)]
    [InlineData(101, true)]
    [InlineData(WindowsMessageBox.IdYes, true)]
    [InlineData(102, false)]
    [InlineData(WindowsMessageBox.IdOk, false)]
    public void AcceptsClick_IgnoresIdsTheBoxNeverMade(int id, bool accepted)
        => Assert.Equal(accepted, WindowsMessageBox.AcceptsClick(WindowsMessageBox.PlanOf(Settings("Yes", "Maybe"), false, false).ButtonIds, id));

    [Fact]
    public void ErrorBox_IsElectronsFixedTaskDialog()
    {
        var plan = WindowsMessageBox.PlanOf(Dialog.ErrorBoxSettingsOf("Bad", "Broke"), false, false);
        Assert.Equal(("Error", "Bad", "Broke", WindowsMessageBox.ErrorIcon, 0u, 0), (plan.WindowTitle, plan.MainInstruction, plan.Content, plan.MainIcon, plan.CommonButtons, plan.Buttons.Count));
        Assert.Equal(0, WindowsMessageBox.ResponseOf(plan, WindowsMessageBox.IdOk, Dialog.ErrorBoxSettingsOf("Bad", "Broke").CancelId));
    }

    [Fact]
    public void TaskDialogConfig_HasCommctrlsPackedLayout()
    {
        // The offsets are the 64-bit ones.
        if (sizeof(nint) != 8) return;
        WindowsMessageBox.TaskDialogConfig c;
        var b = (byte*)&c;
        Assert.Equal(160, sizeof(WindowsMessageBox.TaskDialogConfig));
        Assert.Equal(12, sizeof(WindowsMessageBox.TaskDialogButtonNative));
        Assert.Equal(4, (byte*)&c.Parent - b);
        Assert.Equal(36, (byte*)&c.MainIcon - b);
        Assert.Equal(64, (byte*)&c.Buttons - b);
        Assert.Equal(72, (byte*)&c.DefaultButton - b);
        Assert.Equal(92, (byte*)&c.VerificationText - b);
        Assert.Equal(140, (byte*)&c.Callback - b);
        Assert.Equal(148, (byte*)&c.CallbackData - b);
        Assert.Equal(156, (byte*)&c.Width - b);
    }
}
