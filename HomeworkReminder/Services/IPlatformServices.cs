namespace HomeworkReminder.Services;

/// <summary>
/// 平台相关操作的抽象，由各平台头（Desktop/Android/…）实现并在启动时注入
/// （<see cref="HomeworkReminder.ViewModels.MainViewModel.PlatformServices"/>）。
/// 共享项目不直接调用 Process.Start 之类的平台 API。
/// <para>
/// 已知取舍：登录用的 <c>Avalonia.Controls.NativeWebView</c> 仍直接出现在共享项目里
/// ——LoginView.axaml 直接宿主该控件，MainViewModel.CompleteLoginAsync 的签名也接收它。
/// 彻底抽离需要把登录视图整体挪到 Desktop 头（共享项目只剩抽象），改动面大，
/// 本次刻意不做，留待需要支持无 WebView 的平台时再评估。
/// </para>
/// </summary>
public interface IPlatformServices
{
    /// <summary>
    /// 在系统默认浏览器中打开 URL。
    /// 实现应自行吞掉异常：打不开浏览器不影响应用其它功能。
    /// </summary>
    void OpenUrl(string url);

    /// <summary>
    /// 尝试在雨课堂 App 中打开作业链接（仅移动端有意义）。返回 false = 未安装或失败，
    /// 调用方应回落到 <see cref="OpenUrl"/>。
    /// </summary>
    bool OpenInYktApp(string url) => false;

    /// <summary>
    /// 打开应用内答题页**之前**的会话铺开（把雨课堂主域的 cookie 复制到答题子域）。
    /// <para>
    /// 存在的理由：移动外壳 <c>MobileShellView</c> 原本直接调用 Android 的
    /// <c>CookieManager</c>，于是它**只能编译进 Android 头**——而界面遮挡审计
    /// （<c>--androidcheck</c>，在桌面头离屏渲染手机尺寸）恰恰需要编译这同一个外壳。
    /// 把平台专属的那一句抽到这里，外壳就能留在共享项目里，两边编译的是同一份代码
    /// （否则审计的是副本，改一处漏一处，正是本仓库反复踩过的坑）。
    /// </para>
    /// <para>默认空实现：桌面/其它平台没有跨子域 cookie 这回事。</para>
    /// </summary>
    void PrepareExamWebSession() { }

    /// <summary>
    /// 「为安装更新而退出应用」。
    /// <para>
    /// 实现方应重写到本平台**正常的退出路径**上：桌面头要在退出前持久化小组件位置
    /// （见 <c>MainWindow.QuitForReal</c>），直接 <c>Shutdown()</c> 会丢掉那个位置。
    /// 默认实现是通用兜底：关闭 Avalonia 应用；非桌面生命周期下什么都不做。
    /// </para>
    /// </summary>
    void RequestExitForUpdate()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
