using System.ComponentModel;
using Avalonia.Controls;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 应用主窗口：根据登录状态切换主界面与登录页。
/// <para>
/// 登录态下窗口收缩到刚好容下二维码卡片的紧凑尺寸并禁止拉伸，
/// 避免 WebView 页面在过大视口里露出大片空白；进入主界面后恢复
/// 正常尺寸与可拉伸行为（与 XAML 中声明的默认尺寸一致）。
/// </para>
/// <para>
/// 点「关闭」不直接退出：先弹确认框，可选择直接关闭或最小化到系统托盘
/// （<see cref="Hide"/> 后窗口从任务栏消失，只剩托盘图标）。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>登录页紧凑尺寸。</summary>
    private const double LoginWidth = 480;
    private const double LoginHeight = 700;

    /// <summary>主界面尺寸，与 MainWindow.axaml 中的声明保持一致。</summary>
    private const double MainWidth = 1180;
    private const double MainHeight = 760;
    private const double MainMinWidth = 960;
    private const double MainMinHeight = 580;

    /// <summary>托盘菜单「退出」等路径要求真正关闭时置位，跳过关闭确认。</summary>
    private bool _allowRealClose;

    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnWindowClosing;
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Attach(DataContext as MainViewModel);
    }

    /// <summary>真正退出程序（托盘菜单用）：跳过「最小化到托盘」确认。</summary>
    public void QuitForReal()
    {
        _allowRealClose = true;
        Close();
    }

    /// <summary>从托盘恢复窗口。</summary>
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowRealClose) return;

        // 必须先同步取消关闭，再弹确认框。
        e.Cancel = true;

        var choice = await MessageBox.ConfirmCloseAsync(this);
        switch (choice)
        {
            case MessageBox.CloseChoice.Minimize:
                Hide();
                break;
            case MessageBox.CloseChoice.Quit:
                _allowRealClose = true;
                Close();
                break;
            // Cancel：什么都不做，窗口保持打开。
        }
    }

    private void Attach(MainViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is null) return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        ApplyMode(_vm.ShowLogin);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowLogin) && _vm is not null)
            ApplyMode(_vm.ShowLogin);
    }

    private void ApplyMode(bool isLogin)
    {
        if (isLogin)
        {
            CanResize = false;
            MinWidth = LoginWidth;
            MinHeight = LoginHeight;
            Width = LoginWidth;
            Height = LoginHeight;
        }
        else
        {
            // 恢复主界面的正常尺寸与可拉伸行为。
            CanResize = true;
            MinWidth = MainMinWidth;
            MinHeight = MainMinHeight;
            Width = MainWidth;
            Height = MainHeight;
            CenterOnScreen();
        }
    }

    /// <summary>
    /// 登录成功后窗口从紧凑尺寸放大到主界面尺寸，
    /// 若不重新居中会以左上角为锚点膨胀到屏幕外，因此按工作区重新居中。
    /// </summary>
    private void CenterOnScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var scaling = RenderScaling;
        if (scaling <= 0) scaling = 1;
        var area = screen.WorkingArea;
        Position = new Avalonia.PixelPoint(
            area.X + (int)((area.Width - Width * scaling) / 2),
            area.Y + (int)((area.Height - Height * scaling) / 2));
    }
}
