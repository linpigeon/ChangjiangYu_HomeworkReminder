using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Android.Views;

/// <summary>Android 外壳（抽屉式移动布局）的交互。</summary>
public partial class MobileShellView : UserControl
{
    public MobileShellView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>点抽屉遮罩：收起抽屉。</summary>
    private void OnDrawerScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm) vm.DrawerOpen = false;
    }

    /// <summary>详情二级页的返回键。</summary>
    private void OnDetailBackClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.SelectedItem = null;
    }
}
