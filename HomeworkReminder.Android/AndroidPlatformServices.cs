using System;
using Android.App;
using Android.Content;
using HomeworkReminder.Services;

namespace HomeworkReminder.Android;

/// <summary>
/// Android 平台的 <see cref="IPlatformServices"/> 实现。
/// </summary>
internal sealed class AndroidPlatformServices : IPlatformServices
{
    /// <summary>雨课堂（长江雨课堂）App 的包名。</summary>
    private const string YktPackage = "com.xuetangx.ykt";

    private static Context Ctx => Application.Context;

    public void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
        }
        catch (Exception)
        {
            // 打不开浏览器不影响应用其它功能。
        }
    }

    /// <summary>
    /// 拉起雨课堂 App。雨课堂只注册了 <c>app://yuketang</c> 这一个深链入口
    /// （manifest 里没有 https applinks）；试卷类作业的路由参数按它推送通知的
    /// 内部路由格式附带（exam?classroomId=..&amp;examId=..）——当前版本实测只解析不跳转，
    /// 参数为前向兼容保留，App 忽略时会落在首页。
    /// </summary>
    public bool OpenInYktApp(string url)
    {
        try
        {
            var deepLink = "app://yuketang";
            // https://changjiang.yuketang.cn/v2/web/exam/{classroomId}/{examId}
            var m = System.Text.RegularExpressions.Regex.Match(
                url, @"/v2/web/exam/(\d+)/(\d+)");
            if (m.Success)
            {
                deepLink = $"app://yuketang/exam?classroomId={m.Groups[1].Value}&examId={m.Groups[2].Value}";
            }

            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(deepLink));
            intent.SetPackage(YktPackage);
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
            return true;
        }
        catch (Exception)
        {
            // 未安装雨课堂 / 其它异常：调用方回落浏览器。
            return false;
        }
    }
}
