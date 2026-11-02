using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Riftway;

/// <summary>One message between the main process and a utility process: a JSON object, sent as a 4-byte little-endian
/// length and that many bytes of UTF-8.</summary>
// Type: `hello` (child to parent, first, with Token), `msg` (the parent channel), `port-msg` and `port-close` (a port moved across, by Id), `fatal`.
// Data: The message as JSON text (drift I1), for `msg` and `port-msg`.
// Ports: Ids of ports moved across with the message; each names a new link end on both sides.
internal sealed record WireFrame(string Type, string? Id = null, string? Data = null, IReadOnlyList<string>? Ports = null,
    string? Token = null, string? Location = null, string? Report = null)
{
    /// <summary>Larger frames are refused: a corrupt length must not allocate gigabytes.</summary>
    internal const int MaxLength = 64 * 1024 * 1024;

    internal byte[] Encode()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("t", Type);
            if (Id is not null) w.WriteString("id", Id);
            if (Data is not null)
            {
                w.WritePropertyName("data");
                w.WriteRawValue(Data);
            }
            if (Ports is { Count: > 0 })
            {
                w.WriteStartArray("ports");
                foreach (var port in Ports) w.WriteStringValue(port);
                w.WriteEndArray();
            }
            if (Token is not null) w.WriteString("token", Token);
            if (Location is not null) w.WriteString("location", Location);
            if (Report is not null) w.WriteString("report", Report);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <exception cref="InvalidDataException">Not a frame object.</exception>
    internal static WireFrame Decode(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("A wire frame must be an object with a string \"t\".");
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new WireFrame(
                type.GetString()!,
                Text("id"),
                root.TryGetProperty("data", out var data) ? data.GetRawText() : null,
                root.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array
                    ? [.. ports.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString()! : throw new InvalidDataException("A port id must be a string."))]
                    : [],
                Text("token"),
                Text("location"),
                Text("report"));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("A wire frame is not valid JSON.", e);
        }
    }

    internal static void Write(Stream stream, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxLength) throw new InvalidDataException($"A wire frame of {payload.Length} bytes exceeds {MaxLength}.");
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
    }

    /// <summary>Reads one frame's payload; null at a clean end of stream between frames.</summary>
    /// <exception cref="InvalidDataException">The stream ended inside a frame, or the length is out of range.</exception>
    internal static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var got = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < header.Length) throw new InvalidDataException("The stream ended inside a frame header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxLength) throw new InvalidDataException($"A wire frame length of {length} is out of range.");
        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false) < length)
            throw new InvalidDataException("The stream ended inside a frame.");
        return payload;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
