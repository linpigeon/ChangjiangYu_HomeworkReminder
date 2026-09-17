using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeworkReminder.Services;

/// <summary>
/// 源生成的 JSON 序列化上下文。
/// <para>
/// 为什么需要它：裁剪发布（PublishTrimmed）会移除反射序列化所需的元数据，
/// 反射式 <see cref="JsonSerializer"/> 在运行时直接抛
/// 「Deserialization of types without a parameterless constructor」。
/// 源生成在编译期产出序列化代码，不依赖反射，是裁剪/AOT 环境下唯一可靠的路径。
/// </para>
/// <para>
/// 这里列出的类型覆盖全部序列化点：雨课堂接口 DTO（见 <see cref="YktApiClient"/>）、
/// 会话文件（<see cref="SessionStore"/>）、本地状态（<see cref="LocalStateStore"/>）、
/// 应用设置（<see cref="AppSettings"/>）。新增需要序列化的类型时必须补一条
/// <c>[JsonSerializable]</c>，否则裁剪版会在运行时炸。
/// </para>
/// <para>
/// 容错选项（大小写不敏感、数字可从字符串读出、容忍尾逗号/注释）会被烘进生成的代码，
/// 与历史上反射路径的运行时配置保持一致。<c>WriteIndented</c> 让落盘的
/// session/settings/local-state 保持可读格式；对 POST 请求体只是多几个空格，无害。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
// ---- 雨课堂接口 DTO ----
[JsonSerializable(typeof(List<YktCourse>))]
[JsonSerializable(typeof(YktCourse))]
[JsonSerializable(typeof(List<YktActivity>))]
[JsonSerializable(typeof(YktActivity))]
[JsonSerializable(typeof(YktProgressData))]
[JsonSerializable(typeof(Dictionary<string, YktLeafProgress>))]
[JsonSerializable(typeof(YktLeafDetail))]
[JsonSerializable(typeof(List<YktChapter>))]
[JsonSerializable(typeof(YktChapter))]
[JsonSerializable(typeof(YktUserProfile))]
[JsonSerializable(typeof(YktProgressRequest))]
[JsonSerializable(typeof(YktMarkAllReadRequest))]
// ---- 本地持久化 ----
[JsonSerializable(typeof(SessionFile))]
[JsonSerializable(typeof(SessionStore.ProtectedEnvelope))]
[JsonSerializable(typeof(LocalState))]
[JsonSerializable(typeof(AppSettings))]
// internal：上下文包含 internal 的 SessionStore.ProtectedEnvelope，
// public 上下文会导致生成代码的可访问性不一致（CS0053）。全部使用点都在本程序集内。
internal partial class AppJsonContext : JsonSerializerContext;
