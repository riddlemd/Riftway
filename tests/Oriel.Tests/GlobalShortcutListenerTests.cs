namespace Oriel.Tests;

public sealed class GlobalShortcutListenerTests
{
    // The OS side recorded: which keys are held, every start and stop, and keys the OS refuses.
    private sealed class FakeListener : HotKeyListener
    {
        public readonly HashSet<HotKey> Held = [];
        public readonly List<string> Log = [];
        public readonly HashSet<HotKey> Refused = [];
        public readonly HashSet<HotKey> Vetoed = [];

        protected override T OnUi<T>(Func<T> func) => func();

        protected override bool CanRegister(HotKey key) => !Vetoed.Contains(key);

        protected override bool StartListening(HotKey key)
        {
            Log.Add($"start {key.VirtualKey:X}");
            if (Refused.Contains(key)) return false;
            Held.Add(key);
            return true;
        }

        protected override void StopListening(HotKey key)
        {
            Log.Add($"stop {key.VirtualKey:X}");
            Held.Remove(key);
        }

        public void Press(HotKey reported) => Pressed(reported);

        public void PressAsRegistered(HotKey key) => PressedAsRegistered(key);
    }

    private static HotKey K(string accelerator) => GlobalShortcuts.Key(accelerator);

    [Fact]
    public void AltGr_IsAModifierOfItsOwn()
    {
        Assert.Equal(AcceleratorModifiers.AltGr | AcceleratorModifiers.Control, Accelerator.Parse("Ctrl+AltGr+K", macOS: false).Modifiers);
        Assert.Equal(AcceleratorModifiers.AltGr, Accelerator.Parse("altgr+K", macOS: true).Modifiers);
        Assert.NotEqual(K("AltGr+K"), K("Alt+K"));
        Assert.NotEqual(K("AltGr+K"), K("K"));
        // Chromium's listeners never pass AltGr to the OS.
        Assert.Equal(new HotKey(0x2, 0x4B, AltGr: true), K("Ctrl+AltGr+K"));
        Assert.Equal(0, X11Shortcut(K("AltGr+K")));
    }

    private static ushort X11Shortcut(HotKey key) => GlobalShortcuts.X11Modifiers(key.Modifiers);

    [Fact]
    public void Menus_StillReadAltGrAsAlt()
    {
        Assert.Equal(AcceleratorModifiers.Alt | AcceleratorModifiers.Shift, (AcceleratorModifiers.AltGr | AcceleratorModifiers.Shift).ForMenus());
        Assert.Equal(AcceleratorModifiers.Control, AcceleratorModifiers.Control.ForMenus());
    }

    [Fact]
    public void RegisterAll_AllFree_RegistersEachAndIsTrue()
    {
        var l = new FakeListener();
        Assert.True(GlobalShortcuts.RegisterAll(l, [K("Ctrl+A"), K("Ctrl+B")], () => { }));
        Assert.True(l.IsRegistered(K("Ctrl+A")) && l.IsRegistered(K("Ctrl+B")));
    }

    [Fact]
    public void RegisterAll_OneRefused_UnregistersOnlyThoseThisCallRegistered()
    {
        var l = new FakeListener();
        Assert.True(l.Register(K("Ctrl+D"), () => { }));
        l.Refused.Add(K("Ctrl+C"));
        Assert.False(GlobalShortcuts.RegisterAll(l, [K("Ctrl+A"), K("Ctrl+B"), K("Ctrl+C"), K("Ctrl+E")], () => { }));
        Assert.False(l.IsRegistered(K("Ctrl+A")));
        Assert.False(l.IsRegistered(K("Ctrl+B")));
        Assert.False(l.IsRegistered(K("Ctrl+E")));
        Assert.True(l.IsRegistered(K("Ctrl+D")));
        Assert.Equal([K("Ctrl+D")], l.Held);
        Assert.DoesNotContain("start 45", l.Log);
    }

    [Fact]
    public void RegisterAll_AKeyThisAppAlreadyHolds_FailsAndKeepsIt()
    {
        var l = new FakeListener();
        l.Register(K("Ctrl+B"), () => { });
        Assert.False(GlobalShortcuts.RegisterAll(l, [K("Ctrl+A"), K("Ctrl+B")], () => { }));
        Assert.False(l.IsRegistered(K("Ctrl+A")));
        Assert.True(l.IsRegistered(K("Ctrl+B")) && l.Held.Contains(K("Ctrl+B")));
    }

