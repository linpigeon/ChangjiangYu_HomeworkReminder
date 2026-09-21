using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HomeworkReminder.Views;

/// <summary>自绘托盘菜单的内容。条目由宿主（<see cref="TrayMenuWindow"/>）灌入。</summary>
public partial class TrayMenuView : UserControl
{
    public TrayMenuView()
    {
        InitializeComponent();
    }

    /// <summary>菜单条目。赋值即刷新列表。</summary>
    public IReadOnlyList<TrayMenuItem> ItemsSource
    {
        get => _itemsSource;
        set
        {
            _itemsSource = value;
            MenuItems.ItemsSource = value;
        }
    }

    private IReadOnlyList<TrayMenuItem> _itemsSource = [];

    /// <summary>任一条目被点击（动作已执行，宿主应关闭菜单）。</summary>
    public event EventHandler? ItemInvoked;

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TrayMenuItem item }) return;
        item.Action?.Invoke();
        ItemInvoked?.Invoke(this, EventArgs.Empty);
    }
}
