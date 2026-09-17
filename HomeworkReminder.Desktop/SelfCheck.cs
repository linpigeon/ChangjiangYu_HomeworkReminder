using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeworkReminder.ViewModels;
using HomeworkReminder.Views;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 启动自检。存在的理由：GUI 应用在无人值守环境里无法靠肉眼确认，
/// 所以把「XAML 是否解析、绑定是否解析、控件树是否建起来」变成可断言的输出。
/// <list type="bullet">
///   <item><c>--selfcheck</c>：跑一遍构造 + 布局 + 绑定检查，打印结果后退出。</item>
///   <item><c>--render &lt;path&gt;</c>：用 Skia 离屏渲染成 PNG，可在无显示环境下查看界面。</item>
/// </list>
/// </summary>
internal static class SelfCheck
{
    /// <summary>收集 Avalonia 的绑定/解析告警。空列表表示没有发现问题。</summary>
    private sealed class CollectingLogSink : ILogSink
    {
        public List<string> Messages { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Messages.Add($"[{level}] {area}: {messageTemplate}");

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
            => Messages.Add($"[{level}] {area}: {messageTemplate} :: {string.Join(", ", propertyValues.Select(p => p?.ToString() ?? "null"))}");
    }

    public static int Run(Window window, string? renderPath)
    {
        var sink = new CollectingLogSink();
        Logger.Sink = sink;
        var output = new StringBuilder();

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            output.AppendLine("=== 自检开始 ===");
            output.AppendLine($"window      : {window.GetType().Name} '{window.Title}'");
            output.AppendLine($"size        : {window.Width}x{window.Height}");

            // 触发一次真实布局：数据模板、样式选择器、绑定都在这时求值。
            window.Measure(new Size(window.Width, window.Height));
            window.Arrange(new Rect(0, 0, window.Width, window.Height));
            Dispatcher.UIThread.RunJobs();

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            Walk(window, counts);
            output.AppendLine();
            output.AppendLine("=== 控件树统计 ===");
            foreach (var (k, v) in counts.OrderByDescending(p => p.Value))
                output.AppendLine($"  {k,-26} {v}");

            // 关键控件是否真的建出来了。
            output.AppendLine();
            output.AppendLine("=== 关键控件存在性 ===");
            foreach (var name in new[] { "WebViewHost", "LoginWebView", "CookieBox", "LoginButton" })
            {
                var found = window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name);
                output.AppendLine($"  {name,-16} {(found ? "已创建" : "未创建（可能被 IsVisible=false 折叠，属预期）")}");
            }

            if (renderPath is not null)
            {
                // 位图（头像）与 WebView 都是异步就绪的，不等一会儿截图里对应区域会是空白，
                // 容易被误判成「渲染失败」。这里按是否存在 WebView 决定等待时长。
                var hasWebView = window.GetVisualDescendants()
                    .OfType<Avalonia.Controls.NativeWebView>().Any();

                var budget = hasWebView ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(3);
                var waited = TimeSpan.Zero;
                var step = TimeSpan.FromMilliseconds(250);

                while (waited < budget)
                {
                    Dispatcher.UIThread.RunJobs();
                    System.Threading.Thread.Sleep((int)step.TotalMilliseconds);
                    waited += step;

                    if (hasWebView)
                    {
                        // WebView 适配器就绪后即可停等：再等下去页面内容也不会进入离屏渲染。
                        var ready = window.GetVisualDescendants()
                            .OfType<Avalonia.Controls.NativeWebView>()
                            .Any(v => v.AdapterInfo is not null);
                        if (ready && waited > TimeSpan.FromSeconds(6)) break;
                    }
                }

                Dispatcher.UIThread.RunJobs();
                output.AppendLine($"  （渲染前等待 {(int)waited.TotalMilliseconds} ms，WebView={hasWebView}）");

                window.Measure(new Size(window.Width, window.Height));
                window.Arrange(new Rect(0, 0, window.Width, window.Height));
                Dispatcher.UIThread.RunJobs();

                var size = new PixelSize((int)window.Width, (int)window.Height);
                using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(window);
                bitmap.Save(renderPath);

                var info = new System.IO.FileInfo(renderPath);
                output.AppendLine();
                output.AppendLine($"=== 离屏渲染 ===\n  {renderPath} ({info.Length} bytes)");
            }

            output.AppendLine();
            output.AppendLine("=== Avalonia 告警/错误 ===");
            if (sink.Messages.Count == 0)
            {
                output.AppendLine("  （无）—— 未发现绑定或 XAML 解析问题");
            }
            else
            {
                foreach (var m in sink.Messages.Distinct()) output.AppendLine("  " + m);
            }
        }
        catch (Exception ex)
        {
            output.AppendLine();
            output.AppendLine("=== 自检异常 ===");
            output.AppendLine(ex.ToString());
            Console.WriteLine(output.ToString());
            return 1;
        }
        finally
        {
            Logger.Sink = null;
        }

        Console.WriteLine(output.ToString());
        return sink.Messages.Count == 0 ? 0 : 2;
    }

    private static void Walk(Visual visual, Dictionary<string, int> counts, int depth = 0)
    {
        if (depth > 40) return;

        var name = visual.GetType().Name;
        counts[name] = counts.GetValueOrDefault(name) + 1;

        foreach (var child in visual.GetVisualChildren())
        {
            Walk(child, counts, depth + 1);
        }
    }
}