    [Fact]
    public void RegisterAll_ADuplicate_RollsBackItsFirstCopy()
    {
        var l = new FakeListener();
        Assert.False(GlobalShortcuts.RegisterAll(l, [K("Ctrl+A"), K("Control+A")], () => { }));
        Assert.False(l.IsRegistered(K("Ctrl+A")));
        Assert.Empty(l.Held);
    }

    [Fact]
    public void RegisterAll_AnInvalidAccelerator_ThrowsBeforeRegisteringAny()
        => Assert.Throws<ArgumentException>(() => GlobalShortcut.RegisterAll(["Ctrl+A", "Ctrl+Nope"], () => { }));

    [Fact]
    public void SetSuspended_ReleasesAndRetakesEveryRegistration()
    {
        var l = new FakeListener();
        l.Register(K("Ctrl+A"), () => { });
        l.Register(K("Ctrl+B"), () => { });
        l.SetSuspended(true);
        Assert.True(l.IsSuspended());
        Assert.Empty(l.Held);
        Assert.True(l.IsRegistered(K("Ctrl+A")) && l.IsRegistered(K("Ctrl+B")));
        l.Log.Clear();
        l.SetSuspended(true);
        Assert.Empty(l.Log);
        l.SetSuspended(false);
        Assert.False(l.IsSuspended());
        Assert.Equal([K("Ctrl+A"), K("Ctrl+B")], l.Held.OrderBy(k => k.VirtualKey));
    }

    [Fact]
    public void WhileSuspended_RegisterFailsWithoutTouchingTheOs_AndUnregisterReleasesNothing()
    {
        var l = new FakeListener();
        l.Register(K("Ctrl+A"), () => { });
        l.SetSuspended(true);
        l.Log.Clear();
        Assert.False(l.Register(K("Ctrl+B"), () => { }));
        l.Unregister(K("Ctrl+A"));
        Assert.Empty(l.Log);
        Assert.False(l.IsRegistered(K("Ctrl+A")));
        l.SetSuspended(false);
        Assert.Empty(l.Held);
    }

    [Fact]
    public void Resume_KeepsAShortcutTheOsNowRefuses()
    {
        var l = new FakeListener();
        l.Register(K("Ctrl+A"), () => { });
        l.SetSuspended(true);
        l.Refused.Add(K("Ctrl+A"));
        l.SetSuspended(false);
        Assert.True(l.IsRegistered(K("Ctrl+A")));
        Assert.Empty(l.Held);
    }

    [Fact]
    public void Pressed_FindsTheShortcutWithoutAltGr_SoAnAltGrShortcutNeverFires()
    {
        var l = new FakeListener();
        var fired = new List<string>();
        l.Register(K("AltGr+K"), () => fired.Add("altgr"));
        l.Press(K("AltGr+K"));
        Assert.Empty(fired);
        l.Register(K("K"), () => fired.Add("plain"));
        l.Press(K("AltGr+K"));
        l.Press(K("K"));
        Assert.Equal(["plain", "plain"], fired);
    }

    [Fact]
    public void PressedAsRegistered_FiresTheAltGrShortcutItself_AsChromiumsMacListener()
    {
        var l = new FakeListener();
        var fired = new List<string>();
        l.Register(K("AltGr+K"), () => fired.Add("altgr"));
        l.PressAsRegistered(K("K"));
        Assert.Empty(fired);
        l.PressAsRegistered(K("AltGr+K"));
        Assert.Equal(["altgr"], fired);
    }

    [Fact]
    public void CanRegister_False_FailsBeforeTheOsIsAsked()
    {
        var l = new FakeListener();
        l.Vetoed.Add(K("MediaPlayPause"));
        Assert.False(l.Register(K("MediaPlayPause"), () => { }));
        Assert.Empty(l.Log);
        Assert.False(l.IsRegistered(K("MediaPlayPause")));
        Assert.True(l.Register(K("Shift+MediaPlayPause"), () => { }));
    }
}
