using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>教务导入的结果（用于界面提示）。</summary>
public sealed record JwFetchResult(bool Success, string Message, int Added, int Updated, int Skipped);


/// <summary>
/// 「这一页现在能不能跑脚本」的闸门。
/// <para>
/// <b>为什么需要它</b>：<see cref="NativeWebView"/> / <see cref="NativeWebDialog"/> 的
/// <c>InvokeScript</c> 在**任何页面加载完成之前**调用会直接失败——不是挂起、也不是返回 null，
/// 而是返回一个 <b>已处于 Faulted 状态</b>的任务：
/// </para>
/// <code>
/// System.AggregateException: One or more errors occurred.
///  (Unable to invoke script before any page was loaded. Listen for NavigationCompleted event.)
///   ---> System.InvalidOperationException: Unable to invoke script before any page was loaded.
///        Listen for NavigationCompleted event.
/// </code>
/// <para>
/// 实测（<c>--jwcheck</c>，本地页）：<c>Show()</c> 之后立刻 <c>InvokeScript</c> 即得到上面的异常，
/// 同一个弹窗在 <c>NavigationCompleted</c> 之后再调用则正常返回。
/// 而在页面跳转把脚本执行上下文销毁时，在飞的脚本任务会以
/// <see cref="TaskCanceledException"/>（"A task was canceled."）结束。
/// 两者都不是「页面里没有悬浮球」，所以调用方必须能区分「页面没就绪」与「页面已就绪但球丢了」，
/// 否则只能在每一次轮询里盲目地撞一次异常。
/// </para>
/// <para>
/// 由持有弹窗的视图在 <c>NavigationStarted</c> / <c>NavigationCompleted</c> 上维护，
/// 注入循环读取。**所有成员只在 UI 线程访问**（与 WebView 同一约束），因此不需要加锁。
/// </para>
/// </summary>
public sealed class PageReadyGate
{
    /// <summary>
    /// 适配器创建后至少等这么久还没有 <c>NavigationCompleted</c>，就允许试一次。
    /// <para>
    /// 兜底：万一「创建 → 首次导航完成」这条链断了（导航被吞、加载超时未回调），
    /// 闸门不能永远关着把功能锁死。此时试一次的代价只是又撞一次上面那个异常（已被收敛），
    /// 而永远不试的代价是悬浮球再也不会出现。
    /// </para>
    /// </summary>
    private static readonly TimeSpan AdapterOnlyGrace = TimeSpan.FromSeconds(2);

    private readonly Stopwatch _sinceAdapterCreated = Stopwatch.StartNew();
    private bool _sawAdapter;
    private bool _navigationCompleted;
    private bool? _lastLoggedReady;

    /// <summary>弹窗/控件的适配器已经建好（能够接受导航与脚本调用）。</summary>
    public void MarkAdapterCreated()
    {
        _sawAdapter = true;
        _sinceAdapterCreated.Restart();
        LogHeld("适配器已创建，等待首次页面加载完成");
    }

    /// <summary>重开弹窗：一切回到「还没有任何页面」的初始状态。</summary>
    public void Reset()
    {
        _sawAdapter = false;
        _navigationCompleted = false;
        _lastLoggedReady = null;
        _sinceAdapterCreated.Restart();
    }

    /// <summary>页面开始跳转：脚本上下文即将被销毁，先关闸。</summary>
    public void MarkNavigating()
    {
        if (_navigationCompleted) LogHeld("页面开始跳转，暂停注入");
        _navigationCompleted = false;
    }

    /// <summary>页面加载完成：脚本可以安全执行了。</summary>
    public void MarkReady()
    {
        _navigationCompleted = true;
        if (_lastLoggedReady != true) JwCaptureService.Log("闸门：页面已就绪，开始注入悬浮球");
        _lastLoggedReady = true;
    }

    /// <summary>当前是否允许执行脚本。</summary>
    public bool IsReady =>
        _navigationCompleted || (_sawAdapter && _sinceAdapterCreated.Elapsed > AdapterOnlyGrace);

