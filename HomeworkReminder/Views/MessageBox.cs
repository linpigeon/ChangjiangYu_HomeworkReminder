using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;

namespace HomeworkReminder.Views;

/// <summary>
/// 极简对话框。Avalonia 没有内建 MessageBox，这里用一个临时 Window 实现，
/// 避免为了两个确认框引入额外的对话框库。
/// 颜色在打开时按当前主题从应用资源解析（对话框生命周期只有几秒，不需要实时跟随切换）。
/// </summary>
internal static class MessageBox
{
    /// <summary>从应用资源取主题画刷，取不到时用兜底色。</summary>
    private static IBrush Res(string key, string fallback)
    {
        var app = Application.Current;
        if (app?.TryGetResource(key, app.ActualThemeVariant, out var v) == true && v is IBrush b)
            return b;
        return SolidColorBrush.Parse(fallback);
    }

    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var window = Build(title, message, confirm: false, out _);
        await window.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var window = Build(title, message, confirm: true, out var confirmed);
        await window.ShowDialog(owner);
        return confirmed();
    }

    /// <summary>点窗口「关闭」时的选择。</summary>
    public enum CloseChoice
    {
        /// <summary>对话框被关掉/Esc：取消关闭，回到窗口。</summary>
        Cancel,

        /// <summary>直接退出程序。</summary>
        Quit,

        /// <summary>最小化到系统托盘继续运行。</summary>
        Minimize,
    }

    /// <summary>关闭确认：「直接关闭程序」或「最小化运行」（到托盘）。关掉对话框视为取消。</summary>
    public static async Task<CloseChoice> ConfirmCloseAsync(Window owner)
    {
        var choice = CloseChoice.Cancel;

        var minimizeButton = new Button
        {
            Content = "最小化运行",
            Classes = { "outline" },
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var quitButton = new Button
        {
            Content = "直接关闭程序",
            Classes = { "primary" },
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var window = new Window
        {
            Title = "关闭作业提醒",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("TodoDialogBackground", "#FFFFFF"),
            Icon = LoadIcon(),
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = "关闭作业提醒？",
                        FontSize = 17,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Res("TodoTextPrimary", "#1B1B1B"),
                    },
                    new TextBlock
                    {
                        Text = "最小化运行会收起窗口，在系统托盘保留小图标，同步与提醒照常进行。",
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = Res("TodoTextSecondary", "#5C5C5C"),
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { minimizeButton, quitButton },
                    },
                },
            },
        };

        minimizeButton.Click += (_, _) => { choice = CloseChoice.Minimize; window.Close(); };
        quitButton.Click += (_, _) => { choice = CloseChoice.Quit; window.Close(); };

        await window.ShowDialog(owner);
        return choice;
    }

    /// <summary>加载应用图标给弹窗用；加载失败不阻塞弹窗。</summary>
    internal static WindowIcon? LoadIcon()
    {
        try
        {
            return new WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new System.Uri("avares://HomeworkReminder/Assets/app.ico")));
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private static Window Build(string title, string message, bool confirm, out System.Func<bool> result)
    {
        var accepted = false;
        result = () => accepted;

        var okButton = new Button
        {
            Content = confirm ? "确定" : "好",
            Classes = { "primary" },
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        if (confirm)
        {
            var cancel = new Button
            {
                Content = "取消",
                Classes = { "outline" },
                MinWidth = 88,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(okButton);

        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("TodoDialogBackground", "#FFFFFF"),
            Icon = LoadIcon(),
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 17,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Res("TodoTextPrimary", "#1B1B1B"),
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = Res("TodoTextSecondary", "#5C5C5C"),
                    },
                    buttons,
                },
            },
        };

        okButton.Click += (_, _) => { accepted = true; window.Close(); };
        if (confirm && buttons.Children[0] is Button c)
            c.Click += (_, _) => { accepted = false; window.Close(); };

        return window;
    }
}
