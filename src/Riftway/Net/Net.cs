using System.Net.NetworkInformation;
using System.Net.Sockets;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>HTTP requests over Chromium's network stack, and the network's state. Electron's <c>net</c> module.</summary>
/// <remarks>Usable once the app is ready.</remarks>
public static class Net
{
    /// <summary>Creates a request; call <see cref="ClientRequest.End(byte[])"/> to send it. Electron's <c>net.request()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="ArgumentException">An invalid URL, header, or option combination, with Electron's message.</exception>
    public static ClientRequest Request(ClientRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireReady();
        return new ClientRequest(options);
    }

    /// <summary>Creates a GET request for <paramref name="url"/>. Electron's <c>net.request(url)</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="ArgumentException">The URL is invalid or not http(s).</exception>
    public static ClientRequest Request(string url) => Request(new ClientRequestOptions { Url = url ?? throw new ArgumentNullException(nameof(url)) });

    /// <summary>Sends a request from the default session. Electron's <c>net.fetch()</c>.</summary>
    /// <exception cref="NetException">The request failed, or Electron refused it.</exception>
    public static Task<FetchResponse> FetchAsync(string input, FetchInit? init = null)
    {
        RequireReady();
        return Session.DefaultSession.FetchAsync(input, init);
    }

    /// <summary>Resolves <paramref name="host"/> with the default session. Electron's <c>net.resolveHost()</c>.</summary>
    /// <exception cref="HostResolutionException">The host could not be resolved.</exception>
    public static Task<ResolvedHost> ResolveHostAsync(string host)
    {
        RequireReady();
        return Session.DefaultSession.ResolveHostAsync(host);
    }

    /// <summary>Whether the machine looks connected: false is a strong sign it is offline, true is not proof any
    /// site is reachable. Electron's <c>net.isOnline()</c>.</summary>
    public static bool IsOnline()
    {
        bool? nla = OperatingSystem.IsWindows() ? WindowsNetworkAwareness.HasConnectedNetwork() : true;
        return NetworkStatus.IsOnline(nla, ReadInterfaces);
    }

    /// <summary>Whether the machine looks connected. Electron's <c>net.online</c>.</summary>
    public static bool Online => IsOnline();

    private static IReadOnlyList<NetworkStatus.Interface>? ReadInterfaces()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().Select(n => new NetworkStatus.Interface(
                n.Id, n.Name, n.NetworkInterfaceType == NetworkInterfaceType.Loopback, n.OperationalStatus == OperationalStatus.Up,
                [.. n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(a => !OperatingSystem.IsWindows() || a.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred)]))];
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static void RequireReady()
    {
        if (RiftwayApp.CurrentOrNull is not { IsReady: true })
            throw new InvalidOperationException("net module can only be used after app is ready");
    }
}

/// <summary>Chromium's Windows online test (<c>NetworkChangeNotifierWin::RecomputeCurrentConnectionType</c> with
/// <c>ConnectionTypeFromInterfaces</c>): offline only when NLA lists no network or no usable interface is up.</summary>
internal static class NetworkStatus
{
    /// <summary>An adapter: its id (Windows' AdapterName), friendly name, whether loopback or up, and each IP
    /// address's duplicate-detection state (true when Preferred).</summary>
    internal sealed record Interface(string AdapterName, string FriendlyName, bool IsLoopback, bool IsUp, IReadOnlyList<bool> AddressesPreferred);

    /// <param name="nlaConnected">Whether NLA lists a network; null when the query failed (treated as online).</param>
    /// <param name="interfaces">The adapters; null result when they cannot be read (treated as online).</param>
    public static bool IsOnline(bool? nlaConnected, Func<IReadOnlyList<Interface>?> interfaces)
    {
        if (nlaConnected is null) return true;
        if (nlaConnected == false) return false;
        if (interfaces() is not { } list) return true;
        // GetNetworkList(EXCLUDE_HOST_SCOPE_VIRTUAL_INTERFACES), then ConnectionTypeFromInterfaceList's skips.
        return list.Any(n => !n.IsLoopback && n.IsUp
            && !n.AdapterName.Contains("VMnet", StringComparison.Ordinal)
            && n.FriendlyName != "Teredo Tunneling Pseudo-Interface"
            && !n.FriendlyName.Contains("vmnet", StringComparison.OrdinalIgnoreCase)
            && n.AddressesPreferred.Any(preferred => preferred));
    }
}