    /// <summary>
    /// 真正下发到 WebView 的 <c>InvokeScript</c> 次数。
    /// <para>
    /// 与 <see cref="SkippedNotReady"/> 一起把「页面没就绪就注入」这条不变量变成可断言的数字：
    /// 什么时候 <see cref="InvokeAttempts"/> 都不该在页面为「未就绪」时增长——
    /// 那正是 <c>InvalidOperationException: Unable to invoke script before any page was loaded</c> 的来源。
    /// </para>
    /// </summary>
    public int InvokeAttempts { get; private set; }

    /// <summary>因「页面尚未加载完成」而被拦下的脚本调用次数（这些调用没有下发到 WebView）。</summary>
    public int SkippedNotReady { get; private set; }

    /// <summary>
    /// 记一次脚本调用尝试，并回答「是否允许真正下发」。
    /// <para>
    /// <b>必须在调用 <c>InvokeScript</c> 之前问</b>：返回 false 时调用方不得下发（见 <see cref="WebViewTarget.EvalAsync"/>）。
    /// </para>
    /// </summary>
    internal bool TryCountInvoke()
    {
        if (!IsReady)
        {
            SkippedNotReady++;
            return false;
        }

        InvokeAttempts++;
        return true;
    }

    /// <summary>清零计数（回归断言前调用）。</summary>
    public void ResetCounters()
    {
        InvokeAttempts = 0;
        SkippedNotReady = 0;
    }

    /// <summary>闸门日志去抖：只在状态翻转时说一次，避免 1 秒一轮刷满日志。</summary>
    private void LogHeld(string reason)
    {
        if (_lastLoggedReady == false) return;
        _lastLoggedReady = false;
        JwCaptureService.Log($"闸门：{reason}");
    }
}


/// <summary>
/// 教务系统课表导入：在教务页注入「一键获取课表」悬浮球，用户登录并导航到课表页后点它抓取当前页。
/// <para>
/// <b>不代填表单、不代提交、不注入凭据</b>——用户全程在自己的学校系统里操作，
/// 本工具只读取他已经打开的那个页面（与「浏览器插件抓取当前页」是同一性质）。
/// </para>
/// <para>
/// <b>与旧项目（Electron）的关键差异</b>：那边 <c>executeJavaScript</c> 会 await Promise，
/// 所以悬浮球脚本可以直接 `return new Promise(...)` 把「点击」传回来。
/// 而 Avalonia 的 <see cref="NativeWebView.InvokeScript"/> **不会等待 Promise**
/// （本项目已实测：等待中的 Promise 会被序列化成 <c>{}</c>），
/// 因此这里改成：脚本**同步返回**当前点击标志，C# 侧**轮询**该标志。
/// 这与批改网/雨课堂登录探测用的是同一个模式。
/// </para>
/// <para>
/// **所有成员只能在 UI 线程访问**（<see cref="NativeWebView"/> 是 COM/STA 控件）；
/// 续体一旦被切到线程池，<c>InvokeScript</c> 永远不会返回。
/// 因此下面的循环一律用 <c>ConfigureAwait(true)</c>。
/// </para>
/// </summary>
public static class JwCaptureService
{
    /// <summary>悬浮球元素 id（加前缀避免与教务页自身元素冲突）。</summary>
    private const string BallId = "__hrJwBall";

    /// <summary>点击标志的全局变量名。</summary>
    private const string ClickFlag = "__hrJwBallClicked";

    /// <summary>轮询间隔。悬浮球只是取页面，1 秒的点击延迟可以接受。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>单次 InvokeScript 的超时：页面跳转或 WebView 未就绪时它可能长时间不返回。</summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(3);

    /// <summary>悬浮球默认文案。</summary>
    public const string DefaultLabel = "一键获取课表";

    private static readonly bool DebugEnabled =
        Environment.GetEnvironmentVariable("HWREMINDER_JW_DEBUG") == "1";

