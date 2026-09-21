using System;
using System.Runtime.InteropServices;

namespace HomeworkReminder.Services;

/// <summary>
/// 未打包（unpackaged）Win32 应用发送 Windows toast 通知所需的 WinRT COM 互操作。
/// <para>
/// <b>为什么手写</b>：仓库的离线包源（<c>packages/</c>）与机器缓存里没有任何通知相关
/// NuGet 包（<c>CommunityToolkit.WinUI.Notifications</c> / <c>Microsoft.Toolkit.Uwp.Notifications</c>
/// 都取不到，restore 会 NU1101），而 <c>Windows.UI.Notifications</c> 是系统自带的 WinRT 类型，
/// 走 COM 互操作无需额外依赖——与 <see cref="Dpapi"/> 同一取舍。
/// </para>
/// <para>
/// <b>ABI 依据</b>：WinRT 的 vtable 顺序由 MIDL 声明顺序决定，是稳定契约。
/// 这里用到的方法大都在 <c>IInspectable</c>（6 个槽位）后的第一个槽位（@6），
/// 唯一的例外是 <c>CreateToastNotifierWithId</c>（@7）：
/// </para>
/// <list type="bullet">
/// <item><c>Windows.Data.Xml.Dom.XmlDocument</c> —— <c>IXmlDocumentIO.LoadXml</c> @6</item>
/// <item><c>IToastNotificationFactory</c> —— <c>CreateToastNotification</c> @6</item>
/// <item><c>IToastNotificationManagerStatics</c> —— @6 是无参 <c>CreateToastNotifier()</c>，
///   带 AUMID 的 <c>CreateToastNotifierWithId</c> 在 @7（@8 是 <c>GetTemplateContent</c>）。
///   未打包进程必须走带 AUMID 的重载；调错槽位在 x64 下会把 HSTRING 当成 out 指针写入，
///   造成堆破坏。</item>
/// <item><c>IToastNotifier</c> —— <c>Show</c> @6</item>
/// </list>
/// <para>
/// 这四步已在 <c>tools/ToastProbe</c> 中逐步实测：前三步（XmlDocument 激活、
/// ToastNotification 创建、通知器工厂激活）均成功。
/// </para>
/// <para>
/// 只在 Windows 上可用；调用方必须先用 <see cref="OperatingSystem.IsWindows"/> 守卫。
/// </para>
/// </summary>
public static class WinRtToast
{
    // ---- combase.dll：HSTRING 与 WinRT 激活 ----

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out int length);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();

    // ---- IInspectable 之后的第一个方法统一在槽位 6；CreateToastNotifierWithId 例外 ----
    private const int SlotFirstMethod = 6;

    // IToastNotificationManagerStatics：@6 CreateToastNotifier()、@7 CreateToastNotifierWithId(aumid)、
    // @8 GetTemplateContent。未打包进程只能用带 AUMID 的重载。
    private const int SlotCreateToastNotifierWithId = 7;

    // RoInitialize 的套间类型：RO_INIT_MULTITHREADED = 1。
    // TryShow 可能跑在线程池线程上（自动同步路径），线程未必初始化过 COM。
    private const int RoInitMultithreaded = 1;

    // 6CD0E74E… 是 IXmlDocumentIO 的 IID（首方法即 LoadXml），不是 IXmlDocument——
    // 后者是 F7F3A506-1E87-42D6-BCFB-B8C809FA5494。勿按字面「修正」回 IXmlDocument。
    private static readonly Guid IidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid IidToastNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid IidToastNotificationManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LoadXmlDelegate(IntPtr self, IntPtr xml);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateToastDelegate(IntPtr self, IntPtr xmlDocument, out IntPtr toast);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateNotifierDelegate(IntPtr self, IntPtr applicationId, out IntPtr notifier);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ShowDelegate(IntPtr self, IntPtr notification);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int QueryInterfaceDelegate(IntPtr self, ref Guid iid, out IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int ReleaseDelegate(IntPtr self);

    /// <summary>投递结果，便于诊断「为什么没弹」。</summary>
    /// <param name="Ok">整条链路是否成功。</param>
    /// <param name="Step">失败时处于哪一步。</param>
    /// <param name="HResult">失败时的 HRESULT；成功为 0。</param>
    public readonly record struct ToastResult(bool Ok, string Step, int HResult)
    {
        public string Describe() => Ok
            ? "已投递"
            : $"{Step} 失败（HRESULT=0x{HResult:X8}）";
    }

    /// <summary>
    /// 投递一条 toast。<b>不抛异常</b>——通知失败不应影响同步。
    /// </summary>
    /// <param name="aumid">已在注册表中登记过的 AppUserModelId。</param>
    /// <param name="toastXml">完整 toast XML，见 <see cref="BuildToastXml"/>。</param>
    public static ToastResult TryShow(string aumid, string toastXml)
    {
        if (!OperatingSystem.IsWindows())
            return new ToastResult(false, "非 Windows 平台", 0);

        // 自动同步路径会在线程池线程上调用本方法，线程可能从未初始化 COM。
        // S_FALSE（已初始化）视为成功；只有本次新初始化（S_OK）才负责配对卸载，
        // 免得把线程池线程永久留在 MTA 里影响后续复用它的代码。
        var initHr = RoInitialize(RoInitMultithreaded);
        if (initHr < 0) return new ToastResult(false, "初始化 COM（RoInitialize）", initHr);
        var ownApartment = initHr == 0;

        IntPtr xmlDoc = IntPtr.Zero, toastFactory = IntPtr.Zero, toast = IntPtr.Zero;
        IntPtr mgrFactory = IntPtr.Zero, notifier = IntPtr.Zero;
        var step = "初始化";

        try
        {
            // 1) XmlDocument 实例 + IXmlDocumentIO.LoadXml
            step = "激活 XmlDocument";
            var hClass = NewHString("Windows.Data.Xml.Dom.XmlDocument");
            var hr = RoActivateInstance(hClass, out xmlDoc);
            DeleteHString(hClass);
            if (hr < 0) return new ToastResult(false, step, hr);

            step = "查询 IXmlDocumentIO";
            var iidXml = IidXmlDocumentIO;
            hr = QueryInterface(xmlDoc, ref iidXml, out var xmlTyped);
            if (hr < 0) return new ToastResult(false, step, hr);
            Release(xmlDoc);
            xmlDoc = xmlTyped;

            step = "LoadXml";
            hr = InvokeLoadXml(xmlDoc, toastXml);
            if (hr < 0) return new ToastResult(false, step, hr);

            // 2) ToastNotification 工厂 + 实例
            step = "激活 ToastNotification 工厂";
            var hToast = NewHString("Windows.UI.Notifications.ToastNotification");
            var iidToastFactory = IidToastNotificationFactory;
            hr = RoGetActivationFactory(hToast, ref iidToastFactory, out toastFactory);
            DeleteHString(hToast);
            if (hr < 0) return new ToastResult(false, step, hr);

            step = "创建 ToastNotification";
            hr = InvokeCreateToast(toastFactory, xmlDoc, out toast);
            if (hr < 0) return new ToastResult(false, step, hr);

            // 3) 通知器工厂 + 实例
            step = "激活 ToastNotificationManager";
            var hMgr = NewHString("Windows.UI.Notifications.ToastNotificationManager");
            var iidMgr = IidToastNotificationManagerStatics;
            hr = RoGetActivationFactory(hMgr, ref iidMgr, out mgrFactory);
            DeleteHString(hMgr);
            if (hr < 0) return new ToastResult(false, step, hr);

            step = "创建 ToastNotifier";
            hr = InvokeCreateNotifier(mgrFactory, aumid, out notifier);
            // 0x80070490（找不到元素）通常意味着当前环境没有通知平台，
            // 或该 AUMID 未与开始菜单快捷方式关联。原样上报 HRESULT 便于定位。
            if (hr < 0) return new ToastResult(false, step, hr);

            // 4) 投递
            step = "Show";
            hr = InvokeShow(notifier, toast);
            if (hr < 0) return new ToastResult(false, step, hr);

            return new ToastResult(true, "done", 0);
        }
        catch (Exception ex)
        {
            // DllNotFound / EntryPointNotFound 等：视为环境不支持。
            return new ToastResult(false, $"{step}（{ex.GetType().Name}）", ex.HResult);
        }
        finally
        {
            Release(notifier);
            Release(mgrFactory);
            Release(toast);
            Release(toastFactory);
            Release(xmlDoc);
            if (ownApartment) RoUninitialize();
        }
    }

    /// <summary>
    /// 构造 toast XML。
    /// <para>
    /// 参数顺序与 Windows 的「通知」语义一致：<c>text</c> 第一条是粗体标题，其余为正文。
    /// 全部内容都做 XML 转义——作业标题里出现 <c>&amp;</c>、<c>&lt;</c> 时不至于让整条通知失败。
    /// </para>
    /// </summary>
    public static string BuildToastXml(
        string title,
        string body,
        string? attribution = null,
        string? launchUrl = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<toast");
        if (!string.IsNullOrWhiteSpace(launchUrl))
        {
            // activationType=protocol：点击通知直接调起浏览器打开原页面。
            sb.Append(" activationType=\"protocol\"");
            sb.Append(" launch=\"").Append(Escape(launchUrl)).Append('"');
        }
        sb.Append("><visual><binding template=\"ToastGeneric\">");
        sb.Append("<text>").Append(Escape(title)).Append("</text>");
        sb.Append("<text>").Append(Escape(body)).Append("</text>");
        if (!string.IsNullOrWhiteSpace(attribution))
            sb.Append("<text placement=\"attribution\">").Append(Escape(attribution)).Append("</text>");
        sb.Append("</binding></visual></toast>");
        return sb.ToString();
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    // ---------------- 底层调用 ----------------

    private static IntPtr NewHString(string s)
    {
        var hr = WindowsCreateString(s, s.Length, out var h);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        return h;
    }

    private static void DeleteHString(IntPtr h)
    {
        if (h != IntPtr.Zero) WindowsDeleteString(h);
    }

    private static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (obj == IntPtr.Zero) return unchecked((int)0x80004003); // E_POINTER
        var vtbl = Marshal.ReadIntPtr(obj);
        var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(Marshal.ReadIntPtr(vtbl, 0));
        return qi(obj, ref iid, out result);
    }

    private static void Release(IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        var vtbl = Marshal.ReadIntPtr(p);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
        release(p);
    }

    private static int InvokeLoadXml(IntPtr xmlDoc, string xml)
    {
        var vtbl = Marshal.ReadIntPtr(xmlDoc);
        var fn = Marshal.GetDelegateForFunctionPointer<LoadXmlDelegate>(
            Marshal.ReadIntPtr(vtbl, SlotFirstMethod * IntPtr.Size));
        var h = NewHString(xml);
        try
        {
            return fn(xmlDoc, h);
        }
        finally
        {
            DeleteHString(h);
        }
    }

    private static int InvokeCreateToast(IntPtr factory, IntPtr xmlDoc, out IntPtr toast)
    {
        var vtbl = Marshal.ReadIntPtr(factory);
        var fn = Marshal.GetDelegateForFunctionPointer<CreateToastDelegate>(
            Marshal.ReadIntPtr(vtbl, SlotFirstMethod * IntPtr.Size));
        return fn(factory, xmlDoc, out toast);
    }

    private static int InvokeCreateNotifier(IntPtr factory, string aumid, out IntPtr notifier)
    {
        var vtbl = Marshal.ReadIntPtr(factory);
        var fn = Marshal.GetDelegateForFunctionPointer<CreateNotifierDelegate>(
            Marshal.ReadIntPtr(vtbl, SlotCreateToastNotifierWithId * IntPtr.Size));
        var h = NewHString(aumid);
        try
        {
            return fn(factory, h, out notifier);
        }
        finally
        {
            DeleteHString(h);
        }
    }

    private static int InvokeShow(IntPtr notifier, IntPtr toast)
    {
        var vtbl = Marshal.ReadIntPtr(notifier);
        var fn = Marshal.GetDelegateForFunctionPointer<ShowDelegate>(
            Marshal.ReadIntPtr(vtbl, SlotFirstMethod * IntPtr.Size));
        return fn(notifier, toast);
    }
}
