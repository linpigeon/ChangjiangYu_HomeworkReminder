using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;

namespace HomeworkReminder.Desktop;

/// <summary>
/// Windows 托盘图标宿主。
/// <para>
/// 为什么不用 Avalonia 的 <c>TrayIcon</c>：它的右键菜单只能是系统原生
/// <c>NativeMenu</c>（Win32 绘制，改不了样式），而且 TrayIcon 只有左键
/// <c>Clicked</c> 事件，拿不到右键时机。这里直接持有 Shell_NotifyIcon 图标：
/// 左键还原主窗口，右键在光标处弹自绘的 <c>TrayMenuWindow</c>。
/// </para>
/// <para>
/// 只覆盖 Windows；Avalonia 桌面头（本工程）本来就是 Windows 专用。
/// 在 UI 线程创建（共用一个消息泵），进程退出时 Dispose 删除图标。
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrayIconHost : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 1;
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_GUID = 0x20;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    // 固定 GUID：Explorer 重启后凭它找回同一个图标位置。
    private static readonly Guid IconGuid = new("3E7A2C41-7B5D-4F0E-9C1A-8D6B2E5F4A01");

    /// <summary>左键单击图标。</summary>
    public event EventHandler? LeftClicked;

    /// <summary>右键单击图标，参数是光标的物理像素坐标。</summary>
    public event EventHandler<PixelPoint>? RightClicked;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly string _tooltip;
    private readonly uint _taskbarCreatedMsg;

    // 防止委托被 GC 回收（原生侧持有它的函数指针）。
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly WndProcDelegate _wndProc;
    private bool _iconAdded;

    private TrayIconHost(string tooltip, IntPtr icon, bool ownsIcon)
    {
        _tooltip = tooltip;
        _icon = icon;
        _ownsIcon = ownsIcon;
        _wndProc = WndProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var className = "HomeworkReminderTrayHost";
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = className,
        };
        var atom = RegisterClassExW(ref wc);

        // 消息专用窗口：不显示，只接收托盘回调。
        _hwnd = CreateWindowExW(0, className, className, 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Log($"tray: RegisterClass={atom} hwnd={_hwnd} err={Marshal.GetLastWin32Error()}");

        AddIcon(tooltip);
    }

    private static void Log(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Services.AppPaths.DataDirectory, "startup.log"),
                $"{DateTimeOffset.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    /// <summary>创建托盘图标并挂到应用事件上。图标取自应用资源里的 app.ico。</summary>
    public static TrayIconHost Attach()
    {
        var (icon, owns) = LoadAppIcon();
        var host = new TrayIconHost("作业提醒 · 长江雨课堂", icon, owns);
        host.LeftClicked += (_, _) => (Avalonia.Application.Current as App)?.ShowMainWindow();
        host.RightClicked += (_, pos) => (Avalonia.Application.Current as App)?.ShowTrayMenuAt(pos);
        return host;
    }

    /// <summary>从 avares 里的 .ico 解析出最大的一帧并转成 HICON（支持 PNG 压缩的条目）。</summary>
    private static (IntPtr Icon, bool Owns) LoadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://HomeworkReminder/Assets/app.ico"));
            var ico = new byte[stream.Length];
            var read = 0;
            while (read < ico.Length)
            {
                var n = stream.Read(ico, read, ico.Length - read);
                if (n <= 0) break;
                read += n;
            }

            // ICONDIR：reserved(2) type(2) count(2)，随后每条 ICONDIRENTRY 16 字节：
            // width(1) height(1) colors(1) reserved(1) planes(2) bitcount(2) bytesInRes(4) imageOffset(4)
            var count = BitConverter.ToUInt16(ico, 4);
            var cx = GetSystemMetrics(49); // SM_CXSMICON
            var cy = GetSystemMetrics(50); // SM_CYSMICON

            // 优先选「不小于目标尺寸的最小一帧」（缩小比放大清晰），没有更大的才用最大帧。
            var bestFitSize = int.MaxValue;
            var bestLargest = -1;
            var bestOffset = 0;
            var bestBytes = 0;
            var largestOffset = 0;
            var largestBytes = 0;
            for (var i = 0; i < count; i++)
            {
                var o = 6 + 16 * i;
                var width = ico[o] == 0 ? 256 : ico[o]; // 0 表示 256
                var bytes = BitConverter.ToInt32(ico, o + 8);
                var offset = BitConverter.ToInt32(ico, o + 12);
                if (offset < 0 || bytes <= 0 || offset + bytes > ico.Length) continue;
                if (width > bestLargest)
                {
                    bestLargest = width;
                    largestOffset = offset;
                    largestBytes = bytes;
                }
                if (width >= cx && width < bestFitSize)
                {
                    bestFitSize = width;
                    bestOffset = offset;
                    bestBytes = bytes;
                }
            }
            if (bestFitSize == int.MaxValue)
            {
                bestOffset = largestOffset;
                bestBytes = largestBytes;
            }

            if (bestBytes > 0)
            {
                var image = new byte[bestBytes];
                Array.Copy(ico, bestOffset, image, 0, bestBytes);
                var hIcon = CreateIconFromResourceEx(image, image.Length, true, 0x00030000, cx, cy, 0);
                if (hIcon != IntPtr.Zero) return (hIcon, true);
            }
        }
        catch (Exception)
        {
            // 图标加载失败不至于拖垮托盘：落到系统默认应用图标。
        }
        // 共享系统图标不归我们所有，不能 DestroyIcon。
        return (LoadIconW(IntPtr.Zero, new IntPtr(32512)), false); // IDI_APPLICATION 兜底
    }

    private void AddIcon(string tooltip)
    {
        var data = NewIconData(tooltip);
        _iconAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        Log($"tray: NIM_ADD={_iconAdded} err={Marshal.GetLastWin32Error()}");
    }

    private NOTIFYICONDATAW NewIconData(string tooltip) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID,
        uCallbackMessage = WmTrayCallback,
        hIcon = _icon,
        szTip = tooltip,
        guidItem = IconGuid,
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmTrayCallback)
        {
            var mouseMsg = (uint)lParam.ToInt64();
            if (mouseMsg == WM_LBUTTONUP)
                LeftClicked?.Invoke(this, EventArgs.Empty);
            else if (mouseMsg == WM_RBUTTONUP && GetCursorPos(out var pt))
                RightClicked?.Invoke(this, new PixelPoint(pt.X, pt.Y));
            return IntPtr.Zero;
        }

        // Explorer 崩溃重启后任务栏会重建，托盘图标要重新挂回去。
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            var data = NewIconData(_tooltip);
            _iconAdded = Shell_NotifyIconW(NIM_ADD, ref data);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_iconAdded)
        {
            var data = NewIconData(_tooltip);
            data.uFlags = NIF_GUID;
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _iconAdded = false;
        }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] pbIconBits, int cbIconBits,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVersion, int cxDesired, int cyDesired, uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);
}
