using System.Net;

namespace StarlinkVpnManager.Models;

public sealed record NetworkProbeResult(
    string Name,
    IPAddress Address,
    int Attempts,
    IReadOnlyList<long> SuccessfulSamples,
    int ProbeErrors = 0)
{
    public int LostPackets => Attempts - SuccessfulSamples.Count - ProbeErrors;
    public int? LossPercentage
    {
        get
        {
            var measuredAttempts = Attempts - ProbeErrors;
            return measuredAttempts == 0
                ? null
                : (int)Math.Round(LostPackets * 100d / measuredAttempts);
        }
    }

    public double? AverageLatencyMs => SuccessfulSamples.Count == 0 ? null : SuccessfulSamples.Average();
    public double? JitterMs => SuccessfulSamples.Count < 2
        ? null
        : SuccessfulSamples.Zip(SuccessfulSamples.Skip(1), (left, right) => Math.Abs(right - left)).Average();
}

public sealed record EndpointResolution(string Host, IReadOnlyList<IPAddress> Addresses, string? Error);

public sealed record ActiveNetworkRoute(
    string InterfaceName,
    IPAddress LocalAddress,
    IPAddress? GatewayAddress);

public sealed record NetworkDiagnosticsReport(
    DateTimeOffset CheckedAt,
    ActiveNetworkRoute? ActiveRoute,
    NetworkProbeResult? LocalGateway,
    IReadOnlyList<NetworkProbeResult> PublicTargets,
    IReadOnlyList<EndpointResolution> Endpoints,
    int? PersistentKeepaliveSeconds,
    int? ConfiguredMtu)
{
    public IReadOnlyList<string> Recommendations => BuildRecommendations();

    private IReadOnlyList<string> BuildRecommendations()
    {
        var recommendations = new List<string>();
        var repliedTargets = PublicTargets.Count(target => target.SuccessfulSamples.Count > 0);
        var anyPingExecuted = PublicTargets.Any(target => target.ProbeErrors < target.Attempts);

        if (PublicTargets.Any(target => target.ProbeErrors > 0)
            || LocalGateway?.ProbeErrors > 0)
        {
            recommendations.Add("برخی درخواست‌های ping در خود ویندوز اجرا نشدند؛ اتلاف شبکه را فقط از روی این نمونه‌ها نتیجه‌گیری نکنید.");
        }

        if (ActiveRoute is null)
        {
            recommendations.Add("رابط شبکهٔ مسیر پیش‌فرض پیدا نشد؛ اتصال شبکهٔ دستگاه را بررسی کنید.");
        }
        else if (LocalGateway is null)
        {
            recommendations.Add("برای رابط فعال دروازهٔ محلی قابل آزمایش پیدا نشد؛ این حالت در مسیرهای تونلی یا point-to-point طبیعی است.");
        }
        else if (LocalGateway.LostPackets > 0)
        {
            recommendations.Add("پاسخ دروازهٔ محلی ناپایدار بود؛ Wi-Fi، کابل شبکه و فاصله تا روتر را بررسی کنید.");
        }

        if (!anyPingExecuted)
        {
            recommendations.Add("آزمایش ping توسط ویندوز اجرا نشد؛ دسترسی شبکهٔ سیستم را بررسی کنید.");
        }
        else if (repliedTargets == 0)
        {
            recommendations.Add("از مقصدهای آزمایش پاسخ ICMP دریافت نشد؛ ممکن است ICMP مسدود باشد، پس کیفیت اینترنت از این آزمایش قابل تعیین نیست.");
        }
        else if (PublicTargets.Any(target => target.LossPercentage is >= 5))
        {
            recommendations.Add("در مسیر اینترنت اتلاف بسته دیده شد؛ آزمایش را چند بار تکرار و نتیجه را با وضعیت سرویس‌دهنده مقایسه کنید.");
        }

        if (PublicTargets.Any(target => target.AverageLatencyMs >= 150))
        {
            recommendations.Add("تأخیر مسیر اینترنت بالاست؛ ازدحام یا تغییر مسیر ممکن است نقش داشته باشد. پیش از تغییر MTU دوباره آزمایش کنید.");
        }

        if (PublicTargets.Any(target => target.JitterMs >= 40))
        {
            recommendations.Add("نوسان تأخیر مسیر اینترنت بالاست؛ در ساعات مختلف دوباره آزمایش کنید تا اثر ازدحام مشخص شود.");
        }

        foreach (var endpoint in Endpoints.Where(endpoint => endpoint.Error is not null))
        {
            recommendations.Add($"نام میزبان سرور VPN یعنی «{endpoint.Host}» resolve نشد؛ DNS دستگاه یا نام سرور را بررسی کنید.");
        }

        if (Endpoints.Count > 0 && Endpoints.All(endpoint => endpoint.Error is null))
        {
            recommendations.Add("نام میزبان سرور VPN به IP تبدیل شد؛ این نتیجه دسترسی‌پذیری UDP یا برقراری handshake را تضمین نمی‌کند.");
        }

        if (ConfiguredMtu is not null)
        {
            recommendations.Add($"MTU پروفایل {ConfiguredMtu} است؛ این آزمایش برای تغییر MTU کافی نیست، پس آن را خودکار تغییر ندهید.");
        }

        if (PersistentKeepaliveSeconds is null or <= 0 && Endpoints.Count > 0)
        {
            recommendations.Add("اگر پس از مدتی بی‌کاری دریافت داده متوقف می‌شود، دربارهٔ PersistentKeepalive=25 با ارائه‌دهندهٔ VPN مشورت کنید.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("نشانهٔ آشکاری از ناپایداری در این نمونه‌ها دیده نشد؛ اجرای سرویس به‌تنهایی handshake یا دسترسی اینترنت VPN را ثابت نمی‌کند.");
        }

        return recommendations;
    }
}
