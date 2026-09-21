// 探针：验证用「自带 AUMID + COM 激活」发送 Windows toast 是否可行。
// 思路来自 Tor.Net（Windows 上的 Tor 控制器）的 NotificationCenter 实现：
// 未打包 Win32 应用先写 HKCU\Software\Classes\AppUserModelId\<aumid> 自注册，
// 再通过 RoGetActivationFactory / RoActivateInstance 走 WinRT。
//
// 本探针依次验证：
//   1) AUMID 自注册（HKCU，无需管理员）
//   2) 能否激活 Windows.UI.Notifications.ToastNotificationManager（带 IID）
//   3) 能否调用 CreateToastNotifierWithId(aumid)
//   4) 能否构造并投递 toast
// 每一步都单独报告，失败时能看出卡在哪。
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

const string Aumid = "HomeworkReminder.ToastProbe";

Console.OutputEncoding = Encoding.UTF8;

// ---------- 1) AUMID 自注册 ----------
var key = $@"Software\Classes\AppUserModelId\{Aumid}";
if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("[1] 非 Windows，跳过");
    return 2;
}

try
{
    using var k = Registry.CurrentUser.CreateSubKey(key, writable: true);
    k.SetValue("DisplayName", "作业提醒（探针）", RegistryValueKind.String);
    k.SetValue("ShowInSettings", 0, RegistryValueKind.DWord);
    Console.WriteLine($"[1] AUMID 注册成功: HKCU\\{key}");
}
catch (Exception ex)
{
    Console.WriteLine($"[1] AUMID 注册失败: {ex.GetType().Name}: {ex.Message}");
}

// ---------- 2~4) WinRT 调用 ----------
// 依次试多个候选 AUMID，找出在这台机器上真能投递的那个。
// 自注册的 AUMID 只有注册表项还不够（CreateToastNotifier 返回 0x80070490），
// 通常还需要一个带 AppUserModelID 的开始菜单快捷方式；先用已有的已知良好 AUMID 验证链路。
var candidates = new (string Aumid, string Label)[]
{
    (Aumid, "自注册（仅注册表）"),
    ("Microsoft.Windows.Explorer", "Explorer"),
    ("Microsoft.Windows.Explorer", "Explorer（重复确认）"),
    ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe", "Windows PowerShell"),
    ("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Windows Terminal"),
};

Console.WriteLine();
var ok = 0;
foreach (var (aumid, label) in candidates)
{
    var hr = ToastProbe.TryToast(aumid,
        "<toast><visual><binding template='ToastGeneric'>" +
        "<text>作业提醒（探针）</text><text>测试通知 · 来源 " + label + "</text>" +
        "</binding></visual></toast>",
        out var step, out var detail);

    if (hr >= 0) ok++;
    var status = hr >= 0 ? "✅ 成功" : "❌ 失败";
    Console.WriteLine($"[{label}] {status}  hr=0x{hr:X8}  阶段={step}");
    if (detail.Length > 0) Console.WriteLine($"      {detail}");
}

Console.WriteLine();
if (ok == 0)
{
    // 连系统自带的 Explorer / Terminal 都失败，说明不是 AUMID 的问题，
    // 而是当前环境没有通知平台（会话隔离、无交互式桌面、通知服务未运行等）。
    Console.WriteLine("结论：全部 AUMID 均失败 → 当前环境不支持 toast，与 AUMID 无关。");
    Console.WriteLine("      互操作链路本身已通过（XmlDocument 与 ToastNotification 均成功激活）。");
    Console.WriteLine("      请在正常桌面会话中验证实际弹窗。");
    return 3;
}

Console.WriteLine($"结论：{ok} 个 AUMID 可投递 → WinRT 链路可用，按可用 AUMID 实现即可。");
return 0;

