using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Electron v44.5.1's Linux tray: Chromium 154's StatusIconLinuxDbus and DbusMenu encodings.
public sealed class LinuxTrayTests
{
    // SkUnPreMultiply::gTable, from third_party/skia/src/core/SkUnPreMultiply.cpp.
    private static readonly uint[] s_skiaTable =
    [
        0x00000000, 0xFF000000, 0x7F800000, 0x55000000, 0x3FC00000, 0x33000000, 0x2A800000, 0x246DB6DB,
        0x1FE00000, 0x1C555555, 0x19800000, 0x172E8BA3, 0x15400000, 0x139D89D9, 0x1236DB6E, 0x11000000,
        0x0FF00000, 0x0F000000, 0x0E2AAAAB, 0x0D6BCA1B, 0x0CC00000, 0x0C249249, 0x0B9745D1, 0x0B1642C8,
        0x0AA00000, 0x0A333333, 0x09CEC4EC, 0x0971C71C, 0x091B6DB7, 0x08CB08D4, 0x08800000, 0x0839CE74,
        0x07F80000, 0x07BA2E8C, 0x07800000, 0x07492492, 0x07155555, 0x06E45307, 0x06B5E50D, 0x0689D89E,
        0x06600000, 0x063831F4, 0x06124925, 0x05EE23B9, 0x05CBA2E9, 0x05AAAAAB, 0x058B2164, 0x056CEFA9,
        0x05500000, 0x05343EB2, 0x0519999A, 0x05000000, 0x04E76276, 0x04CFB2B8, 0x04B8E38E, 0x04A2E8BA,
        0x048DB6DB, 0x0479435E, 0x0465846A, 0x045270D0, 0x04400000, 0x042E29F8, 0x041CE73A, 0x040C30C3,
        0x03FC0000, 0x03EC4EC5, 0x03DD1746, 0x03CE540F, 0x03C00000, 0x03B21643, 0x03A49249, 0x03976FC6,
        0x038AAAAB, 0x037E3F20, 0x03722983, 0x03666666, 0x035AF287, 0x034FCACE, 0x0344EC4F, 0x033A5441,
        0x03300000, 0x0325ED09, 0x031C18FA, 0x0312818B, 0x03092492, 0x03000000, 0x02F711DC, 0x02EE5847,
        0x02E5D174, 0x02DD7BAF, 0x02D55555, 0x02CD5CD6, 0x02C590B2, 0x02BDEF7C, 0x02B677D4, 0x02AF286C,
        0x02A80000, 0x02A0FD5C, 0x029A1F59, 0x029364D9, 0x028CCCCD, 0x0286562E, 0x02800000, 0x0279C952,
        0x0273B13B, 0x026DB6DB, 0x0267D95C, 0x026217ED, 0x025C71C7, 0x0256E62A, 0x0251745D, 0x024C1BAD,
        0x0246DB6E, 0x0241B2F9, 0x023CA1AF, 0x0237A6F5, 0x0232C235, 0x022DF2DF, 0x02293868, 0x02249249,
        0x02200000, 0x021B810F, 0x021714FC, 0x0212BB51, 0x020E739D, 0x020A3D71, 0x02061862, 0x02020408,
        0x01FE0000, 0x01FA0BE8, 0x01F62762, 0x01F25214, 0x01EE8BA3, 0x01EAD3BB, 0x01E72A08, 0x01E38E39,
        0x01E00000, 0x01DC7F11, 0x01D90B21, 0x01D5A3EA, 0x01D24925, 0x01CEFA8E, 0x01CBB7E3, 0x01C880E5,
        0x01C55555, 0x01C234F7, 0x01BF1F90, 0x01BC14E6, 0x01B914C2, 0x01B61EED, 0x01B33333, 0x01B05161,
        0x01AD7943, 0x01AAAAAB, 0x01A7E567, 0x01A5294A, 0x01A27627, 0x019FCBD2, 0x019D2A20, 0x019A90E8,
        0x01980000, 0x01957741, 0x0192F685, 0x01907DA5, 0x018E0C7D, 0x018BA2E9, 0x018940C5, 0x0186E5F1,
        0x01849249, 0x018245AE, 0x01800000, 0x017DC11F, 0x017B88EE, 0x0179574E, 0x01772C23, 0x01750750,
        0x0172E8BA, 0x0170D045, 0x016EBDD8, 0x016CB157, 0x016AAAAB, 0x0168A9B9, 0x0166AE6B, 0x0164B8A8,
        0x0162C859, 0x0160DD68, 0x015EF7BE, 0x015D1746, 0x015B3BEA, 0x01596596, 0x01579436, 0x0155C7B5,
        0x01540000, 0x01523D04, 0x01507EAE, 0x014EC4EC, 0x014D0FAC, 0x014B5EDD, 0x0149B26D, 0x01480A4B,
        0x01466666, 0x0144C6B0, 0x01432B17, 0x0141938C, 0x01400000, 0x013E7064, 0x013CE4A9, 0x013B5CC1,
        0x0139D89E, 0x01385831, 0x0136DB6E, 0x01356246, 0x0133ECAE, 0x01327A97, 0x01310BF6, 0x012FA0BF,
        0x012E38E4, 0x012CD45A, 0x012B7315, 0x012A150B, 0x0128BA2F, 0x01276276, 0x01260DD6, 0x0124BC45,
        0x01236DB7, 0x01222222, 0x0120D97D, 0x011F93BC, 0x011E50D8, 0x011D10C5, 0x011BD37A, 0x011A98EF,
        0x0119611A, 0x01182BF3, 0x0116F970, 0x0115C988, 0x01149C34, 0x0113716B, 0x01124925, 0x01112359,
        0x01100000, 0x010EDF12, 0x010DC087, 0x010CA458, 0x010B8A7E, 0x010A72F0, 0x01095DA9, 0x01084AA0,
        0x010739CE, 0x01062B2E, 0x01051EB8, 0x01041466, 0x01030C31, 0x01020612, 0x01010204, 0x01000000
    ];

