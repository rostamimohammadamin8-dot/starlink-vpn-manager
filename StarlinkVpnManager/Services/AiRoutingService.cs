using System.Net;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StarlinkVpnManager.Services;

internal sealed class AiRoutingService
{
    private static readonly string[][] AiDomainGroups =
    [
        ["openai.com", "chatgpt.com", "oaistatic.com"],
        ["anthropic.com", "claude.ai"],
        ["gemini.google.com", "generativelanguage.googleapis.com", "alkalimakersuite-pa.googleapis.com"],
        ["midjourney.com"],
        ["huggingface.co", "hf.co"]
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public JsonObject GenerateRoutingRules(
        bool enableAiBypass,
        IEnumerable<string>? ipCidrs = null,
        string outboundTag = "direct")
    {
        var rules = CreateRules(enableAiBypass, ipCidrs, outboundTag);
        return new JsonObject
        {
            ["route"] = new JsonObject
            {
                ["rules"] = rules
            }
        };
    }

    public string MergeRoutingRules(
        string singBoxConfiguration,
        bool enableAiBypass,
        IEnumerable<string>? ipCidrs = null,
        string outboundTag = "direct")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(singBoxConfiguration);
        var root = JsonNode.Parse(singBoxConfiguration) as JsonObject
                   ?? throw new InvalidDataException("sing-box configuration must be a JSON object.");
        var additions = CreateRules(enableAiBypass, ipCidrs, outboundTag);

        JsonObject route;
        if (!root.TryGetPropertyValue("route", out var routeNode))
        {
            route = new JsonObject();
            root["route"] = route;
        }
        else
        {
            route = routeNode as JsonObject
                    ?? throw new InvalidDataException("The sing-box route section must be an object.");
        }

        JsonArray rules;
        if (!route.TryGetPropertyValue("rules", out var rulesNode))
        {
            rules = new JsonArray();
            route["rules"] = rules;
        }
        else
        {
            rules = rulesNode as JsonArray
                    ?? throw new InvalidDataException("The sing-box route.rules section must be an array.");
        }

        if (enableAiBypass)
        {
            EnsureOutboundExists(root, outboundTag);
        }

        var cidrs = NormalizeCidrs(ipCidrs);
        for (var index = rules.Count - 1; index >= 0; index--)
        {
            if (IsGeneratedRule(rules[index], outboundTag, cidrs))
            {
                rules.RemoveAt(index);
            }
        }

        if (enableAiBypass)
        {
            var insertionIndex = FindCatchAllRuleIndex(rules);
            foreach (var rule in additions)
            {
                rules.Insert(insertionIndex++, rule?.DeepClone());
            }
        }

        return root.ToJsonString(JsonOptions);
    }

    private static JsonArray CreateRules(
        bool enabled,
        IEnumerable<string>? ipCidrs,
        string outboundTag)
    {
        ValidateOutboundTag(outboundTag);
        var cidrs = NormalizeCidrs(ipCidrs);
        var rules = new JsonArray();
        if (!enabled)
        {
            return rules;
        }

        foreach (var domainGroup in AiDomainGroups)
        {
            rules.Add(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(domainGroup.Select(value => JsonValue.Create(value)).ToArray()),
                ["outbound"] = outboundTag
            });
        }

        if (cidrs.Length > 0)
        {
            rules.Add(new JsonObject
            {
                ["ip_cidr"] = new JsonArray(cidrs.Select(value => JsonValue.Create(value)).ToArray()),
                ["outbound"] = outboundTag
            });
        }

        return rules;
    }

    private static string[] NormalizeCidrs(IEnumerable<string>? ipCidrs)
    {
        if (ipCidrs is null)
        {
            return [];
        }

        var cidrs = new List<string>();
        foreach (var cidr in ipCidrs)
        {
            if (string.IsNullOrWhiteSpace(cidr))
            {
                continue;
            }

            try
            {
                cidrs.Add(IPNetwork.Parse(cidr.Trim()).ToString());
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"Invalid IP CIDR: {cidr}", nameof(ipCidrs), ex);
            }
        }

        return cidrs.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsGeneratedRule(JsonNode? node, string outboundTag, IReadOnlyList<string> cidrs)
    {
        if (node is not JsonObject rule
            || !string.Equals(rule["outbound"]?.GetValue<string>(), outboundTag, StringComparison.Ordinal))
        {
            return false;
        }

        if (rule["ip_cidr"] is null
            && rule.Count == 2
            && AiDomainGroups.Any(group => MatchesValues(rule["domain_suffix"], group)))
        {
            return true;
        }

        return cidrs.Count > 0
               && rule.Count == 2
               && MatchesValues(rule["ip_cidr"], cidrs)
               && rule["domain_suffix"] is null;
    }

    private static bool MatchesValues(JsonNode? node, IReadOnlyCollection<string> expected)
    {
        if (node is not JsonArray array || array.Count != expected.Count)
        {
            return false;
        }

        var actual = array
            .Select(value => value?.GetValue<string>())
            .Where(value => value is not null)
            .ToArray();
        return actual.Length == expected.Count
               && actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static int FindCatchAllRuleIndex(JsonArray rules)
    {
        for (var index = rules.Count - 1; index >= 0; index--)
        {
            if (rules[index] is JsonObject rule
                && rule["outbound"] is not null
                && rule.All(property => property.Key == "outbound" || property.Key == "action"))
            {
                return index;
            }
        }

        return rules.Count;
    }

    private static void EnsureOutboundExists(JsonObject root, string outboundTag)
    {
        if (root["outbounds"] is not JsonArray outbounds
            || !outbounds.OfType<JsonObject>().Any(outbound =>
                string.Equals(outbound["tag"]?.GetValue<string>(), outboundTag, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"The sing-box configuration has no outbound tagged '{outboundTag}'.");
        }
    }

    private static void ValidateOutboundTag(string outboundTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundTag);
    }
}
