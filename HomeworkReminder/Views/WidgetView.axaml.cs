using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 桌面小组件的交互。
/// <para>
/// 拖动与「打开主界面」「关闭小组件」都通过事件回传给宿主窗口
/// （<c>WidgetWindow</c>），因为只有窗口才能改自己的位置和可见性。
/// </para>
/// </summary>
public partial class WidgetView : UserControl
{
    public WidgetView()
    {
        InitializeComponent();
    }

    /// <summary>请求打开主界面。</summary>
    public event EventHandler? OpenMainRequested;

    /// <summary>请求关闭小组件。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>行上按下：交给宿主开始拖动（点在勾选框上除外）。</summary>
    public event EventHandler<PointerPressedEventArgs>? DragRequested;

    private WidgetViewModel? Vm => DataContext as WidgetViewModel;

    private void OnOpenMainClick(object? sender, RoutedEventArgs e)
        => OpenMainRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSyncClick(object? sender, RoutedEventArgs e)
        => Vm?.RequestSync();

    private async void OnCheckboxClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TodoItemViewModel item } || Vm is not { } vm)
            return;

        try
        {
            await vm.ToggleAsync(item);
        }
        catch (Exception)
        {
            // 勾选没落盘只是这次没记住，下次刷新会以真实状态为准。
        }
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        // 点在勾选框上时不触发拖动：勾选由 CheckBox 自己的 Click 处理
        //（左键按下会被 CheckBox 标记 Handled 而到不了这里，这里防的是其余按键）。
        if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>() is not null) return;

        // 其余区域按下即视为拖动窗口。Handled 阻止冒泡到 RootPanel 重复触发。
        DragRequested?.Invoke(this, e);
        e.Handled = true;
    }

    private void OnPanelPressed(object? sender, PointerPressedEventArgs e)
    {
        // 整面板都可拖动，但交互控件（勾选框、按钮、下拉框、滚动条）要留给它们自己。
        if (e.Source is Visual v &&
            (v.FindAncestorOfType<CheckBox>() is not null ||
             v.FindAncestorOfType<Button>() is not null ||
             v.FindAncestorOfType<ComboBox>() is not null ||
             v.FindAncestorOfType<ScrollBar>() is not null))
            return;

        DragRequested?.Invoke(this, e);
        e.Handled = true;
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: TodoItemViewModel item }) return;
        Vm?.Open(item);
    }
}
