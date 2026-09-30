using StarlinkVpnManager.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace StarlinkVpnManager.Services;

internal sealed class NetworkDiagnosticsService
{
    private const int ProbeCount = 5;
    private const int ProbeTimeoutMilliseconds = 1200;
    private static readonly IPAddress[] PublicTargets =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8")
    ];

    public async Task<NetworkDiagnosticsReport> DiagnoseAsync(
        IReadOnlyList<string> endpointHosts,
        int? persistentKeepaliveSeconds,
        int? configuredMtu)
    {
        ActiveNetworkRoute? activeRoute;
        try
        {
            activeRoute = FindActiveRoute();
        }
        catch (NetworkInformationException)
        {
            activeRoute = null;
        }

        var probeTasks = PublicTargets
            .Select((address, index) => MeasureAsync(index == 0 ? "Cloudflare DNS" : "Google DNS", address))
            .ToList();

        Task<NetworkProbeResult>? gatewayTask = activeRoute?.GatewayAddress is not { } gateway
            ? null
            : MeasureAsync("دروازهٔ محلی", gateway);

        var endpointTasks = endpointHosts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ResolveEndpointAsync)
            .ToArray();

        await Task.WhenAll(probeTasks);
        if (gatewayTask is not null)
        {
            await gatewayTask;
        }

        var endpoints = await Task.WhenAll(endpointTasks);
        return new NetworkDiagnosticsReport(
            DateTimeOffset.Now,
            activeRoute,
            gatewayTask is null ? null : await gatewayTask,
            probeTasks.Select(task => task.Result).ToArray(),
            endpoints,
            persistentKeepaliveSeconds,
            configuredMtu);
    }

    private static ActiveNetworkRoute? FindActiveRoute()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Connect(new IPEndPoint(PublicTargets[0], 443));
        }
        catch (SocketException)
        {
            return null;
        }

        if (socket.LocalEndPoint is not IPEndPoint { Address: var localAddress }
            || localAddress.Equals(IPAddress.Any))
        {
            return null;
        }

        var network = NetworkInterface.GetAllNetworkInterfaces()
            .Where(candidate => candidate.OperationalStatus == OperationalStatus.Up)
            .FirstOrDefault(candidate => candidate.GetIPProperties().UnicastAddresses
                .Any(address => address.Address.Equals(localAddress)));
        if (network is null)
        {
            return new ActiveNetworkRoute("رابط ناشناخته", localAddress, null);
        }

        var gateway = network.GetIPProperties().GatewayAddresses
            .Select(address => address.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                                       && !IPAddress.IsLoopback(address)
                                       && !address.Equals(IPAddress.Any));

        return new ActiveNetworkRoute(network.Name, localAddress, gateway);
    }

    private static async Task<NetworkProbeResult> MeasureAsync(string name, IPAddress address)
    {
        var samples = new List<long>(ProbeCount);
        var probeErrors = 0;
        using var ping = new Ping();

        for (var attempt = 0; attempt < ProbeCount; attempt++)
        {
            try
            {
                var reply = await ping.SendPingAsync(address, ProbeTimeoutMilliseconds);
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                }
            }
            catch (PingException)
            {
                probeErrors++;
            }
        }

        return new NetworkProbeResult(name, address, ProbeCount, samples, probeErrors);
    }

    private static async Task<EndpointResolution> ResolveEndpointAsync(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return new EndpointResolution(host, [address], null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.Length == 0
                ? new EndpointResolution(host, [], "نام میزبان IP برنگرداند.")
                : new EndpointResolution(host, addresses, null);
        }
        catch (SocketException)
        {
            return new EndpointResolution(host, [], "پرس‌وجوی DNS ناموفق بود.");
        }
        catch (ArgumentException)
        {
            return new EndpointResolution(host, [], "نام میزبان معتبر نیست.");
        }
    }
}
