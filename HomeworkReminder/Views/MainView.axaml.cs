using Avalonia.Controls;

namespace HomeworkReminder.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        // 内容外壳由平台决定：Android 头注入抽屉式的 MobileShellView；
        // 其他平台（iOS/Browser/回退）用桌面三栏 TodoShellView。
        // DataContext 沿可视树继承，无需手动传递。
        ShellHost.Content = App.MobileShellFactory?.Invoke() ?? new TodoShellView();
    }
}