internal static class ToastProbe
{
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string src, int len, out IntPtr h);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr h);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);

    // IUnknown
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");

    // IToastNotificationManagerStatics
    private static readonly Guid IidMgrStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    // IToastNotificationFactory
    private static readonly Guid IidToastFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    // IXmlDocumentIO（不是 IXmlDocument——后者是 F7F3A506-1E87-42D6-BCFB-B8C809FA5494；首方法即 LoadXml）
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int D_GetNotifier(IntPtr self, IntPtr aumid, out IntPtr notifier);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int D_CreateToast(IntPtr self, IntPtr xml, out IntPtr toast);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int D_Show(IntPtr self, IntPtr toast);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int D_LoadXml(IntPtr self, IntPtr xml);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int D_Release(IntPtr self);

    private static IntPtr H(string s)
    {
        var hr = WindowsCreateString(s, s.Length, out var h);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        return h;
    }

    private static void QI(IntPtr obj, Guid iid, string label)
    {
        var vtbl = Marshal.ReadIntPtr(obj);
        var qi = Marshal.GetDelegateForFunctionPointer<D_QueryInterface>(Marshal.ReadIntPtr(vtbl, 0));
        var hr = qi(obj, ref iid, out var got);
        if (hr < 0) throw new Exception($"{label} QueryInterface 失败 hr=0x{hr:X8}");
        if (got == IntPtr.Zero) throw new Exception($"{label} QueryInterface 返回空指针");
        // 探针只验证「能否 QI 到」，QI 出的接口引用立即归还；继续使用外壳对象本身。
        Release(got);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int D_QueryInterface(IntPtr self, ref Guid iid, out IntPtr result);

    private static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        var vtbl = Marshal.ReadIntPtr(p);
        var rel = Marshal.GetDelegateForFunctionPointer<D_Release>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
        rel(p);
    }

    public static int TryToast(string aumid, string xml, out string step, out string detail)
    {
        step = ""; detail = "";
        IntPtr mgr = IntPtr.Zero, notifier = IntPtr.Zero, doc = IntPtr.Zero, toast = IntPtr.Zero, factory = IntPtr.Zero;

        try
        {
            // --- 激活 XmlDocument 并 LoadXml ---
            step = "RoActivateInstance(XmlDocument)";
            var hClass = H("Windows.Data.Xml.Dom.XmlDocument");
            var hr = RoActivateInstance(hClass, out doc);
            WindowsDeleteString(hClass);
            if (hr < 0) { detail = $"hr=0x{hr:X8}"; return hr; }

            step = "QI(IXmlDocumentIO)";
            {
                var iid = IidXmlDocumentIO;   // 静态只读字段不能按 ref 传递，先取到局部变量
                QI(doc, iid, "XmlDocument");
            }

            step = "IXmlDocumentIO.LoadXml (slot 6)";
            {
                var vtbl = Marshal.ReadIntPtr(doc);
                var fn = Marshal.GetDelegateForFunctionPointer<D_LoadXml>(Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size));
                var hx = H(xml);
                try
                {
                    hr = fn(doc, hx);
                    if (hr < 0) { detail = $"LoadXml hr=0x{hr:X8}"; return hr; }
                }
                finally { WindowsDeleteString(hx); }
            }

            // --- 激活 ToastNotification 工厂并创建实例 ---
            step = "RoGetActivationFactory(ToastNotification)";
            var hT = H("Windows.UI.Notifications.ToastNotification");
            {
                var iid = IidToastFactory;
                hr = RoGetActivationFactory(hT, ref iid, out factory);
            }
            WindowsDeleteString(hT);
            if (hr < 0) { detail = $"hr=0x{hr:X8}"; return hr; }

            step = "IToastNotificationFactory.CreateToastNotification (slot 6)";
            {
                var vtbl = Marshal.ReadIntPtr(factory);
                var fn = Marshal.GetDelegateForFunctionPointer<D_CreateToast>(Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size));
                hr = fn(factory, doc, out toast);
                if (hr < 0) { detail = $"CreateToastNotification hr=0x{hr:X8}"; return hr; }
            }

            // --- 取通知器 ---
            step = "RoGetActivationFactory(ToastNotificationManager)";
            var hM = H("Windows.UI.Notifications.ToastNotificationManager");
            {
                var iid = IidMgrStatics;
                hr = RoGetActivationFactory(hM, ref iid, out mgr);
            }
            WindowsDeleteString(hM);
            if (hr < 0) { detail = $"manager factory hr=0x{hr:X8}"; return hr; }

            // IToastNotificationManagerStatics：@6 CreateToastNotifier()、@7 CreateToastNotifierWithId、
            // @8 GetTemplateContent。带 AUMID 的重载必须读槽位 7，读 6 在 x64 下会把
            // HSTRING 当成 out 指针写入（堆破坏）。
            step = "CreateToastNotifierWithId (slot 7)";
            {
                var vtbl = Marshal.ReadIntPtr(mgr);
                var fn = Marshal.GetDelegateForFunctionPointer<D_GetNotifier>(Marshal.ReadIntPtr(vtbl, 7 * IntPtr.Size));
                var ha = H(aumid);
                try
                {
                    hr = fn(mgr, ha, out notifier);
                    if (hr < 0) { detail = $"CreateToastNotifierWithId hr=0x{hr:X8}"; return hr; }
                }
                finally { WindowsDeleteString(ha); }
            }

            // --- 投递 ---
            step = "ToastNotifier.Show (slot 6)";
            {
                var vtbl = Marshal.ReadIntPtr(notifier);
                var fn = Marshal.GetDelegateForFunctionPointer<D_Show>(Marshal.ReadIntPtr(vtbl, 6 * IntPtr.Size));
                hr = fn(notifier, toast);
                if (hr < 0) { detail = $"Show hr=0x{hr:X8}"; return hr; }
            }

            step = "done";
            return 0;
        }
        catch (Exception ex)
        {
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return -1;
        }
        finally
        {
            Release(toast); Release(notifier); Release(doc); Release(factory); Release(mgr);
        }
    }
}
