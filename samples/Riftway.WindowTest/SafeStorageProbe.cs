using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

// DPAPI's ppszDataDescr out-parameter, read independently of Riftway's own DPAPI wrapper.
static unsafe class WindowsDpapiProbe
{
    private struct Blob
    {
        public uint Size;
        public byte* Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern int CryptUnprotectData(Blob* input, char** description, nint entropy, nint reserved, nint prompt, uint flags, Blob* output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(void* memory);

    /// <summary>The description of the os_crypt key in a Local State file, or a reason it could not be read.</summary>
    internal static string Description(string localStatePath)
    {
        try
        {
            var key = JsonNode.Parse(File.ReadAllText(localStatePath))?["os_crypt"]?["encrypted_key"]?.GetValue<string>();
            return DescriptionOf(key);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { return e.GetType().Name; }
    }

    private static string DescriptionOf(string? base64)
    {
        if (base64 is null) return "no key";
        byte[] decoded;
        try { decoded = Convert.FromBase64String(base64); }
        catch (FormatException) { return "not base64"; }
        if (decoded.Length < 5 || !decoded.AsSpan(0, 5).SequenceEqual("DPAPI"u8)) return "no DPAPI header";
        fixed (byte* data = decoded)
        {
            var input = new Blob { Size = (uint)(decoded.Length - 5), Data = data + 5 };
            Blob output = default;
            char* description = null;
            if (CryptUnprotectData(&input, &description, 0, 0, 0, 0, &output) == 0) return $"CryptUnprotectData failed ({Marshal.GetLastPInvokeError()})";
            LocalFree(output.Data);
            var text = description is null ? "null" : new string(description);
            if (description is not null) LocalFree(description);
            return text;
        }
    }
}
