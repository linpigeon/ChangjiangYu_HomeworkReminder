using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeworkReminder.Services;

/// <summary>列表视图里的单个课块（字段自带标签，无需启发式分块）。</summary>
public sealed record ListViewBlock(
    int Weekday,
    string PeriodText,
    string Name,
    string WeekText,
    string TeacherText,
    string LocationText);

/// <summary>
/// 正方教务课表页的轻量 HTML 抽取。
/// <para>
/// 只面向课表页的 <c>&lt;table&gt;</c> 与列表视图做「够用」的扫描，
/// **不是**通用 HTML 解析器。刻意不引入 HTML 解析库：
/// 抽取目标只有两种固定结构，正则足够，且免掉一个运行时依赖。
/// </para>
/// <para>
/// 移植自 UniversityReminder 的 <c>zhengfangHtml.ts</c>，口径保持一致，
/// 以便两边的解析结果可以互相对照。
/// </para>
/// </summary>
public static partial class ZhengfangHtml
{
    /// <summary>课表文本里可能出现的命名实体。</summary>
    private static readonly Dictionary<string, string> NamedEntities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amp"] = "&",
        ["lt"] = "<",
        ["gt"] = ">",
        ["quot"] = "\"",
        ["apos"] = "'",
        ["nbsp"] = " ",
        ["ensp"] = " ",
        ["emsp"] = " ",
        ["middot"] = "·",
        ["hellip"] = "…",
        ["mdash"] = "—",
    };

    [GeneratedRegex(@"&(#x?[0-9a-fA-F]+|[a-zA-Z]+);")]
    private static partial Regex EntityRegex();

    [GeneratedRegex(@"<script\b[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style\b[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"</(?:p|div|li|h[1-6]|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[\t\f\v\u00a0\u3000]+")]
    private static partial Regex InlineSpaceRegex();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex RepeatedSpaceRegex();

    [GeneratedRegex(@"<tbody\b[^>]*id=""xq_([1-7])""[^>]*>([\s\S]*?)</tbody>", RegexOptions.IgnoreCase)]
    private static partial Regex TbodyRegex();

    [GeneratedRegex(@"<tr\b[^>]*>([\s\S]*?)</tr>", RegexOptions.IgnoreCase)]
    private static partial Regex TrRegex();

    [GeneratedRegex(@"id=""jc_\d+-(\d+-\d+)""")]
    private static partial Regex PeriodByIdRegex();

    [GeneratedRegex(@"class=""festival""[^>]*>([^<]+)<")]
    private static partial Regex PeriodByFestivalRegex();

    [GeneratedRegex(@"<div\b[^>]*class=""[^""]*timetable_con[^""]*""[^>]*>([\s\S]*?)</div>", RegexOptions.IgnoreCase)]
    private static partial Regex ConBlockRegex();

    [GeneratedRegex(@"class=""title""[^>]*>([\s\S]*?)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex ListTitleRegex();

    [GeneratedRegex(@"<table\b[^>]*>([\s\S]*?)</table>", RegexOptions.IgnoreCase)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"<t[dh]\b([^>]*)>([\s\S]*?)</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"rowspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RowSpanRegex();

    [GeneratedRegex(@"colspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ColSpanRegex();

    /// <summary>实体解转义：命名实体 + 数字实体（十进制 / 十六进制）。</summary>
    public static string DecodeEntities(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return EntityRegex().Replace(text, m =>
        {
            var body = m.Groups[1].Value;
            if (body.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(body.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hex)
                    && hex is >= 0 and <= 0x10FFFF
                    ? char.ConvertFromUtf32(hex)
                    : m.Value;
            }
            if (body.StartsWith('#'))
            {
                return int.TryParse(body.AsSpan(1), out var dec) && dec is >= 0 and <= 0x10FFFF
                    ? char.ConvertFromUtf32(dec)
                    : m.Value;
            }
            return NamedEntities.TryGetValue(body, out var named) ? named : m.Value;
        });
    }

    /// <summary>
    /// 去标签 + 实体解转义 + 行内空白归一。
    /// <para>
    /// **刻意保留换行**：课表单元格用 <c>&lt;br&gt;</c> 分隔「课程名 / 教师 / 周次 / 地点」多行，
    /// 丢掉换行就没法按行切分课块了。同时去空行、压缩行内连续空白。
    /// </para>
    /// </summary>
    public static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var s = ScriptRegex().Replace(html, " ");
        s = StyleRegex().Replace(s, " ");
        s = BrRegex().Replace(s, "\n");
        s = BlockEndRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, " ");
        s = DecodeEntities(s);

        var lines = new List<string>();
        foreach (var raw in s.Split('\n'))
        {
            var line = InlineSpaceRegex().Replace(raw, " ");
            line = RepeatedSpaceRegex().Replace(line, " ").Trim();
            if (line.Length > 0) lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    /// <summary>从课块 HTML 中按 <c>标签：值</c> 抽取字段（值到下一个空白或标签为止）。</summary>
    private static string PickListField(string blockHtml, string label)
    {
        var m = Regex.Match(blockHtml, label + @"\s*[：:]\s*([^<\s]+)");
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>
    /// 抽取正方「列表视图」（<c>kblist_table</c> 的 <c>tbody#xq_N</c>）中的课块。
    /// <para>
    /// 列表视图按星期分 tbody、按节次分 tr，每块自带「周数：/教师：/上课地点：」标签，
    /// 因此不需要网格视图那种启发式分块。同一节次的 td 带 rowspan 时，后续行继承节次。
    /// </para>
    /// </summary>
    public static List<ListViewBlock> ExtractListViewBlocks(string? html)
    {
        var result = new List<ListViewBlock>();
        if (string.IsNullOrEmpty(html)) return result;

        foreach (Match tbody in TbodyRegex().Matches(html))
        {
            if (!int.TryParse(tbody.Groups[1].Value, out var weekday) || weekday is < 1 or > 7) continue;
            var body = tbody.Groups[2].Value;
            var lastPeriod = string.Empty;

            foreach (Match row in TrRegex().Matches(body))
            {
                var rowHtml = row.Groups[1].Value;
                var periodMatch = PeriodByIdRegex().Match(rowHtml);
                if (!periodMatch.Success) periodMatch = PeriodByFestivalRegex().Match(rowHtml);
                if (periodMatch.Success) lastPeriod = periodMatch.Groups[1].Value.Trim();

                if (lastPeriod.Length == 0) continue;

                foreach (Match cell in ConBlockRegex().Matches(rowHtml))
                {
                    var cellHtml = cell.Groups[1].Value;
                    var weekText = PickListField(cellHtml, "周数");
                    if (weekText.Length == 0) continue;

                    var titleMatch = ListTitleRegex().Match(cellHtml);
                    result.Add(new ListViewBlock(
                        Weekday: weekday,
                        PeriodText: lastPeriod,
                        Name: titleMatch.Success ? StripTags(titleMatch.Groups[1].Value).Trim() : string.Empty,
                        WeekText: weekText,
                        TeacherText: PickListField(cellHtml, "教师"),
                        LocationText: PickListField(cellHtml, "上课地点")));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 抽取全部 <c>&lt;table&gt;</c> 的行/单元格文本，结构为 <c>[表][行][列]</c>。
    /// <para>
    /// <b>会展开 rowspan / colspan</b>：真实课表用 rowspan 做布局
    /// （如「上午」占两行），不展开就会让后续行的列号整体前移，
    /// 结果把课挂到错误的星期上。展开后每个逻辑列都有自己的位置，列号才与表头对得上。
    /// </para>
    /// <para>嵌套表格（课表页一般没有）按最内层切分，属已知限制。</para>
    /// </summary>
    public static List<List<List<string>>> ExtractTableRows(string? html)
    {
        var tables = new List<List<List<string>>>();
        if (string.IsNullOrEmpty(html)) return tables;

        foreach (Match table in TableRegex().Matches(html))
        {
            var rows = new List<List<string>>();
            // 每一列还被上面多少行占用（rowspan 的剩余行数）。
            var carry = new List<int>();

            foreach (Match row in TrRegex().Matches(table.Groups[1].Value))
            {
                var cells = new List<string>();
                var col = 0;

                // 行首先占掉被上面 rowspan 覆盖的列。
                while (col < carry.Count && carry[col] > 0)
                {
                    cells.Add(string.Empty);
                    carry[col]--;
                    col++;
                }

                foreach (Match cell in CellRegex().Matches(row.Groups[1].Value))
                {
                    while (col < carry.Count && carry[col] > 0)
                    {
                        cells.Add(string.Empty);
                        carry[col]--;
                        col++;
                    }

                    var attrs = cell.Groups[1].Value;
                    var text = StripTags(cell.Groups[2].Value);

                    var rowSpan = 1;
                    var rsMatch = RowSpanRegex().Match(attrs);
                    if (rsMatch.Success && int.TryParse(rsMatch.Groups[1].Value, out var rs) && rs > 1) rowSpan = rs;

                    var colSpan = 1;
                    var csMatch = ColSpanRegex().Match(attrs);
                    if (csMatch.Success && int.TryParse(csMatch.Groups[1].Value, out var cs) && cs > 1) colSpan = cs;

                    while (carry.Count < col + colSpan) carry.Add(0);
                    for (var k = 0; k < colSpan; k++)
                    {
                        cells.Add(k == 0 ? text : string.Empty);
                        carry[col] = rowSpan - 1;   // 本行已占一次，余下 rowSpan-1 行继续占位
                        col++;
                    }
                }

                if (cells.Count > 0) rows.Add(cells);
            }
            if (rows.Count > 0) tables.Add(rows);
        }
        return tables;
    }
}
