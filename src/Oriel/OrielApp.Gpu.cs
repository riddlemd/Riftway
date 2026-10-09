using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oriel;

/// <summary>How much GPU information <see cref="OrielApp.GetGpuInfoAsync"/> gathers; Electron's <c>infoType</c>.</summary>
public enum GpuInfoType
{
    /// <summary>Electron's <c>basic</c>.</summary>
    Basic,

    /// <summary>Electron's <c>complete</c>.</summary>
    Complete,
}

public sealed partial class OrielApp
{
    /// <summary>The status of each graphics feature, as <c>chrome://gpu</c> lists it (for example <c>webgl</c>:
    /// <c>enabled</c>). Electron's <c>app.getGPUFeatureStatus()</c>, asynchronous here: it is read from Chromium's
    /// browser DevTools target, which first waits for the GPU process's report (drift A27).</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public async Task<IReadOnlyDictionary<string, string>> GetGpuFeatureStatusAsync()
    {
        if (!IsReady) throw new InvalidOperationException("The app is not ready.");
        var info = await Session.DefaultSession.BrowserTargetAsync("SystemInfo.getInfo").ConfigureAwait(false);
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var feature in info.GetProperty("gpu").GetProperty("featureStatus").EnumerateObject())
            status[feature.Name] = feature.Value.GetString() ?? "";
        return status;
    }

    /// <summary>The GPU's details in Electron's shape: <c>auxAttributes</c>, <c>gpuDevice</c> (the active GPU first),
    /// <c>machineModelName</c> and <c>machineModelVersion</c>. Electron's <c>app.getGPUInfo()</c>; both types answer
    /// from the GPU process's report through DevTools, which leaves out some device fields (drift A27).</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public async Task<JsonObject> GetGpuInfoAsync(GpuInfoType infoType)
    {
        if (!IsReady) throw new InvalidOperationException("The app is not ready.");
        if (!Enum.IsDefined(infoType)) throw new ArgumentException("Invalid info type. Use 'basic' or 'complete'", nameof(infoType));
        var info = await Session.DefaultSession.BrowserTargetAsync("SystemInfo.getInfo").ConfigureAwait(false);
        return GpuInfoFromDevTools(info);
    }

    // SystemInfo.getInfo to Electron's GPUInfoEnumerator: empty strings dropped, initializationTime in milliseconds, and
    // the two counters DevTools adds left out.
    internal static JsonObject GpuInfoFromDevTools(JsonElement info)
    {
        var gpu = info.GetProperty("gpu");
        var aux = new JsonObject();
        foreach (var attribute in gpu.GetProperty("auxAttributes").EnumerateObject())
        {
            if (attribute.Name is "processCrashCount") continue;
            if (attribute.Value.ValueKind == JsonValueKind.String && attribute.Value.GetString()!.Length == 0) continue;
            aux[attribute.Name] = attribute.Name == "initializationTime" && attribute.Value.ValueKind == JsonValueKind.Number
                ? attribute.Value.GetDouble() * 1000
                : JsonNode.Parse(attribute.Value.GetRawText());
        }
        var devices = new JsonArray();
        var first = true;
        foreach (var device in gpu.GetProperty("devices").EnumerateArray())
        {
            var entry = new JsonObject { ["active"] = first };
            first = false;
            foreach (var field in device.EnumerateObject())
            {
                if (field.Value.ValueKind == JsonValueKind.String && field.Value.GetString()!.Length == 0) continue;
                entry[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            }
            devices.Add((JsonNode)entry);
        }
        var result = new JsonObject { ["auxAttributes"] = aux, ["gpuDevice"] = devices };
        foreach (var key in new[] { "modelName", "modelVersion" })
            if (info.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } text)
                result[key == "modelName" ? "machineModelName" : "machineModelVersion"] = text;
        return result;
    }
}
