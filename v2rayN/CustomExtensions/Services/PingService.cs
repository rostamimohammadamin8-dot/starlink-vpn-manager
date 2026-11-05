using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace v2rayN.CustomExtensions.Services;

public sealed record PingResult<TNode>(
    TNode Node,
    long? LatencyMilliseconds,
    bool IsAlive,
    string? Failure = null);

public sealed record StabilityProbeResult<TNode>(
    TNode Node,
    IReadOnlyList<double> SamplesMilliseconds,
    double? MeanLatencyMilliseconds,
    double? JitterMilliseconds,
    double? StandardDeviationMilliseconds,
    int RequestedSamples,
    bool IsAlive,
    bool IsStable,
    string? Failure = null);

public enum NodeReadiness
{
    Unreachable,
    PublicBrowsingOnly,
    TradingReady
}

public sealed class PingService : IDisposable
{
    private static readonly Uri DefaultProbeUri = new("http://www.gstatic.com/generate_204");
    private const int MaximumConcurrency = 100;
    public const double DefaultTradingLatencyLimitMilliseconds = 250;
    public const double DefaultTradingStandardDeviationLimitMilliseconds = 25;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public PingService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsClient = httpClient is null;
    }

    public async Task<IReadOnlyList<PingResult<TNode>>> MeasureAsync<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, Uri>? getProbeUri = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        using var semaphore = new SemaphoreSlim(100, 100);
        var results = new ConcurrentBag<PingResult<TNode>>();

        var tasks = nodes.Select(async node =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var uri = getProbeUri?.Invoke(node) ?? DefaultProbeUri;
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                var timer = Stopwatch.StartNew();
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                timer.Stop();

                var status = (int)response.StatusCode;
                var alive = status is >= 200 and < 400 && timer.ElapsedMilliseconds <= 5000;
                results.Add(alive
                    ? new PingResult<TNode>(node, timer.ElapsedMilliseconds, true)
                    : new PingResult<TNode>(node, null, false, $"HTTP {(int)response.StatusCode} or RTT exceeded 5000 ms"));
            }
            catch (HttpRequestException ex)
            {
                results.Add(new PingResult<TNode>(node, null, false, ex.Message));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(new PingResult<TNode>(node, null, false, ex.Message));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results
            .OrderByDescending(result => result.IsAlive)
            .ThenBy(result => result.LatencyMilliseconds ?? long.MaxValue)
            .ToArray();
    }

    public async Task<IReadOnlyList<PingResult<TNode>>> MeasureActiveAsync<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, Uri>? getProbeUri = null,
        CancellationToken cancellationToken = default)
    {
        var results = await MeasureAsync(nodes, getProbeUri, cancellationToken);
        return results.Where(result => result.IsAlive).ToArray();
    }

    public static NodeReadiness ClassifyReadiness<TNode>(
        StabilityProbeResult<TNode> result,
        double maximumMeanLatencyMilliseconds = DefaultTradingLatencyLimitMilliseconds,
        double maximumStandardDeviationMilliseconds = DefaultTradingStandardDeviationLimitMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!double.IsFinite(maximumMeanLatencyMilliseconds) || maximumMeanLatencyMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumMeanLatencyMilliseconds));
        }

        if (!double.IsFinite(maximumStandardDeviationMilliseconds) || maximumStandardDeviationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumStandardDeviationMilliseconds));
        }

        if (!result.IsAlive)
        {
            return NodeReadiness.Unreachable;
        }

        return result.IsStable &&
               result.MeanLatencyMilliseconds is >= 0 &&
               result.MeanLatencyMilliseconds < maximumMeanLatencyMilliseconds &&
               result.StandardDeviationMilliseconds is >= 0 &&
               result.StandardDeviationMilliseconds <= maximumStandardDeviationMilliseconds
            ? NodeReadiness.TradingReady
            : NodeReadiness.PublicBrowsingOnly;
    }

    public Task<IReadOnlyList<StabilityProbeResult<TNode>>> MeasureHttpStabilityAsync<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, Uri>? getProbeUri,
        double maximumJitterMilliseconds,
        int samplesPerNode = 5,
        TimeSpan? probeTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getProbeUri);
        var timeout = ValidateStabilityOptions(samplesPerNode, maximumJitterMilliseconds, probeTimeout);

        return MeasureStabilityCoreAsync(
            nodes,
            maximumJitterMilliseconds,
            samplesPerNode,
            async (node, token) =>
            {
                var uri = getProbeUri(node)
                    ?? throw new InvalidOperationException("The HTTP probe URI selector returned null.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                var timer = Stopwatch.StartNew();
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedCts.Token);
                timer.Stop();
                if ((int)response.StatusCode is < 200 or >= 400)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} from {uri}.");
                }

                return timer.Elapsed.TotalMilliseconds;
            },
            probeTimeout,
            cancellationToken);
    }

    public Task<IReadOnlyList<StabilityProbeResult<TNode>>> MeasureTcpStabilityAsync<TNode>(
        IEnumerable<TNode> nodes,
        Func<TNode, (string Host, int Port)> getEndpoint,
        double maximumJitterMilliseconds,
        int samplesPerNode = 5,
        TimeSpan? probeTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getEndpoint);
        var timeout = ValidateStabilityOptions(samplesPerNode, maximumJitterMilliseconds, probeTimeout);

        return MeasureStabilityCoreAsync(
            nodes,
            maximumJitterMilliseconds,
            samplesPerNode,
            async (node, token) =>
            {
                var (host, port) = getEndpoint(node);
                if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
                {
                    throw new ArgumentException("A TCP probe requires a host and a port from 1 through 65535.");
                }

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                using var client = new TcpClient();
                var timer = Stopwatch.StartNew();
                await client.ConnectAsync(host, port, linkedCts.Token);
                timer.Stop();
                return timer.Elapsed.TotalMilliseconds;
            },
            probeTimeout,
            cancellationToken);
    }

    private static TimeSpan ValidateStabilityOptions(
        int samplesPerNode,
        double maximumJitterMilliseconds,
        TimeSpan? probeTimeout)
    {
        if (samplesPerNode < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerNode), "At least two samples are required to measure stability.");
        }

        if (!double.IsFinite(maximumJitterMilliseconds) || maximumJitterMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJitterMilliseconds), "The jitter limit must be finite and non-negative.");
        }

        var timeout = probeTimeout ?? TimeSpan.FromSeconds(5);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout), "The probe timeout must be finite and positive.");
        }

        return timeout;
    }

    private static async Task<IReadOnlyList<StabilityProbeResult<TNode>>> MeasureStabilityCoreAsync<TNode>(
        IEnumerable<TNode> nodes,
        double maximumJitterMilliseconds,
        int samplesPerNode,
        Func<TNode, CancellationToken, Task<double>> probeAsync,
        TimeSpan? probeTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        using var semaphore = new SemaphoreSlim(MaximumConcurrency, MaximumConcurrency);
        var indexedNodes = nodes.Select((node, index) => (Node: node, Index: index)).ToArray();
        var tasks = indexedNodes.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var samples = new List<double>(samplesPerNode);
                string? failure = null;
                for (var sampleIndex = 0; sampleIndex < samplesPerNode; sampleIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        samples.Add(await probeAsync(item.Node, cancellationToken));
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failure ??= $"Probe timed out after {(probeTimeout ?? TimeSpan.FromSeconds(5)).TotalMilliseconds:0} ms.";
                    }
                    catch (HttpRequestException exception)
                    {
                        failure ??= exception.Message;
                    }
                    catch (SocketException exception)
                    {
                        failure ??= exception.Message;
                    }
                }

                return (item.Index, Result: CreateStabilityResult(
                    item.Node,
                    samples,
                    samplesPerNode,
                    maximumJitterMilliseconds,
                    failure));
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.OrderBy(item => item.Index).Select(item => item.Result).ToArray();
    }

    private static StabilityProbeResult<TNode> CreateStabilityResult<TNode>(
        TNode node,
        IReadOnlyList<double> samples,
        int requestedSamples,
        double maximumJitterMilliseconds,
        string? failure)
    {
        var isAlive = samples.Count == requestedSamples;
        if (samples.Count == 0)
        {
            return new StabilityProbeResult<TNode>(
                node, samples, null, null, null, requestedSamples, false, false, failure);
        }

        var mean = samples.Average();
        var variance = samples.Average(sample => Math.Pow(sample - mean, 2));
        var standardDeviation = Math.Sqrt(variance);
        var jitter = samples.Count < 2
            ? 0
            : samples.Zip(samples.Skip(1), (previous, current) => Math.Abs(current - previous)).Average();

        return new StabilityProbeResult<TNode>(
            node,
            samples,
            mean,
            jitter,
            standardDeviation,
            requestedSamples,
            isAlive,
            isAlive && jitter <= maximumJitterMilliseconds,
            failure);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
