using System.ComponentModel;

namespace Beam.Core;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    static readonly Dictionary<string, (string En, string Ar)> Map = new()
    {
        ["Tagline"] = ("Share a local Angular project through a public link", "شارك مشروع Angular من جهازك برابط عام"),
        ["Project"] = ("Project", "المشروع"),
        ["PickPrompt"] = ("Choose the folder that contains angular.json", "اختار الفولدر اللي فيه angular.json"),
        ["Browse"] = ("Browse", "اختيار"),
        ["Recent"] = ("Recent", "الأخيرة"),
        ["NoProject"] = ("No project selected", "مفيش مشروع متختار"),
        ["NotAngular"] = ("This folder has no angular.json", "الفولدر ده مفيهوش angular.json"),
        ["Angular"] = ("Angular", "Angular"),
        ["Port"] = ("Port", "البورت"),
        ["HostsOk"] = ("Tunnel hosts allowed", "روابط النفق مسموحة"),
        ["HostsFix"] = ("Host check will be fixed", "هيتم إصلاح حظر الرابط"),
        ["NoModules"] = ("Packages will be installed first", "هيتم تثبيت الحزم الأول"),
        ["GoLive"] = ("Go live", "انشر المشروع"),
        ["Stop"] = ("Stop", "إيقاف"),
        ["Starting"] = ("Starting…", "جاري التشغيل…"),
        ["Stopping"] = ("Stopping…", "جاري الإيقاف…"),
        ["StepCloudflared"] = ("Cloudflare tunnel tool", "أداة نفق Cloudflare"),
        ["StepHosts"] = ("Angular host check", "حظر الرابط في Angular"),
        ["StepServer"] = ("Development server", "سيرفر التطوير"),
        ["StepTunnel"] = ("Public link", "الرابط العام"),
        ["Downloading"] = ("Downloading cloudflared {0}%", "تحميل cloudflared ‏{0}%"),
        ["Installing"] = ("Installing packages", "تثبيت الحزم"),
        ["Building"] = ("Building on port {0}", "بناء المشروع على بورت {0}"),
        ["Connecting"] = ("Connecting to Cloudflare", "الاتصال بـ Cloudflare"),
        ["Ready"] = ("Ready", "جاهز"),
        ["Patched"] = ("New angular.json written, original kept as angular_original.json", "اتكتب angular.json جديد والأصلي اتحفظ باسم angular_original.json"),
        ["AlreadyOk"] = ("Already allowed", "مسموح بالفعل"),
        ["Live"] = ("Live", "شغال"),
        ["Idle"] = ("Idle", "متوقف"),
        ["YourLink"] = ("Your public link", "الرابط العام"),
        ["LinkHint"] = ("Anyone with this link can open the project while Beam is running", "أي حد معاه الرابط يقدر يفتح المشروع طول ما Beam شغال"),
        ["Copy"] = ("Copy", "نسخ"),
        ["Copied"] = ("Copied", "اتنسخ"),
        ["Open"] = ("Open", "فتح"),
        ["Logs"] = ("Activity", "السجل"),
        ["ClearLogs"] = ("Clear", "مسح"),
        ["Failed"] = ("Something went wrong", "حصلت مشكلة"),
        ["ServerExited"] = ("The development server stopped. Check the activity log.", "سيرفر التطوير وقف. راجع السجل."),
        ["TunnelTimeout"] = ("Cloudflare did not return a link in time.", "Cloudflare مرجعش رابط في الوقت المحدد."),
        ["ServerTimeout"] = ("The development server did not start in time.", "سيرفر التطوير مااشتغلش في الوقت المحدد."),
        ["NpmFailed"] = ("Package installation failed.", "تثبيت الحزم فشل."),
        ["TrayOpen"] = ("Open Beam", "فتح Beam"),
        ["TrayCopy"] = ("Copy link", "نسخ الرابط"),
        ["TrayStop"] = ("Stop sharing", "إيقاف المشاركة"),
        ["TrayExit"] = ("Exit", "خروج"),
        ["StillRunning"] = ("Beam is still sharing in the tray", "Beam لسه شغال في التراي"),
        ["Elapsed"] = ("Live for {0}", "شغال من {0}"),
        ["AddProject"] = ("Add project", "مشروع تاني"),
        ["NewProject"] = ("New project", "مشروع جديد"),
        ["LiveCount"] = ("{0} live", "{0} شغالين"),
        ["StopAll"] = ("Stop all", "إيقاف الكل"),
        ["Remove"] = ("Remove from list", "شيله من القايمة"),
        ["Projects"] = ("Projects", "المشاريع"),
        ["BrandSub"] = ("Local to public in one click", "من جهازك للعالم بضغطة"),
        ["AllIdle"] = ("All idle", "الكل متوقف"),
        ["SubIdle"] = ("Idle · port {0}", "متوقف · بورت {0}"),
        ["SubLive"] = ("Live · port {0}", "شغال · بورت {0}"),
        ["SubEmpty"] = ("No folder yet", "مفيش فولدر لسه"),
        ["SubNotAngular"] = ("Not an Angular project", "مش مشروع Angular"),
        ["EmptyTitle"] = ("Share a project with anyone", "شارك مشروعك مع أي حد"),
        ["EmptyBody"] = ("Pick an Angular project folder. Beam fixes the host check, runs the dev server and hands you a public HTTPS link.", "اختار فولدر مشروع Angular وBeam هيصلّح حظر الرابط ويشغّل السيرفر ويديك رابط HTTPS عام."),
        ["ChooseFolder"] = ("Choose project folder", "اختار فولدر المشروع"),
        ["Change"] = ("Change", "تغيير"),
        ["OpenFolder"] = ("Open folder", "فتح الفولدر"),
        ["HeroIdleTitle"] = ("Your public link will appear here", "الرابط العام هيظهر هنا"),
        ["HeroIdleBody"] = ("Press Go live and Beam runs the project and opens a secure tunnel to it.", "دوس انشر المشروع وBeam هيشغّله ويفتحله نفق آمن."),
        ["HeroStartingTitle"] = ("Getting your link ready", "بنجهّز الرابط"),
        ["HeroStartingBody"] = ("This usually takes less than a minute.", "غالباً بياخد أقل من دقيقة."),
        ["CopyLog"] = ("Copy activity", "نسخ السجل"),
        ["LogEmpty"] = ("Activity shows up here once the project starts", "السجل هيظهر هنا لما المشروع يشتغل"),
    };

    public bool Arabic { get; private set; }

    public string this[string key] => Map.TryGetValue(key, out var v) ? (Arabic ? v.Ar : v.En) : key;

    public string F(string key, params object[] args) => string.Format(this[key], args);

    public void SetArabic(bool arabic)
    {
        Arabic = arabic;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Arabic)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
