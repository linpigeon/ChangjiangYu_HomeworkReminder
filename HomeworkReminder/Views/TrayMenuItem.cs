using System;

namespace HomeworkReminder.Views;

/// <summary>自绘托盘菜单的一个条目。<see cref="IsSeparator"/> 为 true 时渲染成分隔线。</summary>
public sealed class TrayMenuItem
{
    private TrayMenuItem(bool isSeparator) => IsSeparator = isSeparator;

    public TrayMenuItem(string label, Action action, bool isChecked = false)
    {
        Label = label;
        Action = action;
        IsChecked = isChecked;
    }

    public string Label { get; } = "";
    public Action? Action { get; }
    public bool IsChecked { get; }
    public bool IsSeparator { get; }

    public static TrayMenuItem CreateSeparator() => new(true);
}
