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
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
