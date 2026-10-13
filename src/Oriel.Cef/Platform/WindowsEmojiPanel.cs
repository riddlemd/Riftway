using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Opens Windows' emoji picker as Chromium's <c>ui::ShowEmojiPanel</c> does (<c>emoji_panel_helper_win.cc</c>).</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsEmojiPanel
{
    private const uint InputKeyboard = 1, KeyUp = 0x0002;
    private const ushort VkLeftWindows = 0x5B, VkOemPeriod = 0xBE;

    /// <summary>Sends Win+. through <c>SendInput</c>: Windows has no API that opens the picker.</summary>
    public static void Show()
    {
        var keys = stackalloc Input[4];
        keys[0] = Key(VkLeftWindows, 0);
        keys[1] = Key(VkOemPeriod, 0);
        keys[2] = Key(VkLeftWindows, KeyUp);
        keys[3] = Key(VkOemPeriod, KeyUp);
        _ = SendInput(4, keys, sizeof(Input));
    }

    private static Input Key(ushort virtualKey, uint flags) => new() { Type = InputKeyboard, Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } };

    // INPUT's union is as large as MOUSEINPUT, its biggest member; SendInput rejects a smaller cbSize.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Keyboard;
        private readonly long _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input* inputs, int size);
}
