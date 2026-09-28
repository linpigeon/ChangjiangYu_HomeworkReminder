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
    /// 考试 / 试卷类作业（type=5 的日志）的直达页。
    /// <paramref name="examId"/> 传日志的 <c>courseware_id</c>——2026-09-28 用户实测
    /// <c>/v2/web/exam/26109213/2144775</c> 可直达答题页（2144775 = 日志 courseware_id）。
    /// </summary>
    public static string Exam(long classroomId, long examId)
        => $"{Base}/v2/web/exam/{classroomId}/{examId}";

    /// <summary>
    /// 章节作业（type=19 的日志）的直达页。
    /// 路由参数名是 exercise_id，实测对应日志 <c>content.leaf_type_id</c>：
    /// 用 <c>get_exercise_detail/{id}/</c> 逐一试出来的——章节树节点 id 与
    /// courseware_id 都返回 Permission Denied，只有 leaf_type_id 有数据（2026-09-28）。
    /// </summary>
    public static string Exercise(long classroomId, long leafTypeId, long skuId)
        => $"{Base}/v2/web/exercise/{classroomId}/{leafTypeId}/{skuId}";

    /// <summary>平台标识。实测前端默认取 3。</summary>
    public const int PlatformId = 3;
}
