using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HomeworkReminder.Services;
using HomeworkReminder.ViewModels;

namespace HomeworkReminder.Views;

/// <summary>
/// 登录页。默认走 WebView 扫码；如果宿主机缺少 WebView2 运行时
/// （或环境禁止 WebView2 启动子进程），可以切到「粘贴 Cookie」兜底路径。
/// </summary>
public partial class LoginView : UserControl
{
    /// <summary>
    /// 注入登录页的整理样式与居中脚本。
    /// <para>
    /// 实测页面结构（HWREMINDER_LOGIN_DOM_DUMP）：二维码不在 iframe 里，
    /// 而是直接在 <c>.login-box</c>（416 宽，加载完全后约 510 高）中；
    /// 外层 .wrapper-inner 宽 950，视口只有 448 宽时页面横向溢出，若某个
    /// 元素获得焦点浏览器还会自动横向滚动去「露出」它——这就是二维码
    /// 整体偏右、底部出现横向滚动条的原因。
    /// </para>
    /// <para>
    /// 对策分两层：
    /// 1. CSS（持久生效）：html/body overflow:hidden + 隐藏所有 scrollbar，
    ///    白色 backdrop 遮住横幅与页头，隐藏卡片右上角的「账号登录」翻角；
    /// 2. JS 定时居中：卡片高度随二维码加载增长（448 → ~510），CSS 写死高度
    ///    会把内容挤出盒子导致视觉下移，所以改用 fixed + left/top:50% +
    ///    translate(-50%,-50%)，按实际渲染尺寸居中。雨课堂二维码过期后卡片
    ///    会重渲染、内联样式被冲掉，用 setInterval 常驻补样式（比
    ///    MutationObserver 简单，且页面跳转后定时器随文档销毁）。
    ///    translate 居中在视口比卡片窄时两侧等量裁剪，不会出现
    ///    「margin:auto 溢出时左边缘跑到视口外」的偏移。
    /// </para>
    /// <para>
    /// 重复注入以「标记是否还在」判断：SPA 整页重渲染可能把注入的节点
    /// 干掉，下次注入必须能补回来。脚本必须同步返回（WebView2 不会等待
    /// Promise，见 YktLoginService 的说明）。
    /// </para>
    /// </summary>
    private const string LoginPageStyleScript =
        "(()=>{" +
        "if(!document.getElementById('hr-login-style')){" +
        "var s=document.createElement('style');" +
        "s.id='hr-login-style';" +
        "s.textContent=[" +
        "'html,body{overflow:hidden!important;background:#ffffff!important;height:100%!important}'," +
        "'*::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}'," +
        "'.login-box .changeImg,.login-box .way-tips{display:none!important}'," +
        "'body:has(.login-box)::before{content:\"\";position:fixed;left:0;top:0;right:0;bottom:0;" +
        "background:#ffffff;z-index:2147482999}'" +
        "].join('\\n');" +
        "(document.head||document.documentElement).appendChild(s);}" +
        "if(!window.__hrCenterTimer){" +
        "window.__hrCenterTimer=setInterval(function(){" +
        "var b=document.querySelector('.login-box');" +
        "if(!b)return;" +
        // 祖先链上的 transform/perspective/filter 会把 fixed 的包含块从视口
        // 变成祖先元素（950 宽的 .wrapper-inner 有入场动画 transform），
        // 导致「居中」落到错误的坐标系里、卡片整体偏右——必须逐层清掉。
        "for(var e=b.parentElement;e;e=e.parentElement){" +
        "var cs=getComputedStyle(e);" +
        "if(cs.transform!=='none')e.style.setProperty('transform','none','important');" +
        "if(cs.perspective!=='none')e.style.setProperty('perspective','none','important');" +
        "if(cs.filter!=='none')e.style.setProperty('filter','none','important');}" +
        "var st=b.style;" +
        "if(st.position==='fixed'&&st.transform.indexOf('translate')===0)return;" +
        "st.setProperty('position','fixed','important');" +
        "st.setProperty('left','50%','important');" +
        "st.setProperty('right','auto','important');" +
        "st.setProperty('top','50%','important');" +
        "st.setProperty('bottom','auto','important');" +
        "st.setProperty('margin','0','important');" +
        "st.setProperty('transform','translate(-50%,-50%)','important');" +
        "st.setProperty('z-index','2147483000','important');" +
        "st.setProperty('background','#ffffff','important');" +
        "},700);}" +
        "return 'ok';})()";

