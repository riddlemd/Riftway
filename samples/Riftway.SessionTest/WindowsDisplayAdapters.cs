using System.Runtime.Versioning;

/// <summary>The machine's display adapters as Windows lists them, independent of Chromium's GPU info.</summary>
[SupportedOSPlatform("windows")]
static class WindowsDisplayAdapters
{
    // The display adapter device class; each installed adapter's driver key holds its PnP id.
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>The PCI vendor ids of the installed display adapters (<c>pci\ven_xxxx</c>); indirect and remote
    /// display adapters have none.</summary>
    internal static HashSet<int> PciVendorIds()
    {
        var vendors = new HashSet<int>();
        using var adapters = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClass);
        foreach (var name in adapters?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var adapter = adapters!.OpenSubKey(name);
                if (adapter?.GetValue("MatchingDeviceId") is not string id || !id.StartsWith(@"pci\ven_", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(id.AsSpan(8, 4), System.Globalization.NumberStyles.HexNumber, null, out var vendor)) vendors.Add(vendor);
            }
            catch (System.Security.SecurityException) { }
        }
        return vendors;
    }
}
