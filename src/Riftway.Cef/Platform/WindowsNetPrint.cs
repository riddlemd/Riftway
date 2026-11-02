using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>A printer as the Windows spooler lists it: <c>PRINTER_INFO_1</c>'s name and comment.</summary>
public sealed record WindowsPrinter(string Name, string? Comment);

/// <summary>The Windows print spooler, read the way Chromium's <c>PrintBackendWin</c> does.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsPrinters
{
    private const uint PrinterEnumLocal = 0x2;
    private const uint PrinterEnumConnections = 0x4;

    /// <summary>Local and connected printers that open, as <c>EnumeratePrinters</c> lists them; null when the spooler fails.</summary>
    public static List<WindowsPrinter>? Enumerate()
    {
        uint needed = 0, returned = 0;
        EnumPrintersW(PrinterEnumLocal | PrinterEnumConnections, null, 4, null, 0, &needed, &returned);
        // Needing no buffer means there are no printers.
        if (needed == 0) return [];

        var buffer = new byte[needed];
        var printers = new List<WindowsPrinter>();
        fixed (byte* data = buffer)
        {
            if (EnumPrintersW(PrinterEnumLocal | PrinterEnumConnections, null, 4, data, needed, &needed, &returned) == 0) return null;
            var infos = (PrinterInfo4*)data;
            for (var i = 0; i < returned; i++)
            {
                if (ReadInfo1(infos[i].pPrinterName) is { } printer) printers.Add(printer);
            }
        }
        return printers;
    }

    /// <summary>Whether the spooler opens a printer by this name (<c>IsValidPrinter</c>).</summary>
    public static bool IsValid(string name)
    {
        nint handle = 0;
        fixed (char* p = name)
        {
            if (OpenPrinterW(p, &handle, null) == 0) return false;
        }
        ClosePrinter(handle);
        return true;
    }

    /// <summary>The default printer's name; empty when there is none, null when the spooler fails.</summary>
    public static string? GetDefault()
    {
        uint size = 0;
        GetDefaultPrinterW(null, &size);
        // No size needed: there is no default printer.
        if (size == 0) return string.Empty;
        var buffer = new char[size];
        fixed (char* p = buffer)
        {
            return GetDefaultPrinterW(p, &size) == 0 ? null : new string(p);
        }
    }

    private static WindowsPrinter? ReadInfo1(char* name)
    {
        nint handle = 0;
        if (OpenPrinterW(name, &handle, null) == 0) return null;
        try
        {
            uint needed = 0;
            GetPrinterW(handle, 1, null, 0, &needed);
            if (needed == 0) return null;
            var buffer = new byte[needed];
            fixed (byte* data = buffer)
            {
                if (GetPrinterW(handle, 1, data, needed, &needed) == 0) return null;
                var info = (PrinterInfo1*)data;
                return new WindowsPrinter(new string(info->pName), info->pComment is null ? null : new string(info->pComment));
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    private struct PrinterInfo4
    {
        public char* pPrinterName;
        public char* pServerName;
        public uint Attributes;
    }

    private struct PrinterInfo1
    {
        public uint Flags;
        public char* pDescription;
        public char* pName;
        public char* pComment;
    }

    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern int EnumPrintersW(uint flags, char* name, uint level, byte* buffer, uint size, uint* needed, uint* returned);

    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern int OpenPrinterW(char* name, nint* handle, void* defaults);

    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern int GetPrinterW(nint handle, uint level, byte* buffer, uint size, uint* needed);

    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern int ClosePrinter(nint handle);

    [DllImport("winspool.drv", ExactSpelling = true)]
    private static extern int GetDefaultPrinterW(char* buffer, uint* size);
}

/// <summary>Network Location Awareness, queried as Chromium's <c>NetworkChangeNotifierWin</c> does.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsNetworkAwareness
{
    private const uint NsNla = 15;
    private const uint LupReturnName = 0x10;
    private const uint LupReturnAll = 0x0FF0;
    private const int WsaEFault = 10014;

    /// <summary>Whether NLA lists any connected network; null when the query fails (Chromium then assumes online).</summary>
    public static bool? HasConnectedNetwork()
    {
        WsaData data;
        if (WSAStartup(0x0202, &data) != 0) return null;
        try
        {
            var query = new WsaQuerySet { dwSize = (uint)sizeof(WsaQuerySet), dwNameSpace = NsNla };
            nint lookup;
            if (WSALookupServiceBeginW(&query, LupReturnAll, &lookup) != 0) return null;
            // Room for the name of the first network, as Chromium allows; WSAEFAULT still means one exists.
            var buffer = stackalloc byte[sizeof(WsaQuerySet) + 256];
            new Span<byte>(buffer, sizeof(WsaQuerySet) + 256).Clear();
            ((WsaQuerySet*)buffer)->dwSize = (uint)sizeof(WsaQuerySet);
            var length = (uint)(sizeof(WsaQuerySet) + 256);
            var found = WSALookupServiceNextW(lookup, LupReturnName, &length, (WsaQuerySet*)buffer) == 0
                || WSAGetLastError() == WsaEFault;
            WSALookupServiceEnd(lookup);
            return found;
        }
        finally
        {
            WSACleanup();
        }
    }

    private struct WsaData
    {
        public ushort wVersion;
        public ushort wHighVersion;
        public ushort iMaxSockets;
        public ushort iMaxUdpDg;
        public nint lpVendorInfo;
        public fixed byte szDescription[257];
        public fixed byte szSystemStatus[129];
    }

    private struct WsaQuerySet
    {
        public uint dwSize;
        public nint lpszServiceInstanceName;
        public nint lpServiceClassId;
        public nint lpVersion;
        public nint lpszComment;
        public uint dwNameSpace;
        public nint lpNSProviderId;
        public nint lpszContext;
        public uint dwNumberOfProtocols;
        public nint lpafpProtocols;
        public nint lpszQueryString;
        public uint dwNumberOfCsAddrs;
        public nint lpcsaBuffer;
        public uint dwOutputFlags;
        public nint lpBlob;
    }

    [DllImport("ws2_32.dll", ExactSpelling = true)]
    private static extern int WSAStartup(ushort version, WsaData* data);

    [DllImport("ws2_32.dll", ExactSpelling = true)]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll", ExactSpelling = true)]
    private static extern int WSAGetLastError();

    [DllImport("ws2_32.dll", ExactSpelling = true)]
    private static extern int WSALookupServiceBeginW(WsaQuerySet* restrictions, uint flags, nint* lookup);

    [DllImport("ws2_32.dll", ExactSpelling = true)]
    private static extern int WSALookupServiceNextW(nint lookup, uint flags, uint* length, WsaQuerySet* results);

    [DllImport("ws2_32.dll", ExactSpelling = true)]
    private static extern int WSALookupServiceEnd(nint lookup);
}
