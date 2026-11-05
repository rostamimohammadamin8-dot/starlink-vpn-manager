using System.Collections;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text.Json;
using System.Windows;
#if NET10_0_WINDOWS10_0_19041_0
using ServiceLib.Resx;
#endif
using v2rayN.CustomExtensions.Services;

namespace v2rayN.CustomExtensions.Tests;

public class CustomExtensionTests
{
    [Test]
    public async Task EnsureSubscription_IsIdempotentAndDeduplicatesUrlAndRemarkMatches()
    {
        var storage = new MemoryStorage(
        [
            new SubscriptionEntry("url-match", "Legacy name", CustomBuiltinHandler.SubscriptionUrl),
            new SubscriptionEntry("remark-match", CustomBuiltinHandler.SubscriptionRemarks, "https://old.example/sub")
        ]);

        var first = await CustomBuiltinHandler.EnsureSubscriptionAsync(storage);
        var second = await CustomBuiltinHandler.EnsureSubscriptionAsync(storage);

        if (first.Added || second.Added || storage.Subscriptions.Count != 1 ||
            storage.Subscriptions[0].Url != CustomBuiltinHandler.SubscriptionUrl ||
            storage.Subscriptions[0].Remarks != CustomBuiltinHandler.SubscriptionRemarks)
        {
            throw new InvalidOperationException("Subscription injection was not idempotent or did not deduplicate both identity forms.");
        }
    }

    [Test]
    public async Task EnsureSubscription_DoesNotDeleteCanonicalWhenDuplicateRowsShareId()
    {
        var storage = new MemoryStorage(
        [
            new SubscriptionEntry("shared-id", "Old remark", CustomBuiltinHandler.SubscriptionUrl),
            new SubscriptionEntry("shared-id", CustomBuiltinHandler.SubscriptionRemarks, "https://old.example/sub")
        ]);

        var result = await CustomBuiltinHandler.EnsureSubscriptionAsync(storage);

        if (result.Subscription.Id != "shared-id" ||
            storage.Subscriptions.Count != 1 ||
            storage.Subscriptions[0] != result.Subscription)
        {
            throw new InvalidOperationException("Reconciliation deleted the canonical subscription or retained same-ID duplicates.");
        }
    }