    /// <summary>把运行日志写到数据目录（只在 HWREMINDER_JW_DEBUG=1 时启用）。</summary>
    public static void Log(string message)
    {
        if (!DebugEnabled) return;
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "jw-import.log");
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志失败不能影响功能。
        }
    }

    /// <summary>
    /// 注入/更新悬浮球（<b>同步返回</b>当前点击标志）。
    /// <para>
    /// 点击时先自行把文案改成「正在获取课表…」再置标志——即时反馈，
    /// 不必等 C# 侧下一轮轮询才发现被点了。
    /// </para>
    /// </summary>
    internal static string BuildInjectScript(string label) =>
        "(() => {" +
        $"var ID={JsonStr(BallId)}, FLAG={JsonStr(ClickFlag)};" +
        "var b=document.getElementById(ID);" +
        "if(!b){b=document.createElement('button');b.id=ID;b.type='button';" +
        "Object.assign(b.style,{position:'fixed',right:'20px',bottom:'24px',zIndex:'2147483647'," +
        "maxWidth:'340px',padding:'10px 16px',borderRadius:'999px',border:'1px solid #2564CF'," +
        "background:'#2564CF',color:'#fff',fontSize:'13px',lineHeight:'1.4'," +
        "boxShadow:'0 4px 12px rgba(0,0,0,0.25)',cursor:'pointer',whiteSpace:'normal',textAlign:'left'});" +
        "(document.body||document.documentElement).appendChild(b);" +
        $"b.addEventListener('click',function(){{window[FLAG]=true;b.textContent={JsonStr(BusyLabel)};}});" +
        "}" +
        $"b.textContent={JsonStr(label)};" +
        $"if(window[FLAG]!==true){{window[FLAG]=false;}}" +
        "return String(window[FLAG]);})()";

    /// <summary>点击后的即时文案。</summary>
    private const string BusyLabel = "正在获取课表…";

    /// <summary>只改文案（不改标志、不重建元素）。</summary>
    internal static string BuildLabelScript(string label) =>
        "(() => {" +
        $"var b=document.getElementById({JsonStr(BallId)});" +
        $"if(b){{b.textContent={JsonStr(label)};}}" +
        "return 'ok';})()";

    /// <summary>读取当前页 HTML（只读，不做任何修改）。</summary>
    internal static string BuildCaptureScript() =>
        "(() => { try { return document.documentElement.outerHTML; } catch (e) { return ''; } })()";

    private static string JsonStr(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>在 UI 线程上执行一段脚本并取回字符串结果；超时或异常返回 null（内嵌 WebView）。</summary>
    public static Task<string?> TryEvalAsync(NativeWebView webView, string script, CancellationToken ct = default) =>
        TryEvalCoreAsync(() => webView.InvokeScript(script), ct);

    /// <summary>在 UI 线程上执行一段脚本并取回字符串结果；超时或异常返回 null（独立弹窗）。</summary>
    public static Task<string?> TryEvalAsync(NativeWebDialog dialog, string script, CancellationToken ct = default) =>
        TryEvalCoreAsync(() => dialog.InvokeScript(script), ct);

    /// <summary>
    /// 执行脚本的公共部分。
    /// <para>
    /// 内嵌 <see cref="NativeWebView"/> 与独立 <see cref="NativeWebDialog"/> 的
    /// <c>InvokeScript</c> 签名一致，但两者没有暴露这些成员的公共同接口，
    /// 所以用传入委托来收敛，而不是 <c>dynamic</c>——保留编译期类型检查。
    /// </para>
    /// <para>
    /// <b>超时后必须继续 await 原任务</b>：脚本任务以异常（页面未加载完成 /
    /// 跳转导致 <c>TaskCanceledException</c>）或成功结束时，没人 await 它就会被算作
    /// 「未观察的任务异常」，冒到 <c>TaskScheduler.UnobservedTaskException</c>，
    /// 在调试器里表现为莫名其妙的「A task was canceled.」。
    /// 返回值仍然按超时处理（null），但异常一定被吃掉。
    /// </para>
    /// </summary>
    private static async Task<string?> TryEvalCoreAsync(Func<Task<string?>> invoke, CancellationToken ct)
    {
        try
        {
            var task = invoke();
            // linkCts：脚本正常返回时立刻取消计时器，避免每轮轮询都留一个 3 秒的定时器。
            using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var timeout = Task.Delay(ScriptTimeout, linkCts.Token);
            var finished = await Task.WhenAny(task, timeout).ConfigureAwait(true);
            if (!ReferenceEquals(finished, task))
            {
                // 超时：连异常都要观察掉，再返回 null。
                _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                Log($"eval: 超时（{ScriptTimeout.TotalSeconds:0.#}s 未返回）");
                return null;
            }

            linkCts.Cancel();
            // await 原任务：异常在这里被观察并收敛，不留未观察异常。
            // 同步抛错（适配器已销毁）也会被下面的 catch 接住。
            return UnwrapScriptResult(await task.ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            // 页面未加载完成（InvalidOperationException）、正在跳转（TaskCanceledException）、
            // 适配器尚未创建 / 已销毁——都归为「这次拿不到」，由调用方决定下一步。
            Log($"eval: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 拆掉 WebView 给脚本返回值套上的 JSON 壳，还原成脚本**真正**返回的文本。
    /// <para>
    /// <b>实测结论（<c>--jwcheck</c> 探针，本地页）</b>：本框架的 <c>InvokeScript</c> 会把脚本
    /// 返回值序列化成 JSON 字符串交给 C#，而它自己的脚本外壳本身又是一层
    /// <c>JSON.stringify(...)</c>，于是带引号/换行的返回值会被**编两层**：
    /// </para>
    /// <code>
    /// 脚本 return 'ok'                        -> "ok"                    （一层）
    /// 脚本 return 200 / true                  -> 200 / true              （原样，非字符串）
    /// 脚本 return {a:'&lt;b&gt;'}                 -> {"a":"\u003Cb>"}        （对象的 JSON 文本）
    /// 脚本 return '&lt;div class="x"&gt;中&lt;/div&gt;'  -> "\u003Cdiv class=\"x\">中\u003C/div>"   ← 两层：外层 JSON 字符串 + 内层转义
    /// </code>
    /// <para>
    /// 不拆会怎样：抓到的页面 HTML 变成「满篇 <c>\u003C</c>、一个尖括号都没有」的字符串
    /// （实测在真实教务页上就是如此，179KB 的快照里 <c>&lt;html</c> 出现 0 次），
    /// 解析器自然一条课都读不出来——且**没有任何警告**，因为那些标记压根没进解析器。
    /// </para>
    /// <para>
    /// 拆壳是幂等的：只在「首尾都是双引号且能解成字符串」时才拆，拆到不能再拆为止；
    /// 裸数字、布尔、对象的 JSON 文本都不受影响（首字符不是引号）。
    /// </para>
    /// </summary>
    /// <remarks>internal 是为了让离线自检（jsoncheck）能直接断言拆壳结果，见 JsonSelfTest。</remarks>
    internal static string? UnwrapScriptResult(string? raw)
    {
        var value = raw;
        // 最多拆三层：防御性上限，避免畸形输入上反复解析。
        for (var depth = 0; depth < 3; depth++)
        {
            if (value is null || value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;

            string? decoded;
            try
            {
                decoded = System.Text.Json.JsonSerializer.Deserialize<string>(value);
            }
            catch (System.Text.Json.JsonException)
            {
                // 首尾是引号但其实不是 JSON 字符串（例如脚本返回 "a"b"）：原样返回。
                return value;
            }

            if (decoded is null || decoded == value) return value;
            value = decoded;
        }

        return value;
    }

    /// <summary>
    /// 把 <c>InvokeScript</c> 的返回归一成脚本真正返回的字符串。
    /// <para>
    /// 不同适配器的返回形态不一致：有的给 <c>"ok"</c>（带引号的 JSON 字符串），
    /// 有的给 <c>ok</c> 裸串；布尔也可能是 <c>true</c>/<c>"true"</c>。
    /// 这里统一去引号再判等，避免因形态差异漏判点击。
    /// </para>
    /// </summary>
    private static string Normalize(string? raw) => (raw ?? string.Empty).Trim().Trim('"');

    /// <summary>探测一次「悬浮球是否被点击」（内嵌 WebView）。首次调用会创建球体。</summary>
    public static async Task<bool> WasClickedAsync(NativeWebView webView, CancellationToken ct = default) =>
        Normalize(await InjectBallAsync(webView, null, ct).ConfigureAwait(true)) == "true";

    /// <summary>探测一次「悬浮球是否被点击」（独立弹窗）。首次调用会创建球体。</summary>
    public static async Task<bool> WasClickedAsync(NativeWebDialog dialog, CancellationToken ct = default) =>
        Normalize(await InjectBallAsync(dialog, null, ct).ConfigureAwait(true)) == "true";

    /// <summary>
    /// 注入（创建或复用）悬浮球，并返回当前点击标志。
    /// <para>
    /// 与只是「更新文案」的 <see cref="SetLabelAsync"/> 区分开：创建球体这件事不该藏在探测里，
    /// 否则测试与调用方都容易误以为 <c>SetLabelAsync</c> 会把球建出来。
    /// </para>
    /// </summary>
    public static Task<string?> InjectBallAsync(NativeWebView webView, string? label = null, CancellationToken ct = default) =>
        TryEvalAsync(webView, BuildInjectScript(label ?? DefaultLabel), ct);

    /// <summary>注入（创建或复用）悬浮球，返回当前点击标志（独立弹窗）。</summary>
    public static Task<string?> InjectBallAsync(NativeWebDialog dialog, string? label = null, CancellationToken ct = default) =>
        TryEvalAsync(dialog, BuildInjectScript(label ?? DefaultLabel), ct);

    /// <summary>更新悬浮球文案（处理中/结果反馈，内嵌 WebView）。</summary>
    public static Task SetLabelAsync(NativeWebView webView, string label, CancellationToken ct = default) =>
        TryEvalAsync(webView, BuildLabelScript(label), ct);

    /// <summary>更新悬浮球文案（处理中/结果反馈，独立弹窗）。</summary>
    public static Task SetLabelAsync(NativeWebDialog dialog, string label, CancellationToken ct = default) =>
        TryEvalAsync(dialog, BuildLabelScript(label), ct);

    /// <summary>读取当前页 HTML（内嵌 WebView）。</summary>
    public static async Task<string?> CapturePageAsync(NativeWebView webView, CancellationToken ct = default)
    {
        var raw = await TryEvalAsync(webView, BuildCaptureScript(), ct).ConfigureAwait(true);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>
    /// 读取当前页 HTML（独立弹窗，带页面就绪闸门）。
    /// <para>
    /// 页面没加载完成时**立即返回 null，且完全不调用 <c>InvokeScript</c>**——
    /// 那种调用必定失败（见 <see cref="PageReadyGate"/>），只会污染调试器的异常输出。
    /// </para>
    /// </summary>
    public static async Task<string?> CapturePageAsync(NativeWebDialog dialog, PageReadyGate gate, CancellationToken ct = default)
    {
        var raw = await new WebViewTarget(dialog, gate).CapturePageAsync(ct).ConfigureAwait(true);
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>
    /// 常驻的悬浮球轮询循环：注入悬浮球 → 等点击 → 执行一次抓取 → 更新文案 → 继续等。
    /// <para>
    /// 由视图在适配器就绪后启动、在覆盖层关闭/视图销毁时取消。
    /// <b>必须在 UI 线程调用</b>（循环内全程 <c>ConfigureAwait(true)</c>）。
    /// </para>
    /// </summary>
    /// <param name="webView">教务页 WebView。</param>
    /// <param name="gate">页面就绪闸门；为 null 时不等页面（旧行为，仅测试用）。</param>
    /// <param name="onClicked">点击后的处理，返回给用户看的提示文案。</param>
    /// <param name="ct">取消令牌。</param>
    public static Task RunBallLoopAsync(NativeWebView webView, PageReadyGate? gate, Func<Task<string>> onClicked, CancellationToken ct) =>
        RunBallLoopAsync(new WebViewTarget(webView, gate), onClicked, ct);

    /// <summary>
    /// 常驻的悬浮球轮询循环（独立弹窗版，见 <see cref="RunBallLoopAsync(NativeWebView,PageReadyGate,Func{Task{string}},CancellationToken)"/>）。
    /// </summary>
    public static Task RunBallLoopAsync(NativeWebDialog dialog, PageReadyGate? gate, Func<Task<string>> onClicked, CancellationToken ct) =>
        RunBallLoopAsync(new WebViewTarget(dialog, gate), onClicked, ct);

    private static async Task RunBallLoopAsync(WebViewTarget target, Func<Task<string>> onClicked, CancellationToken ct)
    {
        Log("ball: 循环开始");
        var label = DefaultLabel;
        var pausedLogged = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 页面还没加载完成时 **一个脚本都不要发**：
                // 此时 InvokeScript 必定以 InvalidOperationException 失败（实测见 PageReadyGate 注释），
                // 每秒撞一次只会刷满调试器的异常输出。等页面就绪再注入。
                if (!target.IsPageReady)
                {
                    if (!pausedLogged)
                    {
                        pausedLogged = true;
                        Log("ball: 页面尚未就绪，暂停注入");
                    }
                    await Task.Delay(PollInterval, ct).ConfigureAwait(true);
                    continue;
                }
                pausedLogged = false;

                var clicked = await target.WasClickedAsync(ct).ConfigureAwait(true);
                if (clicked)
                {
                    Log("ball: 检测到点击");
                    await target.SetLabelAsync(BusyLabel, ct).ConfigureAwait(true);
                    string message;
                    try
                    {
                        message = await onClicked().ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        message = $"获取失败：{ex.Message}";
                        Log($"ball: 处理异常 {ex.GetType().Name}: {ex.Message}");
                    }
                    label = message;
                    await target.SetLabelAsync(label, ct).ConfigureAwait(true);

                    // 处理完等一轮再重新注入：给页面一点时间稳住文案，
                    // 同时避免上一轮的点击标志被立刻重读成新的点击。
                    await Task.Delay(PollInterval, ct).ConfigureAwait(true);
                    continue;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止（含 WebView 以 TaskCanceledException 结束脚本任务的情况）。
        }
        catch (ObjectDisposedException)
        {
            // 窗口/适配器已经销毁，循环随之结束。
            Log("ball: 适配器已销毁，循环结束");
        }
        finally
        {
            Log("ball: 循环结束");
        }
    }

    /// <summary>
    /// 抓取目标抽象：内嵌 <see cref="NativeWebView"/> 与独立 <see cref="NativeWebDialog"/>
    /// 的脚本能力一致，但没有暴露这些成员的公共同接口，因此在这里收敛成一处，
    /// 让注入/轮询/抓取那套逻辑只写一份。
    /// </summary>
    private readonly struct WebViewTarget
    {
        private readonly NativeWebView? _view;
        private readonly NativeWebDialog? _dialog;
        private readonly PageReadyGate? _gate;

        public WebViewTarget(NativeWebView view, PageReadyGate? gate = null) { _view = view; _dialog = null; _gate = gate; }

        public WebViewTarget(NativeWebDialog dialog, PageReadyGate? gate = null) { _view = null; _dialog = dialog; _gate = gate; }

        /// <summary>页面是否已经可以跑脚本（没传闸门时按「可以」处理，保持旧行为）。</summary>
        public bool IsPageReady => _gate?.IsReady ?? true;

        public Task<string?> EvalAsync(string script, CancellationToken ct)
        {
            // 第二道闸：即使调用方忘了先看 IsPageReady，也绝不在页面就绪前调用 InvokeScript。
            // （页面未加载完成时 InvokeScript 必定失败，见 PageReadyGate 的实测记录。）
            if (_gate is not null && !_gate.TryCountInvoke())
            {
                Log("eval: 跳过（页面尚未加载完成）");
                return Task.FromResult<string?>(null);
            }

            return _view is not null ? TryEvalAsync(_view, script, ct) : TryEvalAsync(_dialog!, script, ct);
        }

        public async Task<bool> WasClickedAsync(CancellationToken ct) =>
            Normalize(await EvalAsync(BuildInjectScript(DefaultLabel), ct).ConfigureAwait(true)) == "true";

        public async Task SetLabelAsync(string label, CancellationToken ct) =>
            await EvalAsync(BuildLabelScript(label), ct).ConfigureAwait(true);

        public async Task<string?> CapturePageAsync(CancellationToken ct)
        {
            var raw = await EvalAsync(BuildCaptureScript(), ct).ConfigureAwait(true);
            return string.IsNullOrEmpty(raw) ? null : raw;
        }
    }

    /// <summary>
    /// 把抓到的页面 HTML 存一份到数据目录（<c>jw-last-capture.html</c>）。
    /// <para>
    /// 用途是**排查解析失败**：各校正方页面的表格结构（合并单元格、附加行、嵌套层次）
    /// 差异很大，光凭截图无法确定；留着这份原始快照就能离线复现并按真实结构修解析。
    /// 抓取失败时用户把这份文件发来即可定位。
    /// </para>
    /// <para>只存页面结构，不含账号口令；文件在用户自己的数据目录里。</para>
    /// </summary>
    private static void SaveSnapshot(string html)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "jw-last-capture.html");
            File.WriteAllText(path, html, new System.Text.UTF8Encoding(false));
            Log($"snapshot: 已保存到 {path}");
        }
        catch (Exception ex)
        {
            Log($"snapshot: 保存失败 {ex.Message}");
        }
    }

    /// <summary>
    /// 抓取当前页 → 解析 → 合并导入。返回给用户看的提示文案。
    /// <para>
    /// 失败时把原因说清楚（「未识别出课表结构」而不是笼统的「失败」），
    /// 因为最常见的失败原因是「用户还没导航到课表页」。
    /// </para>
    /// </summary>
    public static async Task<JwFetchResult> FetchAndImportAsync(
        NativeWebView webView,
        Func<ScheduleFile> getSchedule,
        Func<ScheduleFile, ScheduleMergeResult, Task> saveAsync,
        int term,
        CancellationToken ct = default) =>
        await FetchAndImportCoreAsync(new WebViewTarget(webView), getSchedule, saveAsync, term, ct).ConfigureAwait(true);

    /// <summary>
    /// 抓取当前页 → 解析 → 合并导入（独立弹窗版，见
    /// <see cref="FetchAndImportAsync(NativeWebView,Func{ScheduleFile},Func{ScheduleFile,ScheduleMergeResult,Task},int,CancellationToken)"/>）。
    /// </summary>
    /// <param name="pageGate">
    /// 页面就绪闸门。页面没加载完成时**连脚本都不会发**，直接返回「请确认页面已加载完成」，
    /// 而不是去撞 WebView 那条「Unable to invoke script before any page was loaded」。
    /// </param>
    public static async Task<JwFetchResult> FetchAndImportAsync(
        NativeWebDialog dialog,
        PageReadyGate pageGate,
        Func<ScheduleFile> getSchedule,
        Func<ScheduleFile, ScheduleMergeResult, Task> saveAsync,
        int term,
        CancellationToken ct = default) =>
        await FetchAndImportCoreAsync(new WebViewTarget(dialog, pageGate), getSchedule, saveAsync, term, ct)
            .ConfigureAwait(true);

    private static async Task<JwFetchResult> FetchAndImportCoreAsync(
        WebViewTarget target,
        Func<ScheduleFile> getSchedule,
        Func<ScheduleFile, ScheduleMergeResult, Task> saveAsync,
        int term,
        CancellationToken ct)
    {
        var html = await target.CapturePageAsync(ct).ConfigureAwait(true);
        if (string.IsNullOrEmpty(html))
        {
            return new JwFetchResult(false, "读取当前页失败，请确认页面已加载完成后重试", 0, 0, 0);
        }
        Log($"capture: {html.Length} 字符");
        SaveSnapshot(html);

        var current = getSchedule();
        var parsed = ZhengfangParser.ParseSnapshot(html, term, current.Periods);
        Log($"parse: {parsed.Items.Count} 条，警告 {parsed.Warnings.Count} 条");

        if (parsed.Items.Count == 0)
        {
            var why = parsed.Warnings.Count > 0 ? string.Join("；", parsed.Warnings) : "页面中没有课表数据";
            return new JwFetchResult(false, $"未获取到课程（{why}）。请先进入课表查询页再点悬浮球。", 0, 0, 0);
        }

        var (merged, result) = ScheduleMerge.Merge(current.Items, parsed.Items, term);
        current.Items = merged;
        await saveAsync(current, result).ConfigureAwait(true);

        var warningNote = parsed.Warnings.Count > 0 ? $"，{parsed.Warnings.Count} 条未能识别" : string.Empty;
        return new JwFetchResult(true,
            $"已导入：{result}{warningNote}。可再次点击以合并最新课表。",
            result.Added, result.Updated, result.Skipped);
    }
}
