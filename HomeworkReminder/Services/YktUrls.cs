using System;

namespace HomeworkReminder.Services;

/// <summary>
/// 雨课堂网页端链接构造。
/// <para>
/// 这些路径来自前端路由表实测，不要凭直觉拼：
/// </para>
/// <code>
/// /studentLog/:classroomid              name=studentLog-view   课程日志页（作业/公告都在这里）
/// /noticeView/:classroomid/:noticeid    name=notice-view       单条公告
/// /exam/:cid/:eid                       name=exam              考试/试卷类作业
/// /exercise/:classroomId/:leafId/:skuId name=exercise          章节作业
/// </code>
/// <para>
/// 特别注意：<c>/studentLog/{room}/homework</c> 与 <c>/studentLog/{room}/announcement</c>
/// <b>不是路由</b>，直接打开会落到 <c>/v2/web/errpage</c>。
/// </para>
/// </summary>
public static class YktUrls
{
    public const string Base = "https://changjiang.yuketang.cn";

    /// <summary>
    /// 课程日志页（学生视角的课程主页）。作业、公告、课件都从这里进入。
    /// <para>
    /// 查询参数是必需的：前端自己跳转时会带上
    /// <c>university_id</c> / <c>platform_id</c> / <c>classroom_id</c>，
    /// 缺参数时页面无法定位课程。
    /// </para>
    /// </summary>
    /// <param name="universityId">高校 id；传 0（默认）表示取 <see cref="AppSettings.Current"/>。</param>
    public static string CourseLog(long classroomId, int universityId = 0)
    {
        if (universityId == 0) universityId = AppSettings.Current.UniversityId;
        return $"{Base}/v2/web/studentLog/{classroomId}" +
               $"?university_id={universityId}&platform_id={PlatformId}&classroom_id={classroomId}";
    }

    /// <summary>单条公告详情。</summary>
    public static string Notice(long classroomId, long noticeId)
        => $"{Base}/v2/web/noticeView/{classroomId}/{noticeId}?identity=0&type=9";

    /// <summary>
    /// 考试 / 试卷类作业（type=5 的日志）。
    /// <para>
    /// 未验证：<paramref name="examId"/> 应传哪个 id（日志 id 还是试卷 id）没有实测确认，
    /// 因此当前实现不使用它，统一回落到 <see cref="CourseLog"/>。
    /// </para>
    /// </summary>
    public static string Exam(long classroomId, long examId)
        => $"{Base}/v2/web/exam/{classroomId}/{examId}";

    /// <summary>平台标识。实测前端默认取 3。</summary>
    public const int PlatformId = 3;
}
