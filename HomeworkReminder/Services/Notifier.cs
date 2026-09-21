using System;
using System.IO;
using Microsoft.Win32;

namespace HomeworkReminder.Services;

/// <summary>一条系统通知的内容。</summary>
/// <param name="Title">粗体标题，通常是课程名。</param>
/// <param name="Body">正文，通常是作业标题。</param>
/// <param name="Attribution">右下角灰色小字，通常是截止时间。</param>
/// <param name="LaunchUrl">点击通知后打开的地址（可空）。</param>
public readonly record struct AppNotification(
    string Title,
    string Body,
    string? Attribution = null,
    string? LaunchUrl = null);

/// <summary>
/// 系统通知。实现按平台注入（见 <see cref="HomeworkReminder.ViewModels.MainViewModel.Notifier"/>），
/// 共享项目只依赖这个抽象。
/// </summary>
public interface INotifier
{
    /// <summary>当前环境能否发通知（例如非 Windows，或注册 AUMID 失败）。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用时的人类可读原因，用于界面提示；可用时为空。</summary>
    string? UnavailableReason { get; }

    /// <summary>发送一条通知。失败不应抛异常，返回原因字符串（成功为 null）。</summary>
    string? Show(AppNotification notification);
}

/// <summary>
/// 什么都不做的实现，供非 Windows 平台与测试使用。
/// </summary>
public sealed class NullNotifier : INotifier
{
    public bool IsAvailable => false;

    public string? UnavailableReason => "当前平台不支持系统通知";

    public string? Show(AppNotification notification) => UnavailableReason;
}
