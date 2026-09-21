using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeworkReminder.Services;

/// <summary>`GET /v2/api/web/courses/list?identity=2` 里的单门课程。</summary>
public sealed class YktCourse
{
    [JsonPropertyName("classroom_id")] public long ClassroomId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("term")] public int Term { get; set; }
    [JsonPropertyName("course")] public YktCourseMeta? Course { get; set; }

    public long CourseId => Course?.Id ?? 0;
}

public sealed class YktCourseMeta
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

/// <summary>学习日志条目。作业与公告都由这里发现。</summary>
public sealed class YktActivity
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("courseware_id")] public string? CoursewareId { get; set; }
    [JsonPropertyName("create_time")] public YktNumber CreateTime { get; set; }
    [JsonPropertyName("is_finished")] public bool? IsFinished { get; set; }
    [JsonPropertyName("hasRead")] public bool? HasRead { get; set; }
    [JsonPropertyName("content")] public YktActivityContent? Content { get; set; }
}

public sealed class YktActivityContent
{
    /// <summary>
    /// 章节作业（type=19）自带的截止时间。
    /// 实测类型不稳定（字符串 / 数字 / 空串都有），交给 <see cref="YktNumber"/> 统一读取。
    /// </summary>
    [JsonPropertyName("score_d")] public YktNumber ScoreDeadlineRaw { get; set; }

    [JsonPropertyName("leaf_id")] public YktNumber LeafIdRaw { get; set; }

    /// <summary>叶子 id，缺失时为 null。</summary>
    [JsonIgnore]
    public long? LeafId => LeafIdRaw.Milliseconds;
}

/// <summary>
/// `pub_new_pro` 返回的 leaf_schedules 单值。
/// 实测该字典的值有两种形态：数字 `1`（该 leaf 无作答内容），
/// 或对象 <c>{"status":3,"total":1,"done":1,"score":0}</c>。
/// 特性式挂转换器：源生成（<see cref="AppJsonContext"/>）与反射都会遵守。
/// </summary>
[JsonConverter(typeof(YktLeafProgressConverter))]
public sealed class YktLeafProgress
{
    [JsonPropertyName("status")] public int? Status { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("done")] public int? Done { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }

    /// <summary>该 leaf 是否没有可判定的作答记录。</summary>
    [JsonIgnore]
    public bool IsBareNumber => Status is null && Total is null && Done is null;
}

/// <summary>章节树中的一个叶子（作业/考试挂在叶子上）。</summary>
public sealed class YktLeafInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("leafinfo_id")] public YktNumber LeafInfoIdRaw { get; set; }
    [JsonPropertyName("score_deadline")] public YktNumber ScoreDeadline { get; set; }

    /// <summary>叶子的真实 id，缺失时为 null（此时退回章节项自身的 id）。</summary>
    [JsonIgnore]
    public long? LeafInfoId => LeafInfoIdRaw.Milliseconds;
}

public sealed class YktChapter
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("section_leaf_list")] public List<YktLeafInfo>? SectionLeafList { get; set; }
}

/// <summary>`leaf_info` 返回的作业详情。</summary>
public sealed class YktLeafDetail
{
    [JsonPropertyName("publish_time")] public YktNumber PublishTime { get; set; }
    [JsonPropertyName("score_deadline")] public YktNumber ScoreDeadline { get; set; }
    [JsonPropertyName("is_assessed")] public bool? IsAssessed { get; set; }
    [JsonPropertyName("leaf_type")] public int? LeafType { get; set; }
}

/// <summary>
/// `GET /api/v3/user/basic-info` 返回的当前用户信息。
/// <para>
/// 实测注意：<c>nickname</c> 多为「微信用户」这类占位值（微信登录不提供真实昵称），
/// 真正可读的是 <c>name</c>（实名），因此界面以 <c>name</c> 为准。
/// </para>
/// </summary>
public sealed class YktUserProfile
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("avatar")] public string? Avatar { get; set; }
    [JsonPropertyName("school")] public string? School { get; set; }
    [JsonPropertyName("schoolNumber")] public string? SchoolNumber { get; set; }

    /// <summary>/v/course_meta/user_info 用蛇形命名；只写不入的别名。</summary>
    [JsonPropertyName("school_number")]
    public string? SchoolNumberSnake { set => SchoolNumber = value; }

    [JsonPropertyName("role")] public int? Role { get; set; }

    /// <summary>界面上显示的名字：优先实名，退回昵称，再退回学号。</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name!
        : !string.IsNullOrWhiteSpace(Nickname) ? Nickname!
        : !string.IsNullOrWhiteSpace(SchoolNumber) ? SchoolNumber!
        : "已登录用户";

    /// <summary>
    /// 头像地址。雨课堂返回的是 <c>http://</c>，这里统一提升为 https
    /// （同一域名 https 可用），避免明文加载与潜在的混合内容问题。
    /// 注意实际内容是 JPEG，尽管文件名叫 .png——解码按内容判断，不受影响。
    /// </summary>
    [JsonIgnore]
    public string? AvatarUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Avatar)) return null;
            return Avatar!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + Avatar[7..]
                : Avatar;
        }
    }

    /// <summary>副标题：学校 · 学号。</summary>
    [JsonIgnore]
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(School)) parts.Add(School!);
            if (!string.IsNullOrWhiteSpace(SchoolNumber)) parts.Add(SchoolNumber!);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// `POST pub_new_pro` 的请求体。用具名 DTO 而不是匿名对象：
/// 裁剪发布下匿名对象没有源生成元数据，序列化会崩（见 <see cref="AppJsonContext"/>）。
/// </summary>
public sealed class YktProgressRequest
{
    [JsonPropertyName("classroom_id")] public long ClassroomId { get; set; }
    [JsonPropertyName("cid")] public long Cid { get; set; }
    [JsonPropertyName("term")] public string Term { get; set; } = "latest";
    [JsonPropertyName("uv_id")] public int UniversityId { get; set; }
}

/// <summary>`POST user_read_all` 的请求体。不用匿名对象的原因同上。</summary>
public sealed class YktMarkAllReadRequest
{
    [JsonPropertyName("term")] public string Term { get; set; } = "latest";
    [JsonPropertyName("uv_id")] public int UniversityId { get; set; }
}
