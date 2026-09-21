using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HomeworkReminder.Views;

/// <summary>
/// 自绘托盘菜单的宿主窗口：无边框、透明、置顶，弹出在光标处（托盘菜单惯例是向上弹）。
/// 点击菜单外任意处（窗口失焦）或按 Esc 关闭。
/// </summary>
public sealed class TrayMenuWindow : Window
{
    private readonly DateTimeOffset _createdAt = DateTimeOffset.Now;
    private readonly PixelPoint _cursor;

    public TrayMenuWindow(IReadOnlyList<TrayMenuItem> items, PixelPoint cursor)
    {
        _cursor = cursor;

        var view = new TrayMenuView { ItemsSource = items };
        view.ItemInvoked += (_, _) => Close();
        Content = view;

        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;

        // 不用 SizeToContent：它在透明无边框窗口上会失守（实测窗口被拉成了整个工作区大小）。
        // 菜单内容弹出后不再变化，直接量好 DesiredSize 设成固定客户区即可。
        view.Measure(Size.Infinity);
        ClientSize = view.DesiredSize;
        Position = CalculatePosition(view.DesiredSize);

        Opened += OnOpened;
        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;
    }

    private PixelPoint CalculatePosition(Size contentSize)
    {
        // 外边距 8 是给阴影留的，定位时按可视面板大小算。
        var screen = Screens.ScreenFromPoint(_cursor) ?? Screens.Primary;
        var scaling = screen?.Scaling ?? 1;
        if (scaling <= 0) scaling = 1;
        var w = (int)(contentSize.Width * scaling);
        var h = (int)(contentSize.Height * scaling);

        // 托盘菜单从图标向上弹出：光标大致落在菜单下边缘偏右处。
        var x = _cursor.X - w + 8;
        var y = _cursor.Y - h + 4;

        if (screen is { } s)
        {
            var area = s.WorkingArea;
            x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - w));
            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - h));
        }
        return new PixelPoint(x, y);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // 挂到真实窗口后再量一次：字体回退等差异在这里修正。
        if (Content is TrayMenuView view)
        {
            view.Measure(Size.Infinity);
            ClientSize = view.DesiredSize;
            Position = CalculatePosition(view.DesiredSize);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 刚弹出瞬间任务栏可能抢一次焦点，这种「假失焦」不关菜单。
        if (DateTimeOffset.Now - _createdAt > TimeSpan.FromMilliseconds(300))
            Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
