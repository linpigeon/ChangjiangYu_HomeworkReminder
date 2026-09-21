using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 桌面小组件窗口：无边框、透明，可拖动、可缩放，位置与尺寸持久化。
/// <para>
/// 普通窗口层级：不置顶（会一直挡住正在用的窗口），也不钉在桌面层
///（钉桌面后只要桌面被窗口盖住，从托盘「显示小组件」就完全看不到它，像没反应）。
/// 普通层级 + 召唤时 Activate 到最前，两个诉求都成立。
/// </para>
/// <para>
/// <b>没有对应的 .axaml</b>：窗口本身不绘制任何东西（全部外观都在
/// <see cref="WidgetView"/> 的圆角面板里），用代码构造即可。
/// 这样也避免了「XAML 根元素没有无参构造函数」导致运行期加载器不可达的告警。
/// </para>
/// <para>
/// 与主窗口的关系：由 <see cref="MainWindow"/> 持有并挂在托盘同一套生命周期上。
/// 数据不额外请求网络，直接复用 <see cref="MainViewModel"/> 的缓存，
/// 因此与主界面永远一致。
/// </para>
/// </summary>
public class WidgetWindow : Window
{
    private const double DefaultWidth = 340;
    private const double DefaultHeight = 460;
    private const double EdgeMargin = 24;

    /// <summary>窗口边缘多少 DIP 内按下视为「调整大小」而不是「拖动」。</summary>
    private const double ResizeGrip = 7;

    private readonly WidgetViewModel _vm;
    private readonly MainViewModel _main;

    /// <summary>拖动/缩放结束的防抖计时器：OS 拖窗期间位置持续变化，停下来 400ms 视为结束。</summary>
    private readonly Avalonia.Threading.DispatcherTimer _dragEndTimer;

    public WidgetWindow(MainViewModel main)
    {
        _main = main;
        _vm = new WidgetViewModel(main);
        _dragEndTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dragEndTimer.Tick += OnDragEndTimerTick;
        PositionChanged += OnGeometryChanged;
        Resized += OnGeometryChanged;

        var settings = AppSettings.Current;
        Width = settings.WidgetW ?? DefaultWidth;
        Height = settings.WidgetH ?? DefaultHeight;
        MinWidth = 280;
        MinHeight = 340;
        CanResize = true;
        ShowInTaskbar = false;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Title = "作业提醒小组件";

        Closing += OnClosing;
        PointerMoved += OnHoverMoved;

        var view = new WidgetView { DataContext = _vm };
        view.OpenMainRequested += (_, _) => OpenMainRequested?.Invoke(this, EventArgs.Empty);
        // 走 MainViewModel.SetWidgetEnabled：ViewModel、settings、托盘菜单三处状态才能保持一致。
        view.CloseRequested += (_, _) => _main.SetWidgetEnabled(false);
        view.DragRequested += OnDragRequested;
        Content = view;

        RestorePosition();
        _vm.Refresh();
    }

    /// <summary>用户点了「打开主界面」，由 MainWindow 负责还原主窗口。</summary>
    public event EventHandler? OpenMainRequested;

    /// <summary>
    /// Alt+F4 / 任务栏右键关闭会真正销毁窗口，销毁后再 <see cref="ShowWidget"/> 会抛
    /// <see cref="InvalidOperationException"/>，因此统一转成「关闭小组件」的常规路径。
    /// 程序退出时照常放行，位置由 <see cref="PersistPositionForShutdown"/> 保存。
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;