    [Fact]
    public void UnpremultiplyScale_IsSkiasTable()
    {
        for (var a = 0; a < 256; a++) Assert.Equal(s_skiaTable[a], LinuxTrayRules.UnpremultiplyScale(a));
    }

    [Fact]
    public void IconPixmap_IsArgbInNetworkOrder_UnpremultipliedAsGetColor()
    {
        // Premultiplied BGRA: (10, 64, 100) at alpha 128, then opaque blue, then fully transparent.
        var bitmap = new ImageBitmap(3, 1, [10, 64, 100, 128, 255, 0, 0, 255, 0, 0, 0, 0]);
        Assert.Equal(new byte[] { 128, 199, 128, 20, 255, 0, 0, 255, 0, 0, 0, 0 }, LinuxTrayRules.IconPixmap(bitmap));
    }

    [Fact]
    public void ApplyScale_WrapsAsSkiasUnsignedArithmetic()
    {
        // A component above its alpha is invalid premultiplied data; Skia's 32-bit product wraps.
        Assert.Equal(unchecked((byte)((0xFF000000u * 2 + (1u << 23)) >> 24)), LinuxTrayRules.ApplyScale(LinuxTrayRules.UnpremultiplyScale(1), 2));
        Assert.Equal(255, LinuxTrayRules.ApplyScale(LinuxTrayRules.UnpremultiplyScale(255), 255));
    }

    [Fact]
    public void Rgba_IsGdkPixbufOrder()
        => Assert.Equal(new byte[] { 199, 128, 20, 128 }, LinuxTrayRules.Rgba(new ImageBitmap(1, 1, [10, 64, 100, 128])));

