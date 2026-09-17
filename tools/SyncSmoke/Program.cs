using System.Text;
using HomeworkReminder.Models;
using HomeworkReminder.Services;

// 无界面冒烟测试：走一遍真实的雨课堂接口，打印同步结果。
// 用法：dotnet run --project tools/SyncSmoke [session.json 路径]

Console.OutputEncoding = Encoding.UTF8;

var dataDir = Environment.GetEnvironmentVariable("HOMEWORKREMINDER_DATA_DIR")
              ?? AppPaths.DataDirectory;
var sessionPath = args.Length > 0 ? args[0] : Path.Combine(dataDir, "session.json");

Console.WriteLine($"数据目录 : {dataDir}");
Console.WriteLine($"会话文件 : {sessionPath}");

var store = new SessionStore(dataDir);
var saved = await store.LoadAsync();
if (saved is not { HasValue: true })
{
    Console.WriteLine("没有可用的登录态。先运行 tools/import-session.mjs，或在应用里扫码登录。");
    return 2;
}

var session = saved.ToSession();
Console.WriteLine($"会话     : sessionid …{session.SessionId[^6..]} · term={session.Term} · uv_id={session.UniversityId}");
Console.WriteLine($"过期时间 : {(session.ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "(未知)")}");
if (session.IsExpired)
{
    Console.WriteLine("登录态已过期，需要重新登录。");
    return 2;
}

using var api = new YktApiClient(session);

Console.WriteLine();
Console.WriteLine("=== 开始同步 ===");
var progress = new Progress<string>(s => Console.WriteLine($"  · {s}"));

SyncResult result;
try
{
    result = await new SyncService(api).SyncAsync(progress);
}
catch (YktAuthExpiredException)
{
    Console.WriteLine("登录态已失效（雨课堂返回未授权），需要重新登录。");
    return 3;
}
catch (YktApiException ex)
{
    Console.WriteLine($"同步失败：{ex.Message}（errorCode={ex.ErrorCode?.ToString() ?? "-"}）");
    return 4;
}

Console.WriteLine();
Console.WriteLine("=== 结果 ===");
Console.WriteLine($"课程 {result.CourseCount} 门 · 学习日志 {result.ActivityCount} 条 · " +
                  $"作业 {result.Homework.Count} 项 · 公告 {result.Announcements.Count} 条 · " +
                  $"通知中心未读 {result.UnreadNotificationCount}");

var pending = result.PendingHomework.OrderBy(h => h.DueAt ?? DateTimeOffset.MaxValue).ToList();
Console.WriteLine();
Console.WriteLine($"--- 待完成作业（{pending.Count}）---");
foreach (var h in pending)
{
    var flag = h.IsOverdue ? "已过期" : h.RemainingText;
    Console.WriteLine($"  [{h.StatusText,-4}] {h.ProgressText,-7} {h.DueText,-12} {flag,-14} " +
                      $"{Trim(h.CourseName, 20)} | {h.Title}");
}

var done = result.CompletedHomework.ToList();
Console.WriteLine();
Console.WriteLine($"--- 已完成作业（{done.Count}）---");
foreach (var h in done.Take(20))
{
    Console.WriteLine($"  {h.ProgressText,-7} 成绩 {h.Score?.ToString() ?? "-",-5} {h.DueText,-12} " +
                      $"{Trim(h.CourseName, 20)} | {h.Title}");
}
if (done.Count > 20) Console.WriteLine($"  …另有 {done.Count - 20} 项");

Console.WriteLine();
var unreadAnn = result.Announcements.Where(a => !a.IsRead).ToList();
Console.WriteLine($"--- 未读公告（{unreadAnn.Count} / 共 {result.Announcements.Count}）---");
foreach (var a in result.Announcements.Take(15))
{
    Console.WriteLine($"  [{(a.IsRead ? "已读" : "未读")}] {a.PublishedAt?.ToString("MM-dd HH:mm") ?? "-",-12} " +
                      $"{Trim(a.CourseName, 18)} | {a.Title}");
}

if (result.Warnings.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"--- 警告（{result.Warnings.Count}）---");
    foreach (var w in result.Warnings) Console.WriteLine($"  ! {w}");
}

Console.WriteLine();
Console.WriteLine("=== 用户资料 ===");
var userProfile = await api.GetUserProfileAsync();
if (userProfile is null)
{
    Console.WriteLine("  (未取到 —— 不影响待办同步)");
}
else
{
    Console.WriteLine($"  姓名     : {userProfile.DisplayName}");
    Console.WriteLine($"  昵称     : {userProfile.Nickname ?? "-"}   ← 微信登录多为「微信用户」占位");
    Console.WriteLine($"  学校     : {userProfile.School ?? "-"}");
    Console.WriteLine($"  学号     : {userProfile.SchoolNumber ?? "-"}");
    Console.WriteLine($"  用户 id  : {userProfile.Id ?? "-"}");
    Console.WriteLine($"  头像     : {(userProfile.AvatarUrl is null ? "(无)" : userProfile.AvatarUrl)}");
    Console.WriteLine($"  界面副标题: {userProfile.Subtitle}");
}

Console.WriteLine();
Console.WriteLine("=== 链接构造自检 ===");
var urlFailures = VerifyUrls();
if (urlFailures.Count == 0)
{
    Console.WriteLine("  OK —— 链接均为前端路由表里的真实路径");
    Console.WriteLine($"  示例：{YktUrls.CourseLog(26109238)}");
}
else
{
    foreach (var f in urlFailures) Console.WriteLine("  ✗ " + f);
}

Console.WriteLine();
Console.WriteLine(pending.Count > 0 || unreadAnn.Count > 0
    ? "冒烟测试通过：成功从雨课堂取到待办数据。"
    : "冒烟测试完成：本次没有待办事项。");

return urlFailures.Count == 0 ? 0 : 6;

/// <summary>
/// 校验生成的链接没有拼出不存在的路由。
/// 背景：曾经拼出 /studentLog/{room}/homework 与 /announcement，
/// 两者都不是路由，浏览器打开会落到 /v2/web/errpage。
/// </summary>
static List<string> VerifyUrls()
{
    var failures = new List<string>();

    var course = YktUrls.CourseLog(26109238);
    if (!course.Contains("/v2/web/studentLog/26109238?")) failures.Add($"课程链接路径异常：{course}");
    foreach (var p in new[] { "university_id=", "platform_id=", "classroom_id=" })
    {
        if (!course.Contains(p)) failures.Add($"课程链接缺少必需参数 {p}：{course}");
    }

    // 这些段落不是路由，出现即说明又拼错了。
    foreach (var bad in new[] { "/homework", "/announcement", "/errpage" })
    {
        if (course.Contains(bad)) failures.Add($"课程链接含不存在的路由段 {bad}：{course}");
    }

    var notice = YktUrls.Notice(26109213, 15546425);
    if (!notice.Contains("/v2/web/noticeView/26109213/15546425")) failures.Add($"公告链接异常：{notice}");

    return failures;
}

static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
