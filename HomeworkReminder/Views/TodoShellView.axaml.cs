using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 主界面外壳：左侧导航 + 中间待办列表 + 右侧详情。
/// <para>
/// 交互放在 code-behind：勾选框与整行点击都需要知道「点的是哪一项」，
/// 在 Avalonia 里用 Click/PointerPressed 事件配合 DataContext 判断最直接。
/// </para>
/// </summary>
public partial class TodoShellView : UserControl
{
    public TodoShellView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>记录「窄屏自动收起」是我们做的，回到宽屏时只还原这种收起（不覆盖用户手动选择）。</summary>
    private bool _autoCollapsed;

    /// <summary>窄屏（手机竖屏等）自动收起侧边栏，宽屏自动还原。</summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var narrow = Bounds.Width < 700;
        if (narrow && !_autoCollapsed && !vm.SidebarCollapsed)
        {
            vm.SidebarCollapsed = true;
            _autoCollapsed = true;
        }
        else if (!narrow && _autoCollapsed)
        {
            vm.SidebarCollapsed = false;
            _autoCollapsed = false;
        }
    }

    private void OnToggleSidebarClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        // 手动操作后，窄屏自动收起不再插手（直到尺寸再次跨过阈值方向）。
        _autoCollapsed = false;
        vm.ToggleSidebar();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnNavPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Border { DataContext: NavItem nav }) return;

        vm.SelectedNav = nav;
        vm.OnNavChanged();
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.RefreshCommand.ExecuteAsync(null);
    }

    private async void OnLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (TopLevel.GetTopLevel(this) is Window w &&
            !await MessageBox.ConfirmAsync(w, "退出登录", "将清除本机保存的雨课堂登录态，需要重新扫码。确定吗？"))
            return;

        await vm.LogoutCommand.ExecuteAsync(null);
    }

    private async void OnMarkAllReadClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (TopLevel.GetTopLevel(this) is Window w &&
            !await MessageBox.ConfirmAsync(w, "全部标记为已读",
                "雨课堂只提供「全部已读」接口，没有单条已读。这会把通知中心里所有未读一次清空。继续吗？"))
            return;

        var ok = await vm.MarkAllReadAsync();
        if (!ok && TopLevel.GetTopLevel(this) is Window w2)
        {
            await MessageBox.ShowAsync(w2, "标记已读失败",
                "雨课堂没有接受这次「全部已读」请求。可能是登录态已失效，或该接口有变动。");
        }
    }

    private void OnCheckboxClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // 勾选框所在行的 DataContext 就是所属事项——列表行与详情页都是如此。
        if (sender is Control { DataContext: TodoItemViewModel item })
        {
            _ = vm.ToggleDoneAsync(item);
        }
    }

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Border { DataContext: TodoItemViewModel item }) return;

        // 点在勾选框上时不切换选中项，否则勾选会顺带把右侧详情换掉。
        if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>() is not null) return;

        vm.SelectedItem = item;
    }

    private void OnOpenSourceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var item = (sender as Control)?.DataContext as TodoItemViewModel ?? vm.SelectedItem;
        if (item is not null) vm.OpenSource(item);
    }

    private async void OnPickWallpaperClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择壁纸图片",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("图片")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                },
            ],
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
        {
            vm.SetWallpaper(path);
            // 选完收起浮层，让用户立刻看到效果。
            if (sender is Button b) b.Flyout?.Hide();
        }
    }

    private void OnClearWallpaperClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ClearWallpaper();
        if (sender is Button b) b.Flyout?.Hide();
    }
}
