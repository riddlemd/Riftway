using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class AppDetailsTests
{
    [Fact]
    public void Plan_WritesEveryOption_IconAsPathCommaIndex()
    {
        var plan = AppDetails.Plan(new AppDetailsOptions
        {
            AppId = "Riddlemd.Oriel.Test",
            AppIconPath = @"C:\App\app.exe",
            AppIconIndex = 3,
            RelaunchCommand = @"C:\App\app.exe --x",
            RelaunchDisplayName = "Oriel Test",
        });
        Assert.Equal(
            [
                (AppUserModelProperty.Id, "Riddlemd.Oriel.Test"),
                (AppUserModelProperty.RelaunchIconResource, @"C:\App\app.exe,3"),
                (AppUserModelProperty.RelaunchCommand, @"C:\App\app.exe --x"),
                (AppUserModelProperty.RelaunchDisplayNameResource, "Oriel Test"),
            ],
            plan);
    }

    [Fact]
    public void Plan_SkipsEmptyOptions()
    {
        Assert.Empty(AppDetails.Plan(new AppDetailsOptions { AppId = "", AppIconPath = "", AppIconIndex = 2, RelaunchCommand = "", RelaunchDisplayName = "" }));
        Assert.Equal([(AppUserModelProperty.RelaunchDisplayNameResource, "Name")], AppDetails.Plan(new AppDetailsOptions { RelaunchDisplayName = "Name" }));
        Assert.Equal([(AppUserModelProperty.RelaunchCommand, "cmd")], AppDetails.Plan(new AppDetailsOptions { RelaunchCommand = "cmd" }));
        Assert.Equal([(AppUserModelProperty.RelaunchIconResource, "a.ico,0")], AppDetails.Plan(new AppDetailsOptions { AppIconPath = "a.ico" }));
        Assert.Equal([(AppUserModelProperty.Id, "Id")], AppDetails.Plan(new AppDetailsOptions { AppId = "Id" }));
    }

    [Fact]
    public void Properties_ArePKeyAppUserModelIds()
    {
        Assert.Equal(2, (int)AppUserModelProperty.RelaunchCommand);
        Assert.Equal(3, (int)AppUserModelProperty.RelaunchIconResource);
        Assert.Equal(4, (int)AppUserModelProperty.RelaunchDisplayNameResource);
        Assert.Equal(5, (int)AppUserModelProperty.Id);
    }

    [Theory]
    [InlineData(1.0, 10, 20, 100, 50, 10, 20, 110, 70)]
    [InlineData(1.5, 10, 20, 100, 50, 15, 30, 165, 105)]
    [InlineData(1.25, 1, 1, 1, 1, 1, 1, 3, 3)]
    [InlineData(1.25, 3, 3, 3, 3, 3, 3, 8, 8)]
    public void ClipToPixels_EnclosesTheScaledRegion(double scale, int x, int y, int w, int h, int left, int top, int right, int bottom)
        => Assert.Equal((left, top, right, bottom), AppDetails.ClipToPixels(new Rectangle(x, y, w, h), scale));

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public void ClipToPixels_EmptyRegion_ClearsTheClip(int w, int h)
        => Assert.Null(AppDetails.ClipToPixels(new Rectangle(5, 5, w, h), 1.5));

    [Theory]
    [InlineData(BackgroundMaterial.Auto, WindowsBackdrop.Auto)]
    [InlineData(BackgroundMaterial.None, WindowsBackdrop.None)]
    [InlineData(BackgroundMaterial.Mica, WindowsBackdrop.MainWindow)]
    [InlineData(BackgroundMaterial.Acrylic, WindowsBackdrop.TransientWindow)]
    [InlineData(BackgroundMaterial.Tabbed, WindowsBackdrop.TabbedWindow)]
    public void ToBackdrop_MapsElectronsMaterials(BackgroundMaterial material, WindowsBackdrop backdrop)
        => Assert.Equal(backdrop, BackgroundMaterials.ToBackdrop(material));

    [Fact]
    public void Backdrops_AreWin32DwmsbtValues()
    {
        Assert.Equal(0, (int)WindowsBackdrop.Auto);
        Assert.Equal(1, (int)WindowsBackdrop.None);
        Assert.Equal(2, (int)WindowsBackdrop.MainWindow);
        Assert.Equal(3, (int)WindowsBackdrop.TransientWindow);
        Assert.Equal(4, (int)WindowsBackdrop.TabbedWindow);
    }
}
