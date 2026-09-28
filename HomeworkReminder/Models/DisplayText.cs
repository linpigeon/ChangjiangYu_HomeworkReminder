using System.Text.RegularExpressions;

namespace HomeworkReminder.Models;

/// <summary>展示层文本清洗。</summary>
public static partial class DisplayText
{
    /// <summary>
    /// 去掉课程名/作业标题开头的学期前缀（「2026秋-」「26-27-1学期 」「26-27-1 」等）。
    /// 这些前缀来自教师的命名习惯，对「这是哪门课」没有信息量，还挤占显示宽度。
    /// 只剥离开头的一段，不动中间内容。
    /// </summary>
    public static string StripTermPrefix(string text)
        => string.IsNullOrEmpty(text) ? text : TermPrefixRegex().Replace(text, "");

    [GeneratedRegex(@"^\s*(\d{4}[春秋]\s*[-—–]?\s*|\d{4}[-–]\d{4}[-–]\d\s*学期\s*|\d{2}[-–]\d{2}[-–]\d\s*(学期)?\s*)",
        RegexOptions.Compiled)]
    private static partial Regex TermPrefixRegex();
}