        e.Cancel = true;
        _main.SetWidgetEnabled(false);
    }

    /// <summary>按保存的位置或默认位置（主屏右下角）摆放。</summary>
    private void RestorePosition()
    {
        var settings = AppSettings.Current;
        if (settings.WidgetX is { } x && settings.WidgetY is { } y)
        {
            // 显示器拔除或分辨率变化后，保存的位置可能完全落在所有屏幕之外，
            // 那样小组件不可见也拖不回来，必须回退到默认角落。
            var p = new PixelPoint(x, y);
            if (Screens.All.Any(s => s.WorkingArea.Intersects(WidgetRectAt(p))))
            {
                Position = p;
                return;
            }
        }
        MoveToDefaultCorner();
    }

    /// <summary>小组件在某个物理像素位置占据的区域（尺寸按主屏缩放换算成物理像素）。</summary>
    private PixelRect WidgetRectAt(PixelPoint p)
    {
        var scaling = Screens.Primary?.Scaling ?? 1;
        if (scaling <= 0) scaling = 1;
        return new PixelRect(p, new PixelSize(
            (int)(DefaultWidth * scaling),
            (int)(DefaultHeight * scaling)));
    }

    private void MoveToDefaultCorner()
    {
        var screen = Screens.Primary;
        if (screen is null) return;

        var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var area = screen.WorkingArea;
        Position = new PixelPoint(
            area.X + (int)Math.Max(0, area.Width - (DefaultWidth + EdgeMargin) * scaling),
            area.Y + (int)Math.Max(0, area.Height - (DefaultHeight + EdgeMargin) * scaling));
    }

    /// <summary>
    /// 拖动/缩放窗口：交给 OS 的模态循环（<see cref="Window.BeginMoveDrag"/> /
    /// <see cref="Window.BeginResizeDrag"/>）。自己跟指针行不通——<c>GetPosition(this)</c>
    /// 是相对窗口的，窗口移动发生在事件之后，无论「从起点算」还是「从当前位置增量算」
    /// 都会被这个时差带成振荡或累加爆炸。OS 拖窗没有这些问题。
    /// 结束的时机用 <see cref="Window.PositionChanged"/> / <see cref="Window.Resized"/>
    /// + 防抖拿到：停下来就持久化位置与尺寸。
    /// </summary>
    private void OnDragRequested(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (HitTestResizeEdge(e.GetPosition(this)) is { } edge)
            BeginResizeDrag(edge, e);
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    /// <summary>窗口边缘 <see cref="ResizeGrip"/> DIP 内的区域用于缩放（四个角是双向）。</summary>
    private WindowEdge? HitTestResizeEdge(Point p)
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        var left = p.X <= ResizeGrip;
        var right = p.X >= w - ResizeGrip;
        var top = p.Y <= ResizeGrip;
        var bottom = p.Y >= h - ResizeGrip;

        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        return null;
    }

    /// <summary>悬停到窗口边缘时把光标换成对应的缩放光标，否则用户发现不了可以缩放。</summary>
    private void OnHoverMoved(object? sender, PointerEventArgs e)
    {
        Cursor = HitTestResizeEdge(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            _ => Cursor.Default,
        };
    }

    /// <summary>位置/尺寸变化即视为拖动或缩放中；静止片刻后认为结束，持久化几何信息。</summary>
    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        // 构造期的初始定位（窗口还没显示）不算拖动。
        if (!IsVisible) return;

        _dragEndTimer.Stop();
        _dragEndTimer.Start();
    }

    private void OnDragEndTimerTick(object? sender, EventArgs e)
    {
        _dragEndTimer.Stop();
        PersistGeometry();
    }

    private void PersistGeometry()
    {
        var settings = AppSettings.Current;
        settings.WidgetX = Position.X;
        settings.WidgetY = Position.Y;
        settings.WidgetW = Width;
        settings.WidgetH = Height;
        settings.Save();
    }

    /// <summary>显示并激活（托盘菜单或主界面调用）。</summary>
    public void ShowWidget()
    {
        _vm.Refresh();
        if (!IsVisible) Show();
        Activate();
    }

    /// <summary>隐藏小组件并记住这个选择。</summary>
    public void HideWidget()
    {
        Hide();
        AppSettings.Current.WidgetVisible = false;
        AppSettings.Current.Save();
    }

    /// <summary>主界面同步完成后刷新小组件内容。</summary>
    public void RefreshFromMain() => _vm.Refresh();

    /// <summary>
    /// 切换分组。供 <c>--selfcheck-widget</c> 用：默认分组「我的一天」在无当日到期项时
    /// 是空的，渲染出来只有空状态，看不到列表行的真实表现。
    /// </summary>
    public void SelectGroupForCheck(string key)
    {
        _vm.SelectGroup(key);
        _vm.Refresh();
    }

    /// <summary>随主程序一起退出时调用：只保存位置与尺寸，不关闭窗口，也不改写「是否显示」。</summary>
    public void PersistPositionForShutdown() => PersistGeometry();
}