    /// <summary>
    /// Android 用的登录页脚本：手机就是微信本体，扫不了自己屏幕上的二维码——
    /// 自动点卡片右上角的「翻角」切到账号登录（手机/短信/邮箱）。
    /// 桌面那套整理样式（白色遮罩、隐藏翻角）不能注入，否则会把表单盖住。
    /// 翻角点击是「切换」，所以用一次性标记防止重复注入时来回翻。
    /// </summary>
    private const string AccountModeScript =
        "(()=>{" +
        "if(!window.__hrAccountSwitch){" +
        "window.__hrAccountSwitch=setInterval(function(){" +
        "var t=document.querySelector('.changeImg');" +
        "if(t){t.click();clearInterval(window.__hrAccountSwitch);window.__hrAccountSwitch=1;}" +
        "},400);}" +
        "return 'ok';})()";

    /// <summary>DOM 摘要脚本，仅在 HWREMINDER_LOGIN_DOM_DUMP=1 时用于排查页面结构。</summary>
    private const string DomDumpScript =
        "(()=>{var R=e=>{var r=e.getBoundingClientRect();return [Math.round(r.left),Math.round(r.top),Math.round(r.width),Math.round(r.height)].join(',');};" +
        "var w=document.querySelector('.J_login')||document.querySelector('.login-wraper');" +
        "var info={url:location.href,vw:innerWidth,vh:innerHeight,scrollH:document.body?document.body.scrollHeight:0};" +
        "info.wrapper=w?(w.className+'@'+R(w)):'none';" +
        "info.imgs=Array.from(document.querySelectorAll('img')).map(i=>i.className+'|'+i.src.slice(-40)+'|'+R(i)).slice(0,20);" +
        "info.canvas=Array.from(document.querySelectorAll('canvas')).map(c=>c.className+'|'+R(c)).slice(0,10);" +
        "info.tree=w?Array.from(w.querySelectorAll('*')).slice(0,60).map(e=>e.tagName+'.'+String(e.className).slice(0,50)+'|'+R(e)):[];" +
        "return JSON.stringify(info);})()";

    /// <summary>
    /// 登录方式探针（<c>HWREMINDER_LOGIN_PROBE=1</c> 时写入 login-probe.txt）。
    /// <para>
    /// 存在的理由：「微信快捷登录」这类入口渲染在页面里，而页面是纯 SPA、静态抓取
    /// 只能拿到空壳；仓库原有的 <see cref="DomDumpScript"/> 又只遍历主文档，
    /// 二维码/登录方式实际在 <c>/authorize/wx-qrlogin</c> 的 iframe 内，所以一直没被发现。
    /// 这里把**主文档与各 iframe 的可见文本、按钮、链接、自定义协议入口**都抓出来，
    /// 用来回答「页面到底有没有渲染出快捷登录入口」这个问题。
    /// </para>
    /// <para>
    /// 全程只读：不改 DOM、不点击、不发送任何请求。
    /// </para>
    /// </summary>
    public const string LoginMethodProbeScript =
        "(function(){var out=[];" +
        "function desc(doc,label){" +
        "var vis=function(e){var s=getComputedStyle(e);if(s.display==='none'||s.visibility==='hidden'||s.opacity==='0')return false;" +
        "var r=e.getBoundingClientRect();return r.width>0&&r.height>0;};" +
        "var pick=function(sel){try{return Array.from(doc.querySelectorAll(sel)).filter(vis).map(function(e){" +
        "return (e.tagName+'['+(e.className||'')+'] '+(e.textContent||'').trim().replace(/\\s+/g,' ').slice(0,60) " +
        "+' href='+(e.getAttribute&&(e.getAttribute('href')||'')||'').slice(0,120)" +
        "+' onclick='+String(e.getAttribute&&e.getAttribute('onclick')||'').slice(0,120)).trim();});}catch(err){return ['<err '+err.message+'>'];}};" +
        "var txt=(doc.body&&doc.body.innerText||'').replace(/\\s+/g,' ').trim().slice(0,600);" +
        "out.push('===== '+label+' url='+doc.location.href);" +
        "out.push('  visibleText: '+txt);" +
        "out.push('  buttons : '+JSON.stringify(pick('button,a,[role=button],.btn,[class*=login],[class*=way],[class*=changeImg]')));" +
        "out.push('  canvas/img: '+JSON.stringify(pick('canvas,img')));" +
        "out.push('  iframes : '+JSON.stringify(Array.from(doc.querySelectorAll('iframe')).map(function(f){return f.src;})));" +
        "} " +
        "desc(document,'TOP');" +
        // 逐层进 iframe；同源才能读，跨源会被挡（用户/页面看到的效果一致，无需特判）。
        "var walk=function(doc,depth){if(depth>2)return;" +
        "Array.from(doc.querySelectorAll('iframe')).forEach(function(f,i){" +
        "try{var d=f.contentDocument;if(d&&d.body){desc(d,'IFRAME'+depth+'.'+i);walk(d,depth+1);}}" +
        "catch(e){out.push('===== IFRAME'+depth+'.'+i+' <cross-origin: '+e.name+'> src='+f.src);}});};" +
        "walk(document,0);" +
        "return out.join('\\n');})()";