    [Test]
    public async Task EnsureSubscriptions_RegistersAndDeduplicatesAllBarryFarFeeds()
    {
        var feeds = CustomBuiltinHandler.BuiltinSubscriptions
            .Where(feed => feed.Url.Contains("barry-far", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var storage = new MemoryStorage(
        [
            new SubscriptionEntry("unified-id", "Old unified label", CustomBuiltinHandler.BarryFarUnifiedUrl),
            new SubscriptionEntry("unified-duplicate", "Barry-Far Unified Feed", "https://old.example/unified"),
            new SubscriptionEntry("legacy-vless", "Barry-Far VLESS Feed", "https://old.example/vless")
        ]);

        var firstRun = await CustomBuiltinHandler.EnsureSubscriptionsAsync(storage, feeds);
        var secondRun = await CustomBuiltinHandler.EnsureSubscriptionsAsync(storage, feeds);

        if (firstRun.Count != 3 ||
            firstRun.Count(result => result.Added) != 1 ||
            secondRun.Any(result => result.Added) ||
            storage.Subscriptions.Count != 3 ||
            feeds.Any(feed => storage.Subscriptions.Count(item =>
                string.Equals(item.Url, feed.Url, StringComparison.OrdinalIgnoreCase)) != 1) ||
            storage.Subscriptions.Single(item => item.Url == CustomBuiltinHandler.BarryFarUnifiedUrl).Id != "unified-id" ||
            storage.Subscriptions.Single(item => item.Url == CustomBuiltinHandler.BarryFarVlessUrl).Id != "legacy-vless")
        {
            throw new InvalidOperationException("Barry-Far feeds were not canonicalized and deduplicated idempotently.");
        }
    }

    [Test]
    public async Task EnsureSubscriptions_PreservesCanonicalRowsAcrossFeedsWithDuplicateIds()
    {
        var feeds = CustomBuiltinHandler.BuiltinSubscriptions
            .Where(feed => feed.Url.Contains("barry-far", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var storage = new MemoryStorage(
        [
            new SubscriptionEntry("shared-id", "Old unified label", CustomBuiltinHandler.BarryFarUnifiedUrl),
            new SubscriptionEntry("shared-id", "Barry-Far Unified Feed", "https://old.example/unified"),
            new SubscriptionEntry("vless-id", "Barry-Far VLESS Feed", CustomBuiltinHandler.BarryFarVlessUrl)
        ]);

        var results = await CustomBuiltinHandler.EnsureSubscriptionsAsync(storage, feeds);

        if (results.Count != 3 ||
            storage.Subscriptions.Count != 3 ||
            storage.Subscriptions.Count(item => item.Url == CustomBuiltinHandler.BarryFarUnifiedUrl) != 1 ||
            storage.Subscriptions.Count(item => item.Url == CustomBuiltinHandler.BarryFarVlessUrl) != 1 ||
            storage.Subscriptions.Single(item => item.Url == CustomBuiltinHandler.BarryFarUnifiedUrl).Id != "shared-id" ||
            storage.Subscriptions.Single(item => item.Url == CustomBuiltinHandler.BarryFarVlessUrl).Id != "vless-id")
        {
            throw new InvalidOperationException("Reconciling one Barry-Far feed removed or replaced another feed's canonical row.");
        }
    }

    [Test]
    public async Task JsonStorage_PreservesOtherConfigurationAndWritesSubscription()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v2rayn-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "guiNConfig.json");
        await File.WriteAllTextAsync(path, """{"theme":"dark","subscriptions":[]}""");

        try
        {
            var storage = new JsonConfigStorageProvider(path);
            await CustomBuiltinHandler.EnsureSubscriptionAsync(storage);
            var entries = await storage.GetSubscriptionsAsync();
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            if (entries.Count != 1 ||
                config.RootElement.GetProperty("theme").GetString() != "dark" ||
                entries[0].Url != CustomBuiltinHandler.SubscriptionUrl)
            {
                throw new InvalidOperationException("JSON storage did not preserve the existing config or write the subscription.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task JsonStorage_UpsertsAndDeletesLegacyEntriesBySynthesizedId()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"v2rayn-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "guiNConfig.json");
        await File.WriteAllTextAsync(path, """{"subscriptions":[{"remarks":"Legacy","url":"https://legacy.example/sub"}]}""");

        try
        {
            var storage = new JsonConfigStorageProvider(path);
            var legacy = (await storage.GetSubscriptionsAsync()).Single();
            var updated = legacy with { Remarks = "Updated legacy" };

            await storage.UpsertSubscriptionAsync(updated);
            var afterUpsert = (await storage.GetSubscriptionsAsync()).Single();
            if (afterUpsert != updated)
            {
                throw new InvalidOperationException("Upserting a synthesized legacy ID did not update its existing entry.");
            }

            await storage.DeleteSubscriptionAsync(updated.Id);
            if ((await storage.GetSubscriptionsAsync()).Count != 0)
            {
                throw new InvalidOperationException("Deleting a synthesized legacy ID did not remove its entry.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public Task FlagConverter_ReturnsUnicodeRegionalIndicatorFlags()
    {
        if (GeoIpService.ToFlagEmoji("US") != "🇺🇸" ||
            GeoIpService.ToFlagEmoji("ir") != "🇮🇷")
        {
            throw new InvalidOperationException("Country code conversion returned an incorrect flag.");
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task PersianLocalization_CoversEveryMissingPersianResourceAndProvidesRtlHook()
    {
#if NET10_0_WINDOWS10_0_19041_0
        var defaultResources = ResUI.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, true)
            ?? throw new InvalidOperationException("The default UI resource set could not be loaded.");
        var persianResources = ResUI.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("fa"), true, false)
            ?? throw new InvalidOperationException("The Persian UI resource set could not be loaded.");
        var defaultKeys = defaultResources.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var persianKeys = persianResources.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var missingKeys = defaultKeys.Except(persianKeys).ToHashSet(StringComparer.Ordinal);

        if (missingKeys.Count != 44 ||
            !missingKeys.SetEquals(PersianLocalization.MissingTranslations.Keys) ||
            PersianLocalization.MissingTranslations.Values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("The sidecar Persian catalog does not cover exactly the missing default resource keys.");
        }
#else
        if (PersianLocalization.MissingTranslations.Count != 44 ||
            PersianLocalization.MissingTranslations.Values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("The sidecar Persian catalog does not contain all 44 supplemental translations.");
        }
#endif

        if (PersianLocalization.GetFlowDirection(CultureInfo.GetCultureInfo("fa-IR")) != FlowDirection.RightToLeft ||
            PersianLocalization.GetFlowDirection(CultureInfo.GetCultureInfo("en-US")) != FlowDirection.LeftToRight)
        {
            throw new InvalidOperationException("The Persian localization flow-direction hook returned an incorrect direction.");
        }

        return Task.CompletedTask;
    }

    [Test]
    public async Task PingService_MeasuresHttpJitterAndFlagsUnstableNodes()
    {
        using var unstableClient = new HttpClient(new DelayedResponseHandler([5, 80, 5]));
        using var unstableService = new PingService(unstableClient);
        var unstable = (await unstableService.MeasureHttpStabilityAsync(
            ["unstable"],
            _ => new Uri("https://probe.invalid/jitter"),
            maximumJitterMilliseconds: 10,
            samplesPerNode: 3)).Single();

        using var stableClient = new HttpClient(new DelayedResponseHandler([5, 5, 5]));
        using var stableService = new PingService(stableClient);
        var stable = (await stableService.MeasureHttpStabilityAsync(
            ["stable"],
            _ => new Uri("https://probe.invalid/stable"),
            maximumJitterMilliseconds: 1000,
            samplesPerNode: 3)).Single();

        if (!unstable.IsAlive ||
            unstable.IsStable ||
            unstable.SamplesMilliseconds.Count != 3 ||
            unstable.JitterMilliseconds is null or <= 10 ||
            unstable.StandardDeviationMilliseconds is null or <= 0 ||
            PingService.ClassifyReadiness(unstable) != NodeReadiness.PublicBrowsingOnly ||
            !stable.IsAlive ||
            !stable.IsStable ||
            stable.SamplesMilliseconds.Count != 3 ||
            PingService.ClassifyReadiness(stable) != NodeReadiness.TradingReady)
        {
            throw new InvalidOperationException("HTTP stability probes did not calculate jitter or apply the configured stability limit.");
        }
    }

    [Test]
    public async Task PingService_MeasuresTcpStabilityUsingConsecutiveHandshakes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await listener.AcceptTcpClientAsync();
            }
        });

        try
        {
            using var service = new PingService();
            var result = (await service.MeasureTcpStabilityAsync(
                ["loopback"],
                _ => ("127.0.0.1", port),
                maximumJitterMilliseconds: double.MaxValue,
                samplesPerNode: 3,
                probeTimeout: TimeSpan.FromSeconds(2))).Single();

            await acceptTask;
            if (!result.IsAlive ||
                !result.IsStable ||
                result.SamplesMilliseconds.Count != 3 ||
                result.StandardDeviationMilliseconds is null)
            {
                throw new InvalidOperationException("TCP stability probes did not complete all requested loopback handshakes.");
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task PingService_MarksFailedStabilitySamplesUnhealthy()
    {
        using var client = new HttpClient(new StaticResponseHandler());
        using var service = new PingService(client);
        var result = (await service.MeasureHttpStabilityAsync(
            ["offline"],
            _ => new Uri("https://probe.invalid/offline"),
            maximumJitterMilliseconds: 50,
            samplesPerNode: 3)).Single();

        if (result.IsAlive || result.IsStable || result.SamplesMilliseconds.Count != 0 ||
            string.IsNullOrWhiteSpace(result.Failure))
        {
            throw new InvalidOperationException("Failed HTTP stability samples were not reported as unhealthy with a diagnostic.");
        }
    }

    [Test]
    public Task GeoIpService_UsesPackagedDatabasePathRelativeToApplicationBase()
    {
        var expectedSuffix = Path.Combine("dat", "GeoLite2-Country.mmdb");
        var actualPath = GeoIpService.GetDefaultDatabasePath();
        if (!Path.IsPathRooted(actualPath) ||
            !actualPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase) ||
            GeoIpService.DefaultDatabaseRelativePath != "dat/GeoLite2-Country.mmdb")
        {
            throw new InvalidOperationException("The GeoIP database path does not match the sidecar packaging manifest.");
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task SidecarPackagingManifest_IsAvailableInBuildOutput()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "Packaging", "sidecar-manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.GetProperty("scope").GetString() != "v2rayN CustomExtensions sidecar" ||
            manifest.RootElement.GetProperty("runtimeAssets")[0].GetProperty("destination").GetString() !=
            "dat/GeoLite2-Country.mmdb relative to the application base directory")
        {
            throw new InvalidOperationException("The packaged sidecar manifest does not describe the runtime GeoIP path.");
        }

        return Task.CompletedTask;
    }

    [Test]
    public async Task PingService_OrdersLiveNodesAndMarksFailedNodesOffline()
    {
        using var client = new HttpClient(new StaticResponseHandler());
        using var service = new PingService(client);
        var results = await service.MeasureAsync(
            new[] { "slow", "offline", "fast" },
            node => new Uri($"https://probe.invalid/{node}"));

        if (results.Count != 3 ||
            results[0].Node != "fast" ||
            results[1].Node != "slow" ||
            results[2].Node != "offline" ||
            results[2].IsAlive)
        {
            throw new InvalidOperationException("Ping measurements were not sorted or offline nodes were not marked.");
        }
    }

    private sealed class MemoryStorage(IEnumerable<SubscriptionEntry> entries) : IConfigStorageProvider
    {
        public List<SubscriptionEntry> Subscriptions { get; } = entries.ToList();

        public Task<IReadOnlyList<SubscriptionEntry>> GetSubscriptionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SubscriptionEntry>>(Subscriptions.ToArray());

        public Task UpsertSubscriptionAsync(
            SubscriptionEntry subscription,
            CancellationToken cancellationToken = default)
        {
            Subscriptions.RemoveAll(entry => entry.Id == subscription.Id);
            Subscriptions.Add(subscription);
            return Task.CompletedTask;
        }

        public Task DeleteSubscriptionAsync(string id, CancellationToken cancellationToken = default)
        {
            Subscriptions.RemoveAll(entry => entry.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/slow")
            {
                await Task.Delay(30, cancellationToken);
            }

            var response = path switch
            {
                "/fast" => HttpStatusCode.NoContent,
                "/slow" => HttpStatusCode.OK,
                _ => HttpStatusCode.ServiceUnavailable
            };
            return new HttpResponseMessage(response);
        }
    }

    private sealed class DelayedResponseHandler(IReadOnlyList<int> delaysMilliseconds) : HttpMessageHandler
    {
        private int _requestIndex;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _requestIndex) - 1;
            var delay = delaysMilliseconds[Math.Min(index, delaysMilliseconds.Count - 1)];
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
