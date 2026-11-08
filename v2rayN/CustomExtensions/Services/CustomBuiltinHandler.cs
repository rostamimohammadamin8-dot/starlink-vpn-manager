namespace v2rayN.CustomExtensions.Services;

public sealed record SubscriptionEnsureResult(SubscriptionEntry Subscription, bool Added);
public sealed record BuiltinSubscriptionDefinition(string Url, string Remarks);

public static class CustomBuiltinHandler
{
    public const string SubscriptionUrl =
        "https://raw.githubusercontent.com/rostamimohammadamin8-dot/Free-VPN-Config-Pack-2026/main/sub.txt";

    public const string SubscriptionRemarks = "\u26A1 Free Config Pack";

    public const string BarryFarUnifiedUrl =
        "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/All_Configs_Sub.txt";
    public const string BarryFarVlessUrl =
        "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Sub1.txt";
    public const string BarryFarVmessTrojanUrl =
        "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Sub2.txt";

    public static IReadOnlyList<BuiltinSubscriptionDefinition> BuiltinSubscriptions { get; } =
    [
        new(SubscriptionUrl, SubscriptionRemarks),
        new(BarryFarUnifiedUrl, "Barry-Far Unified Feed"),
        new(BarryFarVlessUrl, "Barry-Far VLESS Feed"),
        new(BarryFarVmessTrojanUrl, "Barry-Far VMess/Trojan Feed")
    ];

    public static async Task<SubscriptionEnsureResult> EnsureSubscriptionAsync(
        IConfigStorageProvider storage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return await EnsureSubscriptionAsync(
            storage,
            new BuiltinSubscriptionDefinition(SubscriptionUrl, SubscriptionRemarks),
            cancellationToken);
    }

    public static async Task<IReadOnlyList<SubscriptionEnsureResult>> EnsureSubscriptionsAsync(
        IConfigStorageProvider storage,
        IEnumerable<BuiltinSubscriptionDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(definitions);

        var feeds = definitions.ToArray();
        var duplicateUrl = feeds
            .GroupBy(feed => feed.Url.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateUrl is not null)
        {
            throw new ArgumentException($"A built-in feed URL is listed more than once: {duplicateUrl.Key}", nameof(definitions));
        }

        var results = new List<SubscriptionEnsureResult>(feeds.Length);
        foreach (var feed in feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await EnsureSubscriptionAsync(storage, feed, cancellationToken));
        }

        return results;
    }

    private static async Task<SubscriptionEnsureResult> EnsureSubscriptionAsync(
        IConfigStorageProvider storage,
        BuiltinSubscriptionDefinition definition,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Remarks);
        if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var feedUri) ||
            (feedUri.Scheme != Uri.UriSchemeHttps && feedUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Built-in feed URLs must be absolute HTTP or HTTPS URLs.", nameof(definition));
        }

        var subscriptions = await storage.GetSubscriptionsAsync(cancellationToken);
        var matches = subscriptions
            .Where(item =>
                string.Equals(item.Url.Trim(), definition.Url.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Remarks.Trim(), definition.Remarks.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(item =>
                string.Equals(item.Url.Trim(), definition.Url.Trim(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var added = matches.Length == 0;
        var canonical = added
            ? new SubscriptionEntry(Guid.NewGuid().ToString("N"), definition.Remarks, definition.Url)
            : matches[0] with
            {
                Remarks = definition.Remarks,
                Url = definition.Url
            };

        var hasDuplicateId = matches
            .Skip(1)
            .Any(item => string.Equals(item.Id, canonical.Id, StringComparison.Ordinal));
        if (added || canonical != matches[0] || hasDuplicateId)
        {
            await storage.UpsertSubscriptionAsync(canonical, cancellationToken);
        }

        foreach (var duplicate in matches.Skip(1))
        {
            var duplicateIdIsShared = subscriptions.Count(item =>
                string.Equals(item.Id, duplicate.Id, StringComparison.Ordinal)) > 1;
            if (!duplicateIdIsShared)
            {
                await storage.DeleteSubscriptionAsync(duplicate.Id, cancellationToken);
            }
        }

        return new SubscriptionEnsureResult(canonical, added);
    }
}
