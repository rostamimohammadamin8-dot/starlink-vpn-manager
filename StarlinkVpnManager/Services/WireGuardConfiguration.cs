using System.Text;
using StarlinkVpnManager.Models;

namespace StarlinkVpnManager.Services;

internal static class WireGuardConfiguration
{
    public static void Validate(string configuration)
    {
        var section = string.Empty;
        var hasInterface = false;
        var hasPrivateKey = false;
        var peerCount = 0;
        var peerHasPublicKey = false;

        foreach (var rawLine in configuration.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section == "Peer" && !peerHasPublicKey)
                {
                    throw new FormatException("هر بخش [Peer] باید یک PublicKey معتبر داشته باشد.");
                }

                var sectionName = line[1..^1].Trim();
                if (sectionName.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasInterface)
                    {
                        throw new FormatException("فایل باید دقیقاً یک بخش [Interface] داشته باشد.");
                    }

                    section = "Interface";
                    hasInterface = true;
                }
                else if (sectionName.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                {
                    section = "Peer";
                    peerCount++;
                    peerHasPublicKey = false;
                }
                else
                {
                    throw new FormatException($"بخش پشتیبانی‌نشده در پیکربندی: [{sectionName}]");
                }

                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                throw new FormatException("یک خط از فایل پیکربندی قالب کلید = مقدار ندارد.");
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (value.Length == 0)
            {
                throw new FormatException($"مقدار «{key}» خالی است.");
            }

            if (section == "Interface" && key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
            {
                if (hasPrivateKey)
                {
                    throw new FormatException("بخش [Interface] نمی‌تواند بیش از یک PrivateKey داشته باشد.");
                }

                ValidateKey(value, "PrivateKey");
                hasPrivateKey = true;
            }
            else if (section == "Peer" && key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
            {
                if (peerHasPublicKey)
                {
                    throw new FormatException("هر بخش [Peer] باید دقیقاً یک PublicKey داشته باشد.");
                }

                ValidateKey(value, "PublicKey");
                peerHasPublicKey = true;
            }
            else if (section is not ("Interface" or "Peer"))
            {
                throw new FormatException("ابتدا باید بخش [Interface] یا [Peer] تعریف شود.");
            }
        }

        if (section == "Peer" && !peerHasPublicKey)
        {
            throw new FormatException("هر بخش [Peer] باید یک PublicKey معتبر داشته باشد.");
        }

        if (!hasInterface || !hasPrivateKey || peerCount == 0)
        {
            throw new FormatException("پیکربندی باید شامل [Interface] با PrivateKey و دست‌کم یک [Peer] باشد.");
        }
    }

    public static WireGuardProfileHints GetProfileHints(string configuration)
    {
        int? mtu = null;
        int? keepalive = null;
        var endpointHosts = new List<string>();
        var section = string.Empty;

        foreach (var rawLine in configuration.Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (section.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                && key.Equals("MTU", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, out var parsedMtu))
            {
                mtu = parsedMtu;
            }
            else if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase)
                     && key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase)
                     && TryGetEndpointHost(value, out var host)
                     && !System.Net.IPAddress.TryParse(host, out _))
            {
                endpointHosts.Add(host);
            }
            else if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase)
                     && key.Equals("PersistentKeepalive", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(value, out var parsedKeepalive))
            {
                keepalive = parsedKeepalive;
            }
        }

        return new WireGuardProfileHints(mtu, endpointHosts, keepalive);
    }

    private static bool TryGetEndpointHost(string endpoint, out string host)
    {
        host = string.Empty;
        var value = endpoint.Trim();
        if (value.StartsWith('['))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket <= 1
                || closingBracket + 1 >= value.Length
                || value[closingBracket + 1] != ':'
                || !int.TryParse(value[(closingBracket + 2)..], out var ipv6Port)
                || ipv6Port is < 1 or > 65535
                || !System.Net.IPAddress.TryParse(value[1..closingBracket], out var ipv6Address)
                || ipv6Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = value[1..closingBracket];
            return true;
        }

        var separator = value.LastIndexOf(':');
        if (separator <= 0
            || value.IndexOf(':') != separator
            || !int.TryParse(value[(separator + 1)..], out var port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        host = value[..separator];
        return host.Length > 0;
    }

    private static void ValidateKey(string key, string keyName)
    {
        try
        {
            if (Convert.FromBase64String(key).Length != 32)
            {
                throw new FormatException();
            }
        }
        catch (FormatException)
        {
            throw new FormatException($"مقدار {keyName} یک کلید WireGuard معتبر نیست.");
        }
    }
}

internal sealed record WireGuardProfileHints(
    int? Mtu,
    IReadOnlyList<string> EndpointHosts,
    int? PersistentKeepaliveSeconds);
