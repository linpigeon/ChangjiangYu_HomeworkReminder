using System;
using HomeworkReminder.Services;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 通知链路自检（<c>--toastcheck</c>）。
/// <para>
/// 诊断「为什么没弹通知」的四个环节，逐项报告，失败不抛异常：
/// </para>
/// <list type="number">
/// <item>AUMID 是否注册成功（HKCU，无需管理员）</item>
/// <item>开始菜单快捷方式是否就位（通知平台据此认定「已安装应用」）</item>
/// <item>toast XML 是否生成正确（转义、字段）</item>
/// <item>WinRT 投递是否成功；失败时给出具体 HRESULT 与阶段</item>
/// </list>
/// <para>
/// 退出码：0 = 链路可用；非 0 = 失败（原因打印到控制台）。
/// </para>
/// </summary>
internal static class ToastCheck
{
    public static int Run()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (System.IO.IOException)
        {
            // WinExe 无控制台时给 OutputEncoding 赋值会抛；没控制台就不设编码。
        }

        Console.WriteLine("=== 通知链路自检 ===");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("非 Windows 平台，系统通知不可用。");
            return 2;
        }

        // 1) + 2) 由 WindowsNotifier 的构造完成
        var notifier = new WindowsNotifier();
        Console.WriteLine($"[1] 通知可用性 : {(notifier.IsAvailable ? "可用" : "不可用")}");
        if (notifier.UnavailableReason is { } reason)
            Console.WriteLine($"    原因       : {reason}");

        var shortcut = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs\作业提醒.lnk");
        Console.WriteLine($"[2] 开始菜单快捷方式 : {(System.IO.File.Exists(shortcut) ? "已就位" : "未创建")}");
        Console.WriteLine($"    {shortcut}");

        // 3) toast XML 生成（含转义）
        var xml = WinRtToast.BuildToastXml(
            "信号与系统",
            "第1章作业 & <测试>",
            "截止 09-20 23:59（剩 1.2 天）",
            "https://changjiang.yuketang.cn/v2/web/studentLog/26111830");
        Console.WriteLine($"[3] toast XML : {xml.Length} 字符");
        Console.WriteLine($"    {xml}");
        var escaped = xml.Contains("&amp;") && xml.Contains("&lt;");
        Console.WriteLine($"    转义正确   : {(escaped ? "是" : "否 —— 标题含 & 或 < 时会发不出去")}");

        // 4) 实际投递
        var result = WinRtToast.TryShow("HomeworkReminder.Desktop", xml);
        Console.WriteLine($"[4] WinRT 投递 : {(result.Ok ? "成功" : "失败")}");
        if (!result.Ok) Console.WriteLine($"    {result.Describe()}");

        Console.WriteLine();
        if (result.Ok && escaped)
        {
            Console.WriteLine("结论：通知链路可用。");
            return 0;
        }

        if (result.HResult == unchecked((int)0x80070490))
        {
            // 连系统自带 AUMID 也报这个错误码时，说明是环境没有通知平台，
            // 而不是我们的配置有问题（实测沙箱与无交互桌面的会话都是这样）。
            Console.WriteLine("结论：0x80070490 = 找不到元素。当前环境没有可用的通知平台");
            Console.WriteLine("      （会话隔离 / 无交互式桌面 / 通知服务未运行），与 AUMID 配置无关。");
            Console.WriteLine("      请在正常登录的桌面会话中重跑本自检。");
            return 3;
        }

        Console.WriteLine("结论：通知链路不可用，请按上面失败的那一步排查。");
        return 1;
    }
}
