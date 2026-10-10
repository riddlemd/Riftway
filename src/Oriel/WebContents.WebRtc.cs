using Oriel.Cef;

namespace Oriel;

/// <summary>The UDP ports WebRTC may use; <c>0, 0</c> for any. Electron's <c>udpPortRange</c>.</summary>
/// <param name="Min">The lowest port.</param>
/// <param name="Max">The highest port.</param>
public readonly record struct WebRtcUdpPortRange(int Min, int Max);

/// <summary>WebRTC's network settings.</summary>
public sealed partial class WebContents
{
    private static readonly string[] s_ipHandlingPolicies =
        ["default", "default_public_and_private_interfaces", "default_public_interface_only", "disable_non_proxied_udp"];

    private string _ipHandlingPolicy = "default";
    private WebRtcUdpPortRange _udpPortRange;

    /// <summary>Which network interfaces WebRTC may gather candidates on. Electron's
    /// <c>webContents.getWebRTCIPHandlingPolicy()</c>.</summary>
    public string GetWebRtcIpHandlingPolicy() => CefThread.InvokeOnUi(() => _ipHandlingPolicy);

    /// <summary>Sets which network interfaces WebRTC may use: <c>default</c>, <c>default_public_and_private_interfaces</c>,
    /// <c>default_public_interface_only</c> or <c>disable_non_proxied_udp</c>; anything else is <c>default</c>, as
    /// Electron's. Electron's <c>webContents.setWebRTCIPHandlingPolicy()</c>; it applies to every page of the
    /// <see cref="Session"/> (drift C52).</summary>
    public void SetWebRtcIpHandlingPolicy(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var value = s_ipHandlingPolicies.Contains(policy) ? policy : "default";
        CefThread.InvokeOnUi(() =>
        {
            if (_ipHandlingPolicy == value) return true;
            _ipHandlingPolicy = value;
            Session.SetRendererPreference("webrtc.ip_handling_policy", value);
            return true;
        });
    }

    /// <summary>The UDP ports WebRTC may use, <c>0, 0</c> for any. Electron's <c>webContents.getWebRTCUDPPortRange()</c>.</summary>
    public WebRtcUdpPortRange GetWebRtcUdpPortRange() => CefThread.InvokeOnUi(() => _udpPortRange);

    /// <summary>Limits the UDP ports WebRTC may use; <c>0, 0</c> lifts the limit. Electron's
    /// <c>webContents.setWebRTCUDPPortRange()</c>; it applies to every page of the <see cref="Session"/> (drift C52).</summary>
    /// <exception cref="ArgumentException">A negative port, a range outside 1 to 65535 (other than <c>0, 0</c>), or a
    /// maximum below the minimum; with Electron's messages.</exception>
    public void SetWebRtcUdpPortRange(WebRtcUdpPortRange range)
    {
        if (range.Min < 0 || range.Max < 0) throw new ArgumentException("'min' and 'max' are both required");
        if ((range.Min == 0 && range.Max != 0) || range.Max > ushort.MaxValue)
            throw new ArgumentException("'min' and 'max' must be in the (0, 65535] range or [0, 0]");
        if (range.Min > range.Max) throw new ArgumentException("'max' must be greater than or equal to 'min'");
        CefThread.InvokeOnUi(() =>
        {
            if (_udpPortRange == range) return true;
            _udpPortRange = range;
            // Chromium's renderer preferences read "min-max"; 0-0 is any port.
            Session.SetRendererPreference("webrtc.udp_port_range", $"{range.Min}-{range.Max}");
            return true;
        });
    }
}
