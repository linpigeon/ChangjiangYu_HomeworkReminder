using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeworkReminder.Services;

/// <summary>
/// 容错的数字包装。
/// <para>
/// 雨课堂的数值字段类型并不稳定：同一个字段在不同接口、不同课程上可能是
/// 数字、数字字符串、空串、null，甚至 "<c>2026-09-17 10:20</c>" 这样的时间串。
/// 直接声明成 <c>long?</c> 会在这些形态上抛 <see cref="JsonException"/>，
/// 而一个字段解析失败会连带整门课程的日志全部丢弃。
/// </para>
/// <para>
/// 这里统一用一个宽松的读取器接住，由调用方决定拿不到时如何降级。
/// </para>
/// </summary>
[JsonConverter(typeof(YktNumberConverter))]
public readonly struct YktNumber
{
    private readonly long? _value;

    private YktNumber(long? value) => _value = value;

    /// <summary>解析成功时给出毫秒时间戳，否则为 null。</summary>
    public long? Milliseconds => _value;

    /// <summary>是否成功解析。</summary>
    public bool HasValue => _value.HasValue;

    /// <summary>取值为毫秒时间戳；缺失时返回 0，便于直接参与比较。</summary>
    public long OrZero => _value ?? 0;

    public static YktNumber From(long? value) => new(value);

    public override string ToString() => _value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <inheritdoc />
public sealed class YktNumberConverter : JsonConverter<YktNumber>
{
    public override YktNumber Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var l)) return YktNumber.From(l);
                return YktNumber.From((long)reader.GetDouble());

            case JsonTokenType.String:
            {
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text)) return YktNumber.From(null);
                var trimmed = text.Trim();

                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    return YktNumber.From(parsed);

                // 少数接口会把数字写成小数形式。
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return YktNumber.From((long)d);

                // 有些字段会给日期时间串；尝试转成毫秒时间戳，实在不行就当缺失。
                if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return YktNumber.From(new DateTimeOffset(dt, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds());

                return YktNumber.From(null);
            }

            case JsonTokenType.True:
                return YktNumber.From(1);

            case JsonTokenType.False:
                return YktNumber.From(0);

            case JsonTokenType.Null:
            default:
                reader.Skip();
                return YktNumber.From(null);
        }
    }

    public override void Write(Utf8JsonWriter writer, YktNumber value, JsonSerializerOptions options)
    {
        if (value.Milliseconds is { } v) writer.WriteNumberValue(v);
        else writer.WriteNullValue();
    }
}
