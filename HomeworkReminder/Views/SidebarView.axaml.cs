using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>侧边栏（宽屏栏位与窄屏抽屉共用）的交互。</summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnNavPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Border { DataContext: NavItem nav }) return;

        vm.SelectedNav = nav;
        vm.OnNavChanged();
        // 抽屉模式下选完导航就收起（覆盖式抽屉的惯例）；宽屏下 DrawerOpen 恒为 false。
        vm.DrawerOpen = false;
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
