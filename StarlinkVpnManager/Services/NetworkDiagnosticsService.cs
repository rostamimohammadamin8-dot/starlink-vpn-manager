using StarlinkVpnManager.Models;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;

namespace StarlinkVpnManager.Services;

internal sealed class NetworkDiagnosticsService
{
    private const int ProbeCount = 5;
    private const int ProbeTimeoutMilliseconds = 1200;
    private const int TransportProbeCount = 3;
    private static readonly IPAddress[] PublicTargets =
    [
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8")
    ];
    private readonly LatencyService _latencyService;

    public NetworkDiagnosticsService(LatencyService? latencyService = null)
    {
        _latencyService = latencyService ?? new LatencyService();
    }

    public async Task<NetworkDiagnosticsReport> DiagnoseAsync(
        IReadOnlyList<string> endpointHosts,
        int? persistentKeepaliveSeconds,
        int? configuredMtu,
        CancellationToken cancellationToken = default)
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
            .Select(host => ResolveEndpointAsync(host, cancellationToken))
            .ToArray();
        var tcpTasks = Enumerable.Range(1, TransportProbeCount).Select(attempt =>
            _latencyService.MeasureEndpointsAsync(
                [
                    new LatencyEndpoint($"Cloudflare TCP #{attempt}", "1.1.1.1"),
                    new LatencyEndpoint($"Google TCP #{attempt}", "8.8.8.8")
                ],
                timeoutMs: 3000,
                cancellationToken));
        var httpTasks = Enumerable.Range(1, TransportProbeCount).SelectMany(attempt =>
            new[]
            {
                MeasureHttpAsync($"Cloudflare HTTP #{attempt}", "https://www.cloudflare.com/", cancellationToken),
                MeasureHttpAsync($"Google HTTP #{attempt}", "https://www.google.com/generate_204", cancellationToken)
            }).ToArray();

        await Task.WhenAll(probeTasks);
        if (gatewayTask is not null)
        {
            await gatewayTask;
        }

        var endpoints = await Task.WhenAll(endpointTasks);
        var tcpResults = (await Task.WhenAll(tcpTasks)).SelectMany(results => results).ToArray();
        var httpResults = await Task.WhenAll(httpTasks);
        return new NetworkDiagnosticsReport(
            DateTimeOffset.Now,
            activeRoute,
            gatewayTask is null ? null : await gatewayTask,
            probeTasks.Select(task => task.Result).ToArray(),
            endpoints,
            persistentKeepaliveSeconds,
            configuredMtu)
        {
            TransportLatencies =
            [
                .. tcpResults.Select(result => new NetworkLatencyResult(
                    result.Name,
                    $"{result.Host}:443",
                    "TCP",
                    result.LatencyMs,
                    result.Error)),
                .. httpResults
            ]
        };
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

    private static async Task<EndpointResolution> ResolveEndpointAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return new EndpointResolution(host, [address], null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
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

    private async Task<NetworkLatencyResult> MeasureHttpAsync(
        string name,
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var latency = await _latencyService.MeasureHttpLatencyAsync(
                target,
                timeoutMs: 3000,
                cancellationToken);
            return new NetworkLatencyResult(name, target, "HTTP", latency, null);
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or IOException)
        {
            return new NetworkLatencyResult(name, target, "HTTP", null, ex.Message);
        }
    }
}
