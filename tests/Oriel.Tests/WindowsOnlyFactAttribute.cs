namespace Oriel.Tests;

/// <summary>A fact that only runs on Windows and shows as skipped elsewhere, rather than passing vacuously.</summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}
