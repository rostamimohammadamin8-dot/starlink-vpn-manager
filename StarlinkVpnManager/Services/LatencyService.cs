using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace StarlinkVpnManager.Services;

internal sealed class LatencyService
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public async Task<long> MeasureTcpLatencyAsync(
        string host,
        int port,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ValidateTimeout(timeoutMs);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using var client = new TcpClient();
        var timer = Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return timer.ElapsedMilliseconds;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP connection to {host}:{port} timed out after {timeoutMs} ms.", ex);
        }
    }

    public async Task<long> MeasureHttpLatencyAsync(
        string targetUrl,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUrl);
        ValidateTimeout(timeoutMs);
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A valid absolute HTTP or HTTPS URL is required.", nameof(targetUrl));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        var timer = Stopwatch.StartNew();

        try
        {
            using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return timer.ElapsedMilliseconds;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"HTTP request to {uri.Host} timed out after {timeoutMs} ms.", ex);
        }
    }

    public async Task<IReadOnlyList<LatencyMeasurement>> MeasureEndpointsAsync(
        IEnumerable<LatencyEndpoint> endpoints,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var endpointList = endpoints.ToArray();
        var tasks = endpointList.Select(async endpoint =>
        {
            try
            {
                var latency = await MeasureTcpLatencyAsync(endpoint.Host, endpoint.Port, timeoutMs, ct);
                return new LatencyMeasurement(endpoint.Name, endpoint.Host, latency, null);
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or IOException)
            {
                return new LatencyMeasurement(endpoint.Name, endpoint.Host, null, ex.Message);
            }
        });

        return await Task.WhenAll(tasks);
    }

    private static void ValidateTimeout(int timeoutMs)
    {
        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout must be greater than zero.");
        }
    }
}

internal sealed record LatencyEndpoint(string Name, string Host, int Port = 443);

internal sealed record LatencyMeasurement(string Name, string Host, long? LatencyMs, string? Error);
