using Avalonia.Controls;
using Avalonia.Interactivity;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>待办详情（宽屏右列与窄屏二级页共用）的交互。</summary>
public partial class TodoDetailView : UserControl
{
    public TodoDetailView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnCheckboxClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (sender is Control { DataContext: TodoItemViewModel item })
        {
            _ = vm.ToggleDoneAsync(item);
        }
    }

    private void OnOpenSourceClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var item = (sender as Control)?.DataContext as TodoItemViewModel ?? vm.SelectedItem;
        if (item is not null) vm.OpenSource(item);
    }
}