    [Fact]
    public void BestRepresentation_IsTheLargestScale_OrNullWhenEmpty()
    {
        var image = NativeImage.CreateFromBitmap(new byte[16 * 16 * 4], new NativeImageBitmapOptions { Width = 16, Height = 16 });
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 32, Height = 32, Buffer = new byte[32 * 32 * 4] });
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 1.5, Width = 24, Height = 24, Buffer = new byte[24 * 24 * 4] });
        Assert.Equal(32, LinuxTrayRules.BestRepresentation(image)!.Value.Width);
        Assert.Null(LinuxTrayRules.BestRepresentation(NativeImage.CreateEmpty()));
    }

    [Fact]
    public void IconPixmapValue_IsOneImage_EmptyForANullImage()
    {
        var value = LinuxTrayRules.IconPixmapValue(new ImageBitmap(1, 1, [0, 0, 255, 255]));
        Assert.Equal(new LinuxDBusVariant("a(iiay)", new List<object?> { new object?[] { 1, 1, new byte[] { 255, 255, 0, 0 } } }), value);
        Assert.Equal(new LinuxDBusVariant("a(iiay)", new List<object?> { new object?[] { 0, 0, Array.Empty<byte>() } }), LinuxTrayRules.IconPixmapValue(null));
    }

    [Fact]
    public void ToolTipValue_IsTheTitleOnly()
        => Assert.Equal(new LinuxDBusVariant("(sa(iiay)ss)", new object?[] { "", new List<object?>(), "tip", "" }), LinuxTrayRules.ToolTipValue("tip"));

    [Theory]
    [InlineData("&File", "_File")]
    [InlineData("Save && Quit", "Save & Quit")]
    [InlineData("snake_case", "snake__case")]
    [InlineData("A&&&B", "A&_B")]
    [InlineData("End&", "End_")]
    [InlineData("", "")]
    public void MenuLabel_ConvertsWindowsMnemonicsToGtks(string label, string expected) => Assert.Equal(expected, LinuxTrayRules.MenuLabel(label));

    [Theory]
    [InlineData("CmdOrCtrl+S", "Control s")]
    [InlineData("Alt+Plus", "Alt Shift =")]
    [InlineData("Ctrl+!", "Control Shift 1")]
    [InlineData("Ctrl+?", "Control Shift /")]
    [InlineData("Ctrl+num1", null)]
    [InlineData("Ctrl+capslock", null)]
    [InlineData("Super+Shift+Alt+Ctrl+A", "Control Alt Shift Super a")]
    [InlineData("Ctrl+Space", "Control  ")]
    [InlineData("Ctrl+1", "Control 1")]
    [InlineData("Ctrl+=", "Control =")]
    [InlineData("Ctrl+-", "Control -")]
    [InlineData("Ctrl+,", "Control ,")]
    [InlineData("Ctrl+.", "Control .")]
    [InlineData("Ctrl+/", "Control /")]
    [InlineData("Ctrl+;", "Control ;")]
    [InlineData("Ctrl+'", "Control '")]
    [InlineData("Ctrl+[", "Control [")]
    [InlineData("Ctrl+]", "Control ]")]
    [InlineData("Ctrl+\\", "Control \\")]
    [InlineData("Ctrl+`", "Control `")]
    [InlineData("F5", null)]
    [InlineData("Ctrl+Enter", null)]
    [InlineData("Ctrl+Tab", null)]
    [InlineData("Ctrl+Up", null)]
    [InlineData("Ctrl+Delete", null)]
    public void Shortcut_IsModifiersThenTheUnshiftedUsCharacter(string accelerator, string? expected)
        => Assert.Equal(expected, LinuxTrayRules.Shortcut(accelerator) is { } parts ? string.Join(" ", parts) : null);

    [Theory]
    [InlineData("XDG_CURRENT_DESKTOP=ubuntu:GNOME", "GNOME")]
    [InlineData("XDG_CURRENT_DESKTOP=Unity", "UNITY")]
    [InlineData("XDG_CURRENT_DESKTOP=Unity;DESKTOP_SESSION=gnome-fallback-compiz", "GNOME")]
    [InlineData("XDG_CURRENT_DESKTOP=KDE", "KDE4")]
    [InlineData("XDG_CURRENT_DESKTOP=KDE;KDE_SESSION_VERSION=5", "KDE5")]
    [InlineData("XDG_CURRENT_DESKTOP=KDE;KDE_SESSION_VERSION=6", "KDE6")]
    [InlineData("XDG_CURRENT_DESKTOP=Pantheon", "PANTHEON")]
    [InlineData("XDG_CURRENT_DESKTOP=X-Cinnamon", "CINNAMON")]
    [InlineData("XDG_CURRENT_DESKTOP=Deepin", "DEEPIN")]
    [InlineData("XDG_CURRENT_DESKTOP=XFCE", "XFCE")]
    [InlineData("XDG_CURRENT_DESKTOP=UKUI", "UKUI")]
    [InlineData("XDG_CURRENT_DESKTOP=LXQt", "LXQT")]
    [InlineData("XDG_CURRENT_DESKTOP=COSMIC", "COSMIC")]
    [InlineData("XDG_CURRENT_DESKTOP=sway;DESKTOP_SESSION=gnome", "GNOME")]
    [InlineData("DESKTOP_SESSION=mate", "GNOME")]
    [InlineData("DESKTOP_SESSION=deepin", "DEEPIN")]
    [InlineData("DESKTOP_SESSION=kde-plasma", "KDE4")]
    [InlineData("DESKTOP_SESSION=kde", "KDE3")]
    [InlineData("DESKTOP_SESSION=kde;KDE_SESSION_VERSION=5", "KDE4")]
    [InlineData("DESKTOP_SESSION=xubuntu", "XFCE")]
    [InlineData("DESKTOP_SESSION=my-xfce", "XFCE")]
    [InlineData("DESKTOP_SESSION=ukui", "UKUI")]
    [InlineData("GNOME_DESKTOP_SESSION_ID=x", "GNOME")]
    [InlineData("KDE_FULL_SESSION=true", "KDE3")]
    [InlineData("KDE_FULL_SESSION=true;KDE_SESSION_VERSION=4", "KDE4")]
    [InlineData("", "OTHER")]
    public void DesktopEnvironment_IsChromiums(string variables, string expected)
        => Assert.Equal(expected, LinuxTrayRules.DesktopEnvironment(Env(variables)));

    [Theory]
    [InlineData("XDG_CURRENT_DESKTOP=GNOME", true)]
    [InlineData("XDG_CURRENT_DESKTOP=Pantheon", true)]
    [InlineData("XDG_CURRENT_DESKTOP=KDE", false)]
    [InlineData("", false)]
    public void ShouldWriteIconToFile_OnlyForGnomeAndPantheon(string variables, bool expected)
        => Assert.Equal(expected, LinuxTrayRules.ShouldWriteIconToFile(Env(variables)));

    [Theory]
    [InlineData("SNAP=/snap/x/1", true)]
    [InlineData("FLATPAK_SANDBOX_DIR=/run/flatpak", true)]
    [InlineData("", false)]
    public void IsSandboxedWithoutOwnName_ForSnapAndFlatpak(string variables, bool expected)
        => Assert.Equal(expected, LinuxTrayRules.IsSandboxedWithoutOwnName(Env(variables)));

    [Theory]
    [InlineData("ar-EG", "rtl ")]
    [InlineData("he-IL", "rtl ")]
    [InlineData("en-US@posix", "ltr")]
    [InlineData("de-DE", "ltr")]
    public void TextDirection_KeepsChromiumsTrailingSpace(string locale, string expected) => Assert.Equal(expected, LinuxTrayRules.TextDirection(locale));

    [Theory]
    [InlineData("xx-YY,ar-EG,en-US", "ar")]
    [InlineData("de-AT,de", "de")]
    [InlineData("xx", "en-US")]
    [InlineData("", "en-US")]
    public void ApplicationLanguage_IsTheFirstCandidateWithLocaleData(string candidates, string expected)
        => Assert.Equal(expected, LinuxTrayRules.ApplicationLanguage(candidates.Split(',', StringSplitOptions.RemoveEmptyEntries), l => l is "ar" or "de" or "en"));

    private static Func<string, string?> Env(string variables)
    {
        var map = variables.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        return name => map.GetValueOrDefault(name);
    }
}
