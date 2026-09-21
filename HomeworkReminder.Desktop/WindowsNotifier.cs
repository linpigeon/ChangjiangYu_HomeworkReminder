using System;
using System.IO;
using System.Runtime.Versioning;
using HomeworkReminder.Services;
using Microsoft.Win32;

namespace HomeworkReminder.Desktop;

/// <summary>
/// Windows 版通知实现：自注册 AppUserModelId + 写开始菜单快捷方式 + WinRT toast。
/// <para>
/// <b>为什么需要自注册</b>：未打包（unpackaged）的 Win32 应用没有包标识，
/// <c>ToastNotificationManager.CreateToastNotifier(aumid)</c> 要求该 AUMID
/// 对应一个「已安装的应用」——只写注册表项不够。
/// </para>
/// <para>
/// 实测（<c>tools/ToastProbe</c>）：仅注册 <c>HKCU\Software\Classes\AppUserModelId\&lt;aumid&gt;</c>
/// 时 <c>CreateToastNotifier</c> 返回 <c>0x80070490</c>（找不到元素）；
/// 同时还需要一个带 <c>System.AppUserModel.ID</c> 属性的开始菜单快捷方式，
/// 这也是 Tor.Net 等项目采用的做法。
/// </para>
/// <para>
/// 全部写入都在 HKCU 与用户自己的开始菜单目录，<b>不需要管理员权限</b>。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNotifier : INotifier
{
    /// <summary>
    /// 自注册的 AppUserModelId。命名遵循「公司.产品」惯例，
    /// 这样通知来源显示为「作业提醒」而不是「Windows PowerShell」。
    /// </summary>
    private const string Aumid = "HomeworkReminder.Desktop";

    private const string DisplayName = "作业提醒";

    private bool _registered;
    private string? _unavailableReason;

    public WindowsNotifier()
    {
        if (!OperatingSystem.IsWindows())
        {
            _unavailableReason = "当前平台不支持系统通知";
            return;
        }

        try
        {
            RegisterAumid();
            EnsureStartMenuShortcut();
            _registered = true;
        }
        catch (Exception ex)
        {
            _unavailableReason = $"通知初始化失败：{ex.GetType().Name}: {ex.Message}";
        }
    }

    public bool IsAvailable => _registered && _unavailableReason is null;

    public string? UnavailableReason => _unavailableReason;

    public string? Show(AppNotification notification)
    {
        if (!IsAvailable) return _unavailableReason ?? "通知不可用";

        var xml = WinRtToast.BuildToastXml(
            notification.Title,
            notification.Body,
            notification.Attribution,
            notification.LaunchUrl);

        var result = WinRtToast.TryShow(Aumid, xml);
        return result.Ok ? null : result.Describe();
    }

    /// <summary>
    /// 注册 AUMID。已存在且 DisplayName 一致时跳过——避免每次启动都写注册表。
    /// </summary>
    private static void RegisterAumid()
    {
        var subKey = $@"Software\Classes\AppUserModelId\{Aumid}";

        using var existing = Registry.CurrentUser.OpenSubKey(subKey);
        if (existing?.GetValue("DisplayName") as string == DisplayName) return;

        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
                        ?? throw new InvalidOperationException("无法创建 AUMID 注册表项");
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        // 有意取舍：ShowInSettings=0 让本应用不出现在「设置 → 通知」列表里，
        // 减少对用户的干扰；代价是用户无法在系统设置里单独关闭本应用的通知，
        // 只能在应用内（或整体通知开关）管理。
        key.SetValue("ShowInSettings", 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// 在开始菜单创建一个指向本程序的快捷方式，并设置 <c>System.AppUserModel.ID</c>。
    /// 这个属性是通知平台认定「该 AUMID 属于一个已安装应用」的依据。
    /// </summary>
    private static void EnsureStartMenuShortcut()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");
        if (!Directory.Exists(startMenu)) return;

        var linkPath = Path.Combine(startMenu, $"{DisplayName}.lnk");

        // 已存在且指向同一个可执行文件时无需重建。
        if (File.Exists(linkPath) && ShortcutTargets(linkPath, exe)) return;

        ShellLink.Create(linkPath, exe, DisplayName, Aumid, exe);
    }

    /// <summary>
    /// 现有快捷方式是否已指向我们的 exe。发布目录变更后旧快捷方式指向的 exe
    /// 已不存在，必须识别出来并重建，否则点通知会启动一个已不存在的旧路径。
    /// </summary>
    private static bool ShortcutTargets(string linkPath, string exe)
    {
        return ShellLink.TryGetTarget(linkPath, out var target)
               && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
    }
}
