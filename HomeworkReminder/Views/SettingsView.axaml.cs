using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HomeworkReminder.Services;
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

        if (files.Count == 0 || files[0] is not { } file) return;

        // 复制到应用数据目录再设置：Android 的 SAF 返回 content://（没有本地路径，
        // TryGetLocalPath 为 null）；桌面端同理受益——源文件被移动/删除后壁纸仍在。
        try
        {
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
        Vm?.ShowSettingsTab();
    }

    private void OnShowAboutTabClick(object? sender, RoutedEventArgs e)
    {
        Vm?.ShowAboutTab();
    }
}
