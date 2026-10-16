using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace HomeworkReminder.Android;

/// <summary>
/// Avalonia 12 的 Android 应用入口：App 的初始化从 MainActivity 挪到了这里
/// （AvaloniaMainActivity 不再有泛型参数，CustomizeAppBuilder 也移到了这个类上）。
/// </summary>
[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
        AndroidPlatformHooks.Register();
        // 注入移动端外壳（抽屉式布局）：MainView 用它替换共享项目里的桌面三栏外壳。
        // 外壳本身在共享项目里（HomeworkReminder.Views.MobileShellView），
        // 平台的差异通过 IPlatformServices 注入，见 PrepareExamWebSession。
        App.MobileShellFactory = () => new HomeworkReminder.Views.MobileShellView();
        // 平台服务：浏览器打开 + 雨课堂 App 跳转。
        ViewModels.MainViewModel.PlatformServices = new AndroidPlatformServices();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
