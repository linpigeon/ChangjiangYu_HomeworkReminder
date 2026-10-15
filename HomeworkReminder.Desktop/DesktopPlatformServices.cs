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

    /// <summary>
    /// 为安装更新而退出。
    /// <para>
    /// 优先走 <see cref="MainViewModel.ExitForUpdate"/>（桌面头指派到
    /// <c>MainWindow.QuitForReal</c>，它会先持久化小组件位置再关停所有窗口）。
    /// 未指派时退回直接关停应用——更新流程比小组件位置重要，不能卡在退出上。
    /// </para>
    /// </summary>
    public void RequestExitForUpdate()
    {
        if (ViewModels.MainViewModel.ExitForUpdate is { } exit)
        {
            exit();
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
