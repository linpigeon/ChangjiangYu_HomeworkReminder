using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>设置页的交互（壁纸文件选择）。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

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
            vm.SetWallpaper(path);
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
        Vm?.ShowSettingsTab();
    }

    private void OnShowAboutTabClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowAboutTab();
    }
}
