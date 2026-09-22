using Avalonia.Controls;

namespace HomeworkReminder.Views;

/// <summary>
/// 桌面外壳：三栏（导航 / 列表 / 详情），全部是共享部件的组合，没有自有交互。
/// 移动端外壳见 Android 工程的 MobileShellView（抽屉式布局）。
/// </summary>
public partial class TodoShellView : UserControl
{
    public TodoShellView()
    {
        InitializeComponent();
    }
}
