using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>设置页的交互（壁纸文件选择、更新确认）。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // 发现新版本后的「是否现在更新」确认框。
        // 由视图提供弹窗（需要 Window 作为 owner），ViewModel 只负责问「同不同意」，
        // 两者通过委托解耦。切换 DataContext 时重新挂钩，避免挂到旧的 ViewModel 上。
        DataContextChanged += (_, _) => AttachUpdateConfirm();
        AttachUpdateConfirm();
    }

    /// <summary>把确认框挂到当前 ViewModel 上（无 ViewModel 时清空）。</summary>
    private void AttachUpdateConfirm()
    {
        if (Vm is { } vm)
        {
            vm.ConfirmUpdateRequested = ConfirmUpdateAsync;
        }
    }

    /// <summary>弹确认框问用户是否现在更新。</summary>
    private async Task<bool> ConfirmUpdateAsync(string title, string message)
    {
        // 没有可用的窗口时**返回 false**（不同意）——更新属于「不该在没人确认时擅自发生」的操作。
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        try
        {
            return await MessageBox.ConfirmAsync(owner, title, message);
        }
        catch (Exception)
        {
            // 弹窗失败不能让更新流程把异常抛到界面外，按「不同意」处理。
            return false;
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>保存教务系统登录页网址（非 https 会在 ViewModel 里被拒绝并给出提示）。</summary>
    private async void OnSaveJwUrlClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            await vm.SaveJwLoginUrlAsync();
        }
        catch (Exception)
        {
            // async void：异常逃逸会终止进程。保存失败不影响其它设置。
        }
    }

    private async void OnPickWallpaperClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        // try 必须从文件选择器就包住：OpenFilePickerAsync 与 OpenReadAsync 都会抛
        // （存储提供程序不可用、流被关闭等），async void 里逃逸会终止进程。
        try
        {
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

            if (files.Count == 0 || files[0] is not { } file) return;

            // 复制到应用数据目录再设置：Android 的 SAF 返回 content://（没有本地路径，
            // TryGetLocalPath 为 null）；桌面端同理受益——源文件被移动/删除后壁纸仍在。
            var ext = System.IO.Path.GetExtension(file.Name);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            var dest = System.IO.Path.Combine(AppPaths.DataDirectory, "wallpaper" + ext);
            await using (var src = await file.OpenReadAsync())
            await using (var dst = System.IO.File.Create(dest))
            {
                await src.CopyToAsync(dst);
            }
            vm.SetWallpaper(dest);
        }
        catch (Exception ex)
        {
            vm.SyncStatus = $"壁纸读取失败：{ex.Message}";
        }
    }

    private void OnClearWallpaperClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ClearWallpaper();
    }

    private void OnOpenUpdatePageClick(object? sender, RoutedEventArgs e)
    {
        Vm?.OpenUpdatePage();
    }

    private void OnShowSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowSettingsTab(0);
    }

    private void OnShowAccountTabClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowSettingsTab(1);
    }

    private void OnShowTimetableTabClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowSettingsTab(2);
    }

    private void OnShowAboutTabClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowSettingsTab(3);
    }

    // ---------------- 节次时间表编辑器 ----------------

    private void OnAddPeriodClick(object? sender, RoutedEventArgs e) => Vm?.PeriodsEditor.AddPeriod();

    private void OnRemovePeriodClick(object? sender, RoutedEventArgs e) => Vm?.PeriodsEditor.RemoveLast();

    private void OnResetPeriodsClick(object? sender, RoutedEventArgs e) => Vm?.PeriodsEditor.ResetToDefault();

    private void OnDisablePeriodClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PeriodRowViewModel row }) Vm?.PeriodsEditor.Disable(row);
    }

    private void OnEnablePeriodClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PeriodRowViewModel row }) Vm?.PeriodsEditor.Enable(row);
    }

    /// <summary>
    /// 保存时间表：校验失败会在编辑器里给出具体原因（这里不再弹窗，避免两处提示打架）。
    /// </summary>
    private async void OnSavePeriodsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            await vm.PeriodsEditor.SaveAsync();
        }
        catch (Exception ex)
        {
            // async void：异常逃逸会终止进程。
            vm.PeriodsEditor.Status = $"保存失败：{ex.Message}";
            vm.PeriodsEditor.StatusIsError = true;
        }
    }

    private void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        Vm?.SaveProfile();
    }

    private void OnYktLoginClick(object? sender, RoutedEventArgs e)
    {
        Vm?.OpenYktLogin();
    }

    private async void OnYktLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            if (TopLevel.GetTopLevel(this) is Window w &&
                !await MessageBox.ConfirmAsync(w, "退出登录", "将清除本机保存的雨课堂登录态，需要重新扫码。确定吗？"))
                return;
            await vm.LogoutCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // LogoutAsync 自身没有 try/catch，异常会从 async void 处理器逃逸。
            SidebarView.ReportHandlerFailure(vm, "退出登录", ex);
        }
    }

    private void OnChaoxingLoginClick(object? sender, RoutedEventArgs e)
    {
        Vm?.OpenChaoxingLogin();
    }

    private async void OnChaoxingLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            // 与雨课堂退出登录一致：会清掉 WebView 里的登录态，属于需要确认的操作。
            if (TopLevel.GetTopLevel(this) is Window w &&
                !await MessageBox.ConfirmAsync(w, "退出学习通", "将清除本机保存的学习通登录态，需要重新登录。确定吗？"))
                return;
            await vm.LogoutChaoxingAsync();
        }
        catch (Exception ex)
        {
            // 处理器是 async void：异常一旦逃逸就会终止进程，这里必须兜住。
            vm.SyncStatus = $"退出学习通失败：{ex.Message}";
        }
    }

    private void OnPigaiLoginClick(object? sender, RoutedEventArgs e)
    {
        Vm?.OpenPigaiLogin();
    }

    private async void OnPigaiLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            // 与另两个账号一致：会清掉 WebView 里的登录态，属于需要确认的操作。
            if (TopLevel.GetTopLevel(this) is Window w &&
                !await MessageBox.ConfirmAsync(w, "退出批改网", "将清除本机保存的批改网登录态，需要重新登录。确定吗？"))
                return;
            await vm.LogoutPigaiAsync();
        }
        catch (Exception ex)
        {
            vm.SyncStatus = $"退出批改网失败：{ex.Message}";
        }
    }
}
