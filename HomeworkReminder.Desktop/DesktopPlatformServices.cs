using System;
using System.Diagnostics;
using HomeworkReminder.Services;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 桌面平台的 <see cref="IPlatformServices"/> 实现。
/// 在 Program.Main 启动早期注入到 MainViewModel.PlatformServices。
/// </summary>
internal sealed class DesktopPlatformServices : IPlatformServices
{
    public void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // 打不开浏览器不影响应用其它功能。
        }
    }
}
