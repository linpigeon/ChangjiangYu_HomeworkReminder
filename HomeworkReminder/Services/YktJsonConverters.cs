using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeworkReminder.Services;

// DTO 定义见 YktDtos.cs；本文件只放容错用的自定义转换器与进度载荷 DTO。
// 序列化元数据统一由源生成的 AppJsonContext 提供（见 AppJsonContext.cs）。

/// <summary>
/// leaf_schedules 的字典值既可能是数字（该 leaf 无作答内容，实测值 <c>1</c>），
/// 也可能是对象。统一映射为 <see cref="YktLeafProgress"/>，数字形态保留为「无进度信息」。
/// </summary>
public sealed class YktLeafProgressConverter : JsonConverter<YktLeafProgress>
{
    public override YktLeafProgress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                reader.GetDouble();
                return new YktLeafProgress();

            case JsonTokenType.String:
                // 少数情况下后端会把数字序列化成字符串
                _ = reader.GetString();
                return new YktLeafProgress();

            case JsonTokenType.Null:
                return new YktLeafProgress();

            case JsonTokenType.StartObject:
            {
                var clone = JsonDocument.ParseValue(ref reader);
                using (clone)
                {
                    var root = clone.RootElement;
                    return new YktLeafProgress
                    {
                        Status = ReadInt(root, "status"),
                        Total = ReadInt(root, "total"),
                        Done = ReadInt(root, "done"),
                        Score = ReadDouble(root, "score"),
                    };
                }
            }

            default:
                reader.Skip();
                return new YktLeafProgress();
        }
    }

    private static int? ReadInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)v.GetDouble(),
            JsonValueKind.String when int.TryParse(v.GetString(), out var s) => s,
            _ => null,
        };
    }

    private static double? ReadDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public override void Write(Utf8JsonWriter writer, YktLeafProgress value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Status is { } s) writer.WriteNumber("status", s);
        if (value.Total is { } t) writer.WriteNumber("total", t);
        if (value.Done is { } d) writer.WriteNumber("done", d);
        if (value.Score is { } sc) writer.WriteNumber("score", sc);
        writer.WriteEndObject();
    }
}

/// <summary>
/// `pub_new_pro` 的 data 载荷。leaf_schedules 是关键字段——完成状态唯一可靠来源。
/// </summary>
public sealed class YktProgressData
{
    [JsonPropertyName("leaf_schedules")]
    public Dictionary<string, YktLeafProgress>? LeafSchedules { get; set; }
}
