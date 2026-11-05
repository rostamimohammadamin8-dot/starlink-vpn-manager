using System.Globalization;
using System.Windows;

namespace v2rayN.CustomExtensions.Services;

public static class PersianLocalization
{
    private static readonly IReadOnlyDictionary<string, string> MissingPersianTranslations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["menuCheckAndUpdate"] = "بررسی و به‌روزرسانی",
            ["TbSettingsMux4RayConcurrency"] = "تعداد اتصال‌های هم‌زمان Xray Mux",
            ["TbSettingsMux4RayXudpConcurrency"] = "تعداد اتصال‌های هم‌زمان XUDP در Xray Mux",
            ["TbSettingsMux4RayXudpProxyUDP443"] = "ارسال UDP پورت ۴۴۳ از طریق پراکسی در XUDP",
            ["TbHttpOutboundHeaders"] = "سرآیندهای درخواست HTTP",
            ["TipHttpOutboundHeaders"] = "سرآیندهای دلخواه درخواست خروجی HTTP را به‌صورت شیء JSON وارد کنید؛ مقدار هر سرآیند باید رشته یا آرایه‌ای از رشته‌ها باشد.",
            ["TbVerifyPeerCertByName"] = "اعتبارسنجی گواهی همتا بر اساس نام",
            ["MsgAllowInsecureDeprecated"] = "این گره از اتصال رمزنگاری‌نشده استفاده می‌کند و ممکن است واسطه‌های شبکه بتوانند ارتباط شما را مشاهده کنند. به دلایل امنیتی، این نوع گره در Xray-core نسخهٔ ۲۶٫۲٫۶ و بالاتر قابل استفاده نیست. اگر گره را خودتان ساخته‌اید، TLS یا رمزنگاری امن دیگری را فعال کنید یا گواهی را با pinSHA256 تثبیت کنید. اگر گره متعلق به ارائه‌دهنده است، از او ارتقای فنی بخواهید؛ در صورت همکاری‌نکردن، ارائه‌دهندهٔ امن‌تری انتخاب کنید. اطلاعات بیشتر: https://github.com/2dust/v2rayN/discussions/9460",
            ["TbRouteExcludeAddress"] = "نشانی‌های مستثنا از مسیر TUN",
            ["TbRouteExcludeAddressTip"] = "برای جداکردن نشانی‌ها از ویرگول (،) استفاده کنید.",
            ["MsgTunRouteExcludeInvalidAddress"] = "نشانی نامعتبر در فهرست استثناهای مسیر TUN: {0}",
            ["MsgOptionsConflict"] = "تعارض میان گزینهٔ {0} و گزینهٔ {1}",
            ["TbEnableFinalFragment"] = "فعال‌کردن قطعه‌بندی نهایی بسته",
            ["TbEnableFinalFragmentTip"] = "بخش پایانی بسته‌ها را به قطعه‌های کوچک‌تر تقسیم می‌کند؛ ممکن است سرعت انتقال و تأخیر تغییر کند.",
            ["TbEnabletDnsViaProxy"] = "ارسال DNS از طریق پل ارتباطی",
            ["MsgInsecureConfiguration"] = "پیکربندی ناامن شناسایی شد: گزینهٔ AllowInsecure فعال است اما گواهی‌ای ارائه نشده است؛ این وضعیت می‌تواند حملهٔ مرد میانی را ممکن کند.",
            ["TbHy2RealmUrl"] = "نشانی Realm",
            ["InvalidHy2RealmUrl"] = "نشانی Realm معتبر نیست.",
            ["InvalidHttpOutboundHeaders"] = "لطفاً JSON معتبر برای سرآیندهای درخواست HTTP وارد کنید.",
            ["TbHy2RealmUrlTip"] = "الگو: realm://<token>@<rendezvous-host>[:port]/<realm-name>?stun=<stun-host>[:port]",
            ["TbGeckoPacketSize"] = "اندازهٔ بستهٔ Gecko (کمینه/بیشینه)",
            ["TbLegacyProtectTip"] = "در صورت فعال‌بودن، از TUN در sing-box استفاده می‌شود؛ در غیر این صورت TUN در Xray به‌کار می‌رود.",
            ["TbRootCertificateProvider"] = "ارائه‌دهندهٔ گواهی ریشه",
            ["TbRootCertificateProviderTip"] = "فقط بر دانلودها و درخواست‌های شبکهٔ رابط گرافیکی v2rayN اثر دارد و اعتبارسنجی گواهی در هسته را تغییر نمی‌دهد.",
            ["TbProxyDialResolveStrategy"] = "روش تبدیل نام به IP هنگام اتصال پراکسی",
            ["TbProxyDialResolveStrategyTip"] = "پیشنهاد نمی‌شود؛ ممکن است باعث حلقهٔ مسیریابی شود.",
            ["TbEnableHappyEyeballs"] = "فعال‌کردن Happy Eyeballs",
            ["TbEnableHappyEyeballsTip"] = "به روش UseIP نیاز دارد. تلاش IPv4 و IPv6 را هم‌زمان آغاز می‌کند و مسیر در دسترس سریع‌تر را انتخاب می‌کند.",
            ["TbIpv6Address"] = "نشانی IPv6",
            ["TbIpv4Address"] = "نشانی IPv4",
            ["menuAddCustomOutboundServer"] = "افزودن خروجی سفارشی",
            ["MsgCustomOutboundFileNotFound"] = "فایل خروجی سفارشی {0} پیدا نشد: {1}",
            ["TbCustomOutboundTip"] = "برای Xray و sing-box فقط یک خروجی یا endpoint پشتیبانی می‌شود.",
            ["LvCustomCoreType"] = "هستهٔ پیکربندی سفارشی",
            ["TbXrayOnly"] = "فقط Xray",
            ["TbBlockAAAAQueries"] = "مسدودکردن درخواست‌های AAAA",
            ["TbBlockAAAAQueriesTips"] = "در صورت فعال‌بودن، درخواست‌های IPv6 مسدود می‌شوند.",
            ["TbDNS"] = "DNS",
            ["TbSettingsMux4Ray"] = "تنظیمات Xray Mux",
            ["LvRequestHeaders"] = "سرآیندهای HTTP (JSON)",
            ["SubRequestHeadersTips"] = "اختیاری. نمونه: {\"X-hwid\":\"my_device\"}. این مقادیر برای همهٔ نشانی‌های این گروه، از جمله سرویس تبدیل اشتراک، جایگزین سرآیندهای پیش‌فرض می‌شوند.",
            ["SubRequestHeadersInvalid"] = "سرآیندهای درخواست معتبر نیستند. یک شیء JSON با نام‌های یکتا و مقدارهای رشته‌ای وارد کنید؛ نام و مقدار سرآیند نباید شکست خط داشته باشد.",
            ["TbSortingProcess"] = "فرایند مرتب‌سازی",
            ["menuAddMasqueServer"] = "افزودن سرور [MASQUE]"
        };

    public static IReadOnlyDictionary<string, string> MissingTranslations => MissingPersianTranslations;

    public static string GetString(string key, string? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return MissingPersianTranslations.TryGetValue(key, out var translation)
            ? translation
            : fallback ?? key;
    }

    public static FlowDirection GetFlowDirection(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    public static void ApplyFlowDirection(FrameworkElement element, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.FlowDirection = GetFlowDirection(culture);
    }
}