    public LoginView()
    {
        InitializeComponent();

        LoginWebView.AdapterCreated += (_, _) =>
        {
            _webViewUsable = true;
            // 适配器就绪后把占位收起来，露出真正的登录页。
            WebViewPlaceholder.IsVisible = false;
        };
        LoginWebView.AdapterDestroyed += (_, _) => _webViewUsable = false;
        LoginWebView.NavigationCompleted += async (_, _) => await InjectLoginPageStyleAsync();
    }

    /// <summary>
    /// 每次导航完成后注入整理样式，并在随后的几秒内多次补注：
    /// 页面是 SPA，二维码卡片晚于主文档渲染，且整页重渲染可能把
    /// 注入的 style 节点干掉——脚本以「style 标签是否还在」判断，
    /// 被干掉时补注会重新生效。CSS 对后来插入的元素同样适用，
    /// 无需 MutationObserver。
    /// </summary>
    private async Task InjectLoginPageStyleAsync()
    {
        try
        {
            // Android 上扫码无意义（微信就在本机），直接切账号登录；
            // 桌面端沿用 QR 卡片的美化脚本。
            var script = OperatingSystem.IsAndroid() ? AccountModeScript : LoginPageStyleScript;
            await LoginWebView.InvokeScript(script).ConfigureAwait(true);

            foreach (var delay in new[] { 1, 2, 3 })
            {
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(true);
                await LoginWebView.InvokeScript(script).ConfigureAwait(true);
            }

            if (Environment.GetEnvironmentVariable("HWREMINDER_LOGIN_DOM_DUMP") == "1")
            {
                var dump = await LoginWebView.InvokeScript(DomDumpScript).ConfigureAwait(true);
                await File.AppendAllTextAsync(
                    Path.Combine(AppPaths.DataDirectory, "login-dom.txt"),
                    $"--- dump ---\n{dump ?? "(null)"}\n").ConfigureAwait(true);
            }

            // 登录方式探针：用来确认页面到底有没有渲染出「微信快捷登录」这类入口。
            // 只读 DOM，不点击、不请求。
            if (Environment.GetEnvironmentVariable("HWREMINDER_LOGIN_PROBE") == "1")
            {
                var probe = await LoginWebView.InvokeScript(LoginMethodProbeScript).ConfigureAwait(true);
                await File.AppendAllTextAsync(
                    Path.Combine(AppPaths.DataDirectory, "login-probe.txt"),
                    $"--- probe {DateTimeOffset.Now:O} ---\n{probe ?? "(null)"}\n").ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // 注入失败只是视觉退化（页面保留滚动条与默认居中），不影响登录链路。
        }
    }

    private bool _webViewUsable;

    /// <summary>避免重复触发自动检测。</summary>
    private bool _autoLoginStarted;

    /// <summary>
    /// WebView 里还停留在上一次登录的页面，需要重新导航到登录页。
    /// 退出登录后会置位；此时不能显示占位，否则会盖住已有内容。
    /// </summary>
    private bool _needsFreshPage;

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 让 ViewModel 在退出登录时能清掉 WebView 内部的 Cookie。
        MainViewModel.WebViewAccessor = () => LoginWebView;

        if (Vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
        }

        // 登录页出现即自动挂载 WebView 并开始检测：
        // 浏览器控件里可能已经有可用的登录态（用户之前登录过），
        // 此时不该再要求他点一次「扫码登录」——那会让人以为必须重新扫码。
        Avalonia.Threading.Dispatcher.UIThread.Post(AutoStartLogin, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Vm is { } vm) vm.PropertyChanged -= OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.LoginPageOpen)) return;
        if (Vm is not { LoginPageOpen: "ykt" }) return;

        // 重新打开登录覆盖层（例如退出登录后再登录）：允许重新自动检测，
        // 并且不要把占位盖在「仍停留在旧页面的 WebView」上面。
        _autoLoginStarted = false;
        _needsFreshPage = true;

        // 打开覆盖层必须自己把 WebView 挂上来。
        // 启动时 LoginView 已在可视树里，但 OnAttachedToVisualTree 投递的 AutoStartLogin
        // 会因 LoginPageOpen 仍为 null 直接返回（且不置 _autoLoginStarted），
        // 因此「设置 → 账号 → 扫码登录」这条路径此前只有下面那一行隐藏占位：
        // WebViewHost 的 IsVisible 一直停在 XAML 里的 False，
        // 而 WebView2 的原生控件在隐藏状态下照样会把登录页加载出来 ——
        // 结果是页面显示了、原生绘制面却从未按控件边界同步过，二维码不居中。
        ShowWebView();

        // 等布局跑完再启动登录检测：ShowWebView 刚刚才把 Host 置为可见，
        // 同一帧内拿到的 Bounds 还是旧的。
        Avalonia.Threading.Dispatcher.UIThread.Post(AutoStartLogin, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private async void AutoStartLogin()
    {
        if (_autoLoginStarted) return;
        if (Vm is not { } vm || vm.LoginPageOpen != "ykt") return;
        if (vm.ShowCookieInput) return;

        _autoLoginStarted = true;

        // async void：异常逃逸会终止进程（没有全局 UI 异常处理器），必须自己兜住。
        try
        {
            ShowWebView();

            // 供界面调试：HWREMINDER_CLEAR_WEBVIEW_LOGIN=1 时先清掉 WebView 里持久化的
            // 登录态，否则自动检测会直接接管旧会话跳进主界面，永远看不到二维码。
            if (Environment.GetEnvironmentVariable("HWREMINDER_CLEAR_WEBVIEW_LOGIN") == "1")
            {
                // 等适配器就绪后再清，否则 CookieManager 拿不到。
                for (var i = 0; i < 100 && !_webViewUsable; i++)
                    await Task.Delay(100);
                if (_webViewUsable)
                    await YktLoginService.ClearCookiesAsync(LoginWebView);
            }

            // isAutoDetect: true —— 后台检测，不占用按钮的可点状态。
            _ = vm.CompleteLoginAsync(LoginWebView, isAutoDetect: true);
        }
        catch (Exception ex)
        {
            vm.LoginStatusText = $"登录页初始化失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 以编程方式触发「重新检测登录」，与点击按钮走同一条路径。
    /// 供 --logintest 自检复现「自动检测进行中时点按钮」这一场景。
    /// </summary>
    public Task TriggerReDetectAsync() => CompleteLoginFromButtonAsync();

    private async Task CompleteLoginFromButtonAsync()
    {
        if (Vm is not { } vm) return;

        vm.ErrorMessage = string.Empty;
        ShowWebView();

        try
        {
            // isAutoDetect: false —— 用户主动检测，会打断后台自动检测
            // quickProbe:      页面里已经有登录态时直接接管并同步，不必等轮询
            await vm.CompleteLoginAsync(LoginWebView, isAutoDetect: false, quickProbe: true);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage =
                $"浏览器控件初始化失败：{ex.Message}\n" +
                "常见原因是缺少 WebView2 运行时，或当前环境不允许启动浏览器子进程。" +
                "请点「使用 Cookie 登录」。";
            WebViewHost.IsVisible = false;
            WebViewPlaceholder.IsVisible = true;
        }
    }

    private void ShowWebView()
    {
        if (Vm is { } vm) vm.ShowCookieInput = false;
        WebViewHost.IsVisible = true;
        // 适配器创建完成前先显示占位；但若 WebView 里已有内容（例如刚退出登录），
        // 就不要用占位把它盖住。
        WebViewPlaceholder.IsVisible = !_webViewUsable && !_needsFreshPage;

        // 让托管布局重跑一遍，把原生控件按最终边界重新定位。
        // 必要性：WebView2 的原生控件在 IsVisible=False 期间仍会创建并加载页面，
        // 而登录覆盖层是「从隐藏切换为显示」，原生绘制面若不在这次切换后同步，
        // 页面就只会在错误的位置/尺寸上渲染（表现为二维码偏出中间）。
        // 只置 IsVisible 不触发 WebView 自身的重新定位，必须显式失效布局。
        WebViewHost.InvalidateMeasure();
        WebViewHost.InvalidateArrange();
    }

    private async void OnStartLoginClick(object? sender, RoutedEventArgs e)
        => await CompleteLoginFromButtonAsync();

    private void OnToggleCookieInputClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ShowCookieInput = true;
        WebViewHost.IsVisible = false;
        WebViewPlaceholder.IsVisible = false;
        CookieBox.Focus();
    }

    private void OnBackToScanClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ShowCookieInput = false;
        // 只有确认控件可用时才把它重新挂回可视树，
        // 避免在缺 WebView2 的机器上反复触发初始化失败。
        WebViewHost.IsVisible = _webViewUsable;
        WebViewPlaceholder.IsVisible = !_webViewUsable;
    }

    private async void OnUseCookieClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var text = CookieBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            vm.ErrorMessage = "请先粘贴 cookie 内容。";
            return;
        }

        // UseCookieAsync 只接 ArgumentException；解析之外的失败（例如保存会话时的 IO）
        // 会从 async void 处理器逃逸并终止进程。
        try
        {
            await vm.UseCookieAsync(text);
        }
        catch (Exception ex)
        {
            vm.ErrorMessage = $"Cookie 登录失败：{ex.Message}";
        }
    }
}
