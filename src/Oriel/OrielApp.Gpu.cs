using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Handlers;

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
    private const string DisableDomainBlockingFor3DAPIsSwitch = "disable-domain-blocking-for-3d-apis";

    /// <summary>Chromium's GPU information changed, as when the GPU process starts or reports more. Electron's
    /// <c>gpu-info-update</c>.</summary>
    /// <remarks>Raised only with Oriel's CEF patch 0013; stock CEF has no such notification (drift A27).</remarks>
    public event EventHandler? GpuInfoUpdate;

    /// <summary>The status of each graphics feature, as <c>chrome://gpu</c> lists it (for example <c>webgl</c>:
    /// <c>enabled</c>). Electron's <c>app.getGPUFeatureStatus()</c>. With Oriel's CEF patch 0013 this is
    /// <see cref="GetGpuFeatureStatus"/>; with stock CEF it is read from a hidden <c>chrome://gpu</c> page, which first
    /// waits for the GPU process's report (drift A27).</summary>
    /// <exception cref="InvalidOperationException">The app is not ready, or <c>chrome://gpu</c> did not report.</exception>
    public async Task<IReadOnlyDictionary<string, string>> GetGpuFeatureStatusAsync()
    {
        if (!IsReady) throw new InvalidOperationException("The app is not ready.");
        if (CefRuntime.IsPatched) return GetGpuFeatureStatus();
        var info = await ChromeGpuAsync().ConfigureAwait(false);
        return FeatureStatusFrom(info.GetProperty("featureStatus").GetProperty("featureStatus"));
    }

    /// <summary>The status of each graphics feature, as <see cref="GetGpuFeatureStatusAsync"/>, read from Chromium
    /// directly. Electron's synchronous <c>app.getGPUFeatureStatus()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="PlatformNotSupportedException">Stock CEF: use <see cref="GetGpuFeatureStatusAsync"/>; needs
    /// Oriel's CEF patch 0013.</exception>
    public IReadOnlyDictionary<string, string> GetGpuFeatureStatus()
    {
        if (!IsReady) throw new InvalidOperationException("The app is not ready.");
        if (!CefRuntime.IsPatched) throw new PlatformNotSupportedException("A synchronous GPU feature status needs Oriel's CEF patch 0013.");
        var json = CefThread.InvokeOnUi(() => CefPatchedApi.GetGpuFeatureStatusJson() ?? "{}");
        using var document = JsonDocument.Parse(json);
        return FeatureStatusFrom(document.RootElement);
    }

    private static Dictionary<string, string> FeatureStatusFrom(JsonElement features)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var feature in features.EnumerateObject())
            status[feature.Name] = feature.Value.GetString() ?? "";
        return status;
    }

    /// <summary>The GPU's details in Electron's shape: <c>auxAttributes</c>, <c>gpuDevice</c> (the primary GPU first),
    /// <c>machineModelName</c> and <c>machineModelVersion</c>. Electron's <c>app.getGPUInfo()</c>. With Oriel's CEF
    /// patch 0013 Chromium's <c>GPUInfo</c> is enumerated as Electron does, and <c>complete</c> asks the GPU process for
    /// its DirectX and video details on Windows and macOS. With stock CEF the result comes from the rows
    /// <c>chrome://gpu</c> shows, which leave out some fields: <c>basic</c> answers once the GPU process has reported,
    /// <c>complete</c> once the rows have also stopped changing for a second (drift A27).</summary>
    /// <exception cref="InvalidOperationException">The app is not ready, GPU access is not allowed, or
    /// <c>chrome://gpu</c> did not report.</exception>
    public async Task<JsonObject> GetGpuInfoAsync(GpuInfoType infoType)
    {
        if (!IsReady) throw new InvalidOperationException("The app is not ready.");
        if (!Enum.IsDefined(infoType)) throw new ArgumentException("Invalid info type. Use 'basic' or 'complete'", nameof(infoType));
        if (CefRuntime.IsPatched)
        {
            var json = await StartGpuInfo(infoType == GpuInfoType.Complete).ConfigureAwait(false);
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        var info = await ChromeGpuAsync(infoType == GpuInfoType.Complete).ConfigureAwait(false);
        return GpuInfoFromChromeGpu(info.GetProperty("basicInfo"));
    }

    /// <summary>Stops Chromium from blocking WebGL and other 3D APIs for a site it blames for a GPU crash, by starting
    /// Chromium with <c>--disable-domain-blocking-for-3d-apis</c>. Electron's
    /// <c>app.disableDomainBlockingFor3DAPIs()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is ready.</exception>
    public void DisableDomainBlockingFor3DAPIs() => DisableDomainBlockingFor3DAPIs(IsReady, CommandLine);

    internal static void DisableDomainBlockingFor3DAPIs(bool ready, CommandLine commandLine)
    {
        if (ready) throw new InvalidOperationException("app.disableDomainBlockingFor3DAPIs() can only be called before app is ready");
        if (!commandLine.HasSwitch(DisableDomainBlockingFor3DAPIsSwitch)) commandLine.AppendSwitch(DisableDomainBlockingFor3DAPIsSwitch);
    }

    private void OnGpuInfoUpdate() => GpuInfoUpdate?.Invoke(this, EventArgs.Empty);

    private static unsafe Task<string> StartGpuInfo(bool complete)
    {
        var callback = new GpuInfoCallback();
        var native = (IntPtr)callback.Native;
        CefThread.RunOnUi(() => CefPatchedApi.GetGpuInfo(complete, (cef_gpu_info_callback_t*)native));
        return callback.Task;
    }

    // The page's first report predates the GPU process's (initialization time 0), and Windows' DirectX and Vulkan rows
    // arrive in a later one with no marker, so complete info waits for the rows to settle.
    private const string ChromeGpuScript = """
        new Promise((resolve, reject) => {
          const deadline = Date.now() + 10000;
          let last = '', changed = Date.now();
          (function poll() {
            const info = window.browserBridge && browserBridge.gpuInfo;
            const ready = info && info.featureStatus && info.basicInfo;
            const rows = ready ? JSON.stringify(info.basicInfo) : '';
            if (rows !== last) { last = rows; changed = Date.now(); }
            const reported = ready && info.basicInfo.some(r => r.description === 'Initialization time' && Number(r.value) > 0);
            if ((reported && Date.now() - changed >= SETTLE) || (ready && Date.now() > deadline))
              resolve({ featureStatus: info.featureStatus, basicInfo: info.basicInfo });
            else if (Date.now() > deadline) reject(new Error('chrome://gpu did not report the GPU information.'));
            else setTimeout(poll, 50);
          })();
        })
        """;

    // A page session cannot reach SystemInfo.getInfo: it needs the browser target, which only a remote-debugging
    // client may attach (target_handler.cc, AccessMode::kBrowser). chrome://gpu's WebUI is handed the same data.
    private static async Task<JsonElement> ChromeGpuAsync(bool complete = false)
    {
        var page = await SessionHelperPage.CreateAsync(Session.DefaultSession, "chrome://gpu").ConfigureAwait(false);
        try
        {
            var result = await page.SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", ChromeGpuScript.Replace("SETTLE", complete ? "1000" : "0", StringComparison.Ordinal));
                w.WriteBoolean("awaitPromise", true);
                w.WriteBoolean("returnByValue", true);
            }).ConfigureAwait(false);
            if (result.TryGetProperty("exceptionDetails", out var details))
                throw new InvalidOperationException(details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
                    ? description.GetString() : "chrome://gpu did not report the GPU information.");
            return result.GetProperty("result").GetProperty("value").Clone();
        }
        finally { page.CloseSoon(); }
    }

    // chrome://gpu's row titles (GpuInfoAsBasicInfo, gpu_internals_ui.cc) to GPUInfo::EnumerateFields' names, which
    // Electron's are. Rows with no counterpart there are left out.
    private static readonly Dictionary<string, string> s_auxAttributeRows = new(StringComparer.Ordinal)
    {
        ["Initialization time"] = "initializationTime",
        ["In-process GPU"] = "inProcessGpu",
        ["Skia Backend"] = "skiaBackendType",
        ["Passthrough Command Decoder"] = "passthroughCmdDecoder",
        ["Sandboxed"] = "sandboxed",
        ["Optimus"] = "optimus",
        ["AMD switchable"] = "amdSwitchable",
        ["Direct composition"] = "directComposition",
        ["Supports overlays"] = "supportsOverlays",
        ["YUY2 overlay support"] = "yuy2OverlaySupport",
        ["NV12 overlay support"] = "nv12OverlaySupport",
        ["BGRA8 overlay support"] = "bgra8OverlaySupport",
        ["RGB10A2 overlay support"] = "rgb10a2OverlaySupport",
        ["P010 overlay support"] = "p010OverlaySupport",
        ["DirectML feature level"] = "directMLFeatureLevel",
        ["Driver D3D12 feature level"] = "dx12FeatureLevel",
        ["Driver Vulkan API version"] = "vulkanVersion",
        ["Pixel shader version"] = "pixelShaderVersion",
        ["Vertex shader version"] = "vertexShaderVersion",
        ["Max. MSAA samples"] = "maxMsaaSamples",
        ["GL implementation parts"] = "glImplementationParts",
        ["Display type"] = "displayType",
        ["GL_VENDOR"] = "glVendor",
        ["GL_RENDERER"] = "glRenderer",
        ["GL_VERSION"] = "glVersion",
        ["GL_EXTENSIONS"] = "glExtensions",
        ["Window system binding vendor"] = "glWsVendor",
        ["Window system binding version"] = "glWsVersion",
        ["Window system binding extensions"] = "glWsExtensions",
        ["Direct rendering version"] = "directRenderingVersion",
        ["Reset notification strategy"] = "glResetNotificationStrategy",
    };

    // chrome://gpu's basicInfo rows ({description, value}) to Electron's GPUInfoEnumerator shape, empty strings dropped.
    internal static JsonObject GpuInfoFromChromeGpu(JsonElement basicInfo)
    {
        var aux = new JsonObject();
        var devices = new JsonArray();
        string? modelName = null, modelVersion = null;
        foreach (var row in basicInfo.EnumerateArray())
        {
            if (row.GetProperty("description").GetString() is not { } title || !row.TryGetProperty("value", out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0) continue;
            if (title.Length > 3 && title.StartsWith("GPU", StringComparison.Ordinal) && title.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0)
            {
                if (value.ValueKind == JsonValueKind.String) devices.Add((JsonNode)GpuDeviceFromRow(value.GetString()!));
            }
            else if (title == "Machine model name") modelName = value.GetString();
            else if (title == "Machine model version") modelVersion = value.GetString();
            else if (s_auxAttributeRows.TryGetValue(title, out var key))
                aux[key] = key switch
                {
                    // Shown in whole milliseconds, which Electron gives as a number.
                    "initializationTime" when double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var ms) => ms,
                    // Shown as the GL enum in hex.
                    "glResetNotificationStrategy" when HexInt(value.GetString()) is { } strategy => strategy,
                    _ => JsonNode.Parse(value.GetRawText()),
                };
        }
        var result = new JsonObject { ["auxAttributes"] = aux, ["gpuDevice"] = devices };
        if (modelName is not null) result["machineModelName"] = modelName;
        if (modelVersion is not null) result["machineModelVersion"] = modelVersion;
        return result;
    }

    // GPUDeviceToString: "VENDOR= 0x10de [name], DEVICE=0x2203 [name], SUBSYS=0x..., REV=161, LUID={0,1},
    // DRIVER_VENDOR=..., DRIVER_VERSION=... *ACTIVE*"; names may hold commas, so fields split outside brackets.
    internal static JsonObject GpuDeviceFromRow(string row)
    {
        const string ActiveMark = " *ACTIVE*";
        var active = row.EndsWith(ActiveMark, StringComparison.Ordinal);
        if (active) row = row[..^ActiveMark.Length];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= row.Length; i++)
        {
            if (i < row.Length)
            {
                if (row[i] is '[' or '{') depth++;
                else if (row[i] is ']' or '}') depth--;
                if (depth > 0 || row[i] != ',') continue;
            }
            var field = row[start..i].Trim();
            start = i + 1;
            var equals = field.IndexOf('=');
            if (equals > 0) fields[field[..equals]] = field[(equals + 1)..].Trim();
        }

        var device = new JsonObject();
        (int? Id, string? Name) IdAndName(string key)
        {
            if (!fields.TryGetValue(key, out var text)) return (null, null);
            var open = text.IndexOf(" [", StringComparison.Ordinal);
            var close = text.LastIndexOf(']');
            return open > 0 && close > open ? (HexInt(text[..open]), text[(open + 2)..close]) : (HexInt(text), null);
        }
        var (vendorId, vendorString) = IdAndName("VENDOR");
        var (deviceId, deviceString) = IdAndName("DEVICE");
        if (vendorId is { } v) device["vendorId"] = v;
        if (deviceId is { } d) device["deviceId"] = d;
        if (fields.TryGetValue("REV", out var rev) && int.TryParse(rev, CultureInfo.InvariantCulture, out var revision)) device["revision"] = revision;
        if (fields.TryGetValue("SUBSYS", out var subSys) && HexInt(subSys) is { } subSysId) device["subSysId"] = subSysId;
        device["active"] = active;
        if (vendorString is { Length: > 0 }) device["vendorString"] = vendorString;
        if (deviceString is { Length: > 0 }) device["deviceString"] = deviceString;
        if (fields.TryGetValue("DRIVER_VENDOR", out var driverVendor) && driverVendor.Length > 0) device["driverVendor"] = driverVendor;
        if (fields.TryGetValue("DRIVER_VERSION", out var driverVersion) && driverVersion.Length > 0) device["driverVersion"] = driverVersion;
        return device;
    }

    // Chromium prints unsigned ids; Electron's enumerator hands them on as int, and a hex parse into int wraps the
    // high bit the same way.
    private static int? HexInt(string? text)
        => text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n)
            ? n : null;
}
