using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>正方课表 JSON（<c>xskbcx_cxXsKb.html</c>）的单条记录。</summary>
public sealed class ZhengfangKbRow
{
    /// <summary>课程名称。</summary>
    [JsonPropertyName("kcmc")] public string? Kcmc { get; set; }

    /// <summary>教师姓名。</summary>
    [JsonPropertyName("xm")] public string? Xm { get; set; }

    /// <summary>场地名称（上课地点）。</summary>
    [JsonPropertyName("cdmc")] public string? Cdmc { get; set; }

    /// <summary>星期几（1-7）。</summary>
    [JsonPropertyName("xqj")] public JsonElement Xqj { get; set; }

    /// <summary>星期几名称（如「星期一」）。</summary>
    [JsonPropertyName("xqjmc")] public string? Xqjmc { get; set; }

    /// <summary>节次（如「1-2」）。</summary>
    [JsonPropertyName("jcs")] public string? Jcs { get; set; }

    /// <summary>节次（部分版本用 jc）。</summary>
    [JsonPropertyName("jc")] public string? Jc { get; set; }

    /// <summary>周次（如「1-16周」）。</summary>
    [JsonPropertyName("zcd")] public string? Zcd { get; set; }
}

/// <summary>连续周次段：<c>1-8,10-16周(单)</c> 会拆成若干段。</summary>
public sealed record WeekSegment(int StartWeek, int EndWeek, WeekParity Parity);

/// <summary>节次范围。</summary>
public sealed record PeriodRange(int StartPeriod, int EndPeriod);

/// <summary>解析产物：条目 + 部分失败说明（不因为个别坏记录就整体失败）。</summary>
public sealed record ParsedSchedule(IReadOnlyList<ScheduleItem> Items, IReadOnlyList<string> Warnings);

/// <summary>
/// 正方教务课表解析内核（纯函数、无 I/O、无网络）。
/// <para>
/// 三条输入路径，按可靠性从高到低尝试：
/// </para>
/// <list type="number">
/// <item><b>JSON 直取</b>：接口返回体含 <c>kbList[]</c> → 字段直映射（字段位置明确，最可靠）；</item>
/// <item><b>列表视图</b>：<c>kblist_table</c> 的课块自带「周数/教师/上课地点」标签；</item>
/// <item><b>网格兜底</b>：普通课表 <c>&lt;table&gt;</c> → 表头映射星期、首列取节次、单元格按行切块。</item>
/// </list>
/// <para>
/// **只产出节次**，具体时间由节次时间表推导——这样周视图、待办、提醒三处的时间口径不会分叉。
/// </para>
/// </summary>
public static class ZhengfangParser
{
    private static readonly string[] WeekdayNamesLong = ["星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日"];
    private static readonly string[] WeekdayNamesShort = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>
    /// 周次文字 → 连续段数组。
    /// <para>
    /// 支持 <c>1-16周</c> / <c>1-8,10-16周</c> / <c>1-15周(单)</c> / <c>2-14周(双)</c> / <c>1周</c>，
    /// 也兼容省略「周」字的 <c>1-16</c> / <c>1-8,10-16</c> 与纯数字 <c>5</c>。
    /// </para>
    /// <para>
    /// <b>严格性至关重要</b>：真实课表单元格里混着学号、课程代码、班级、学时学分等附加行，
    /// 它们含有大量「数字-数字」与裸数字。早期版本用「在整行里找 (\d+)-(\d+)」的宽松匹配，
    /// 结果把 <c>(2026-2027-1)-B32211</c> 认成周次、把 <c>理论:42,实践:3</c> 里逗号后的
    /// <c>3</c> 认成「第 3 周」，进而让 <see cref="SplitCellBlocks"/> 在错误的行上切块、
    /// 把整格数据切碎——在真实页面上必现。
    /// </para>
    /// <para>
    /// 因此这里改为：**必须整串消费完**才算周次。先剥掉括号内的节次（如 <c>(1-2节)</c>），
    /// 再逐段解析，任何一段对不上就整体判为「不是周次」。
    /// </para>
    /// </summary>
    public static List<WeekSegment> ParseWeekRange(string? text)
    {
        var segments = new List<WeekSegment>();
        if (string.IsNullOrWhiteSpace(text)) return segments;

        // 剥掉**所有**括号组，并按内容取舍：
        //   含「节」→ 是节次标注（(1-2节)），丢弃；
        //   含「单/双」→ 是单双周标注（(单)），取出参与 parity 判定；
        //   其余（含数字或「周」）→ 是周次本体（(1-16周)），展开回正文。
        // 必须遍历全部而不是只看开头：真实写法是 `(3-4节)1-15周(单)`，
        // 单双周标在**结尾**，只处理开头会把整串判成非法。
        var body = text.Trim();
        var parenParity = WeekParity.Every;
        var guard = 0;
        while (guard++ < 8)
        {
            var open = body.IndexOfAny(['(', '（']);
            if (open < 0) break;
            var close = body.IndexOfAny([')', '）'], open);
            if (close < 0) break;

            var inner = body[(open + 1)..close];
            string replacement;
            if (inner.Contains('节')) replacement = string.Empty;
            else if (inner.Contains('单')) { parenParity = WeekParity.Odd; replacement = string.Empty; }
            else if (inner.Contains('双')) { parenParity = WeekParity.Even; replacement = string.Empty; }
            else replacement = inner.Contains('周') ? inner : inner + "周";

            body = (body[..open] + replacement + body[(close + 1)..]).Trim();
        }

        var parity = parenParity != WeekParity.Every ? parenParity
            : body.Contains('单') ? WeekParity.Odd
            : body.Contains('双') ? WeekParity.Even
            : WeekParity.Every;

        // 去掉所有「周」字后，剩下的必须是纯「数字 / 分隔符」结构，否则不是周次。
        // 注意必须在判定 parity 之后再删。
        var stripped = body.Replace("周", string.Empty);
        if (stripped.Length == 0) return segments;

        foreach (var part in stripped.Split([',', '，', ';', '；', '、'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(part, @"^(\d+)(?:-(\d+))?$");
            if (!m.Success) return [];   // 任一段对不上 → 整串不是周次

            var start = int.Parse(m.Groups[1].Value);
            var end = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : start;
            if (start < 1 || end < start) return [];
            if (start > Timetable.MaxWeek || end > Timetable.MaxWeek) return [];
            segments.Add(new WeekSegment(start, end, parity));
        }
        return segments;
    }

    /// <summary>
    /// 节次文字 → 范围。
    /// <para>
    /// 支持 <c>1-2节</c> / <c>第1-2节</c> / <c>1-2</c> / <c>0102</c>（四位补零，前两位起、后两位止）/ <c>第3节</c>。
    /// </para>
    /// <para>
    /// <b>严格要求「整串就是节次」</b>，不做「在长文本里找一段数字」的宽松匹配：
    /// 课表单元格里混着课程代码（<c>(2026-2027-1)-B32211</c>）这类内容，
    /// 宽松匹配会把 <c>2026-2027</c> 当成节次范围（实测踩到过）。
    /// 节次与周次写在一行的情形（<c>(1-2节)1-14周</c>）交给
    /// <see cref="ExtractPeriodFromMixed"/> 处理。
    /// </para>
    /// </summary>
    public static PeriodRange? ParsePeriodLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 先去掉记号与空白，再剥掉可能存在的括号（节次常被括号包着）。
        var body = new string(text.Where(c => c is not ('第' or '节') && !char.IsWhiteSpace(c)).ToArray());
        body = body.Trim('(', ')', '（', '）');
        if (body.Length == 0) return null;

        int start, end;
        var range = System.Text.RegularExpressions.Regex.Match(body, @"^(\d+)-(\d+)$");
        if (range.Success)
        {
            start = int.Parse(range.Groups[1].Value);
            end = int.Parse(range.Groups[2].Value);
        }
        else if (body.Length == 4 && body.All(char.IsAsciiDigit))
        {
            // 正方部分版本把「1-2 节」写成 0102。
            start = int.Parse(body[..2]);
            end = int.Parse(body[2..]);
        }
        else if (body.All(char.IsAsciiDigit))
        {
            start = end = int.Parse(body);
        }
        else
        {
            // 不是「节次」的形状 —— 明确返回 null，不再猜。
            return null;
        }

        if (start < 1 || end < start || end > Timetable.MaxPeriod) return null;
        return new PeriodRange(start, end);
    }

    /// <summary>
    /// 从**节次与周次写在一起**的文本里提取节次。
    /// <para>
    /// 不少正方版本的单元格把两者挤在同一行，形如
    /// <c>(1-2节)1-14周</c> / <c>（3-4节）1-16周</c> / <c>第1-2节 1-14周</c>。
    /// 这种文本直接丢给 <see cref="ParsePeriodLabel"/> 会失败——它去掉「第/节」后
    /// 剩 <c>1-2)1-14</c>，既不是纯区间也不是纯数字，宽松匹配还会抓到最后一段变成 <c>14-1</c>。
    /// </para>
    /// <para>
    /// 所以这里**先按括号/「节」定位节次片段**（优先括号内），再交给
    /// <see cref="ParsePeriodLabel"/>。拿不到就返回 null，由调用方决定是否回退到行节次。
    /// </para>
    /// </summary>
    public static PeriodRange? ExtractPeriodFromMixed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // ① 括号内的内容优先：`(1-2节)1-14周` → 取「1-2节」
        var paren = System.Text.RegularExpressions.Regex.Match(text, @"[（(]([^）)]*)[）)]");
        if (paren.Success)
        {
            var inner = paren.Groups[1].Value;
            var parsed = ParsePeriodLabel(inner);
            if (parsed is not null) return parsed;
        }

        // ② 退而取「数字-数字节」这一小段：`第1-2节 1-14周` → 「1-2节」
        var withJie = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s*-\s*(\d+)\s*节");
        if (withJie.Success
            && int.TryParse(withJie.Groups[1].Value, out var s1)
            && int.TryParse(withJie.Groups[2].Value, out var e1)
            && s1 >= 1 && e1 >= s1 && e1 <= Timetable.MaxPeriod)
        {
            return new PeriodRange(s1, e1);
        }

        // ③ 单节写法：`第3节 1-14周`
        var single = System.Text.RegularExpressions.Regex.Match(text, @"第\s*(\d+)\s*节");
        if (single.Success
            && int.TryParse(single.Groups[1].Value, out var s2)
            && s2 >= 1 && s2 <= Timetable.MaxPeriod)
        {
            return new PeriodRange(s2, s2);
        }

        return null;
    }

    /// <summary>星期：优先数字（1-7），回退中文名。</summary>
    public static int? ParseWeekday(int? numeric, string? label)
    {
        if (numeric is >= 1 and <= 7) return numeric;

        var text = label?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        var i = Array.IndexOf(WeekdayNamesLong, text);
        if (i >= 0) return i + 1;
        i = Array.IndexOf(WeekdayNamesShort, text);
        return i >= 0 ? i + 1 : null;
    }

    /// <summary>
    /// 由节次时间表推导课块时间；越界回退内置时间表的首末节，并保证 <c>start &lt; end</c>。
    /// </summary>
    private static (string StartTime, string EndTime) DeriveTimes(PeriodRange period, IReadOnlyList<PeriodSlot> periods)
    {
        var startSlot = Timetable.ResolvePeriodTime(periods, period.StartPeriod);
        var endSlot = Timetable.ResolvePeriodTime(periods, period.EndPeriod);
        var first = periods.Count > 0 ? periods[0] : null;

        var startTime = startSlot?.StartTime ?? first?.StartTime ?? "08:00";
        var endTime = endSlot?.EndTime ?? (periods.Count > 0 ? periods[^1].EndTime : "09:40");

        if (string.CompareOrdinal(startTime, endTime) >= 0)
        {
            startTime = first?.StartTime ?? "08:00";
            endTime = first?.EndTime ?? "08:45";
        }
        return (startTime, endTime);
    }

    private static ScheduleItem BuildItem(
        (string Name, string Teacher, string Location, int Weekday, PeriodRange Period) b,
        WeekSegment seg,
        IReadOnlyList<PeriodSlot> periods,
        int term)
    {
        var (startTime, endTime) = DeriveTimes(b.Period, periods);
        return new ScheduleItem
        {
            Name = b.Name,
            Teacher = b.Teacher.Length > 0 ? b.Teacher : null,
            Location = b.Location.Length > 0 ? b.Location : null,
            Weekday = b.Weekday,
            StartPeriod = b.Period.StartPeriod,
            EndPeriod = b.Period.EndPeriod,
            StartWeek = seg.StartWeek,
            EndWeek = seg.EndWeek,
            WeekParity = seg.Parity,
            StartTime = startTime,
            EndTime = endTime,
            Term = term,
        };
    }

    /// <summary>
    /// <c>kbList[]</c>（正方课表 JSON）→ 课表条目。一条记录含不连续周次时**拆为多条**。
    /// </summary>
    public static ParsedSchedule FromKbList(IEnumerable<ZhengfangKbRow>? rows, int term, IReadOnlyList<PeriodSlot>? periods = null)
    {
        var list = periods ?? Timetable.DefaultPeriods;
        var items = new List<ScheduleItem>();
        var warnings = new List<string>();

        var index = 0;
        foreach (var row in rows ?? [])
        {
            index++;
            var name = (row.Kcmc ?? string.Empty).Trim();
            var teacher = (row.Xm ?? string.Empty).Trim();
            var location = (row.Cdmc ?? string.Empty).Trim();

            int? numericWeekday = null;
            if (row.Xqj.ValueKind == JsonValueKind.Number && row.Xqj.TryGetInt32(out var n)) numericWeekday = n;
            else if (row.Xqj.ValueKind == JsonValueKind.String && int.TryParse(row.Xqj.GetString(), out var s)) numericWeekday = s;

            var weekday = ParseWeekday(numericWeekday, row.Xqjmc);
            var period = ParsePeriodLabel(row.Jcs ?? row.Jc);
            var weeks = ParseWeekRange(row.Zcd);

            var problems = new List<string>();
            if (name.Length == 0) problems.Add("课程名为空");
            if (weekday is null) problems.Add("星期无法识别");
            if (period is null) problems.Add("节次无法识别");
            if (weeks.Count == 0) problems.Add("周次无法识别");

            if (problems.Count > 0 || weekday is null || period is null)
            {
                warnings.Add($"「{(name.Length > 0 ? name : $"第 {index} 条")}」{string.Join("、", problems)}");
                continue;
            }

            foreach (var seg in weeks)
                items.Add(BuildItem((name, teacher, location, weekday.Value, period), seg, list, term));
        }
        return new ParsedSchedule(items, warnings);
    }

    /// <summary>列表视图课块（<see cref="ZhengfangHtml.ExtractListViewBlocks"/> 结果）→ 课表条目。</summary>
    public static ParsedSchedule FromListViewBlocks(
        IEnumerable<ListViewBlock> blocks, int term, IReadOnlyList<PeriodSlot>? periods = null)
    {
        var list = periods ?? Timetable.DefaultPeriods;
        var items = new List<ScheduleItem>();
        var warnings = new List<string>();

        foreach (var block in blocks)
        {
            var name = block.Name.Trim();
            var period = ParsePeriodLabel(block.PeriodText);
            var weeks = ParseWeekRange(block.WeekText);
            if (name.Length == 0 || period is null || weeks.Count == 0)
            {
                warnings.Add($"「{(name.Length > 0 ? name : "空课块")}」无法识别周次或节次");
                continue;
            }

            var baseInfo = (name, block.TeacherText.Trim(), block.LocationText.Trim(), block.Weekday, period);
            foreach (var seg in weeks)
                items.Add(BuildItem(baseInfo, seg, list, term));
        }
        return new ParsedSchedule(items, warnings);
    }

    /// <summary>
    /// 单元格多门课：以「周次行」为分块依据。
    /// <para>
    /// 参照实现（Electron 版）用「周次行 + 吞掉其后一行」切块，并假设单元格只有
    /// <c>名称 / 节次周次 / 地点 / 教师</c> 四行。**该假设在真实页面上不成立**：
    /// 珠海科技学院这类页面的单元格含学号、课程代码、班级、考试类型、学时学分、绩点等
    /// 约 12 行，且 <c>理论:42,实践:3</c> 这种行会被误判成周次行，于是单门课也被切成两块
    /// （参照实现在同一份输入上同样切成 2 块，见实现记录）。
    /// </para>
    /// <para>
    /// 因此这里加一条**完整性前置条件**：只有当「当前块已经含上课地点行」时才允许在周次行处切分。
    /// 理由是一门课的名称、节次周次、地点是相连的，见到地点说明这一门课已经收集完整；
    /// 而学号/课程代码/学时这类附加行都在地点之后，不会造成提前切分。
    /// 多门课的单元格里，第二门课的周次行到来时前一块必然已完整，仍然正确切分。
    /// </para>
    /// </summary>
    public static List<List<string>> SplitCellBlocks(string? text)
    {
        var blocks = new List<List<string>>();
        if (string.IsNullOrEmpty(text)) return blocks;

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var current = new List<string>();
        var trailing = 0;

        foreach (var line in lines)
        {
            current.Add(line);
            if (trailing > 0)
            {
                trailing--;
                if (trailing == 0)
                {
                    blocks.Add(current);
                    current = [];
                }
                continue;
            }

            // 周次行 + 当前块已完整（含地点）→ 在这里切开，并把紧随的一行（地点）并入上一块。
            // 未完整时不切：那一行多半是附加信息（学时/学分/学号）而不是新的一门课。
            if (line.Contains('周') && ParseWeekRange(line).Count > 0 && current.Any(LooksLikeLocation))
            {
                trailing = 1;
            }
        }
        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    /// <summary>单课块 → 条目：首行课程名；含「周」的行为周次；含「节」的行为节次；其余按序为教师、地点。</summary>
    private static (List<ScheduleItem> Items, string Warning) BlockToItems(
        List<string> lines, (int Weekday, PeriodRange? FallbackPeriod) basis,
        IReadOnlyList<PeriodSlot> periods, int term)
    {
        var name = (lines.Count > 0 ? lines[0] : string.Empty).Trim();
        var weekLine = string.Empty;
        var periodLine = string.Empty;
        var rest = new List<string>();

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];

            // ① 周次行优先。它一定排在课程名之后、附加信息之前。
            if (weekLine.Length == 0 && line.Contains('周') && ParseWeekRange(line).Count > 0)
            {
                weekLine = line;
                continue;
            }

            // ② 再认节次行，且**必须带明确节次标记**。
            //
            // 这里不能只看 ParsePeriodLabel 能否解析：它把裸数字也当成节次
            // （`ParsePeriodLabel("3")` → 第 3 节，网格首列确实需要这条规则）。
            // 但块内一旦这样放宽，真实单元格后方那些纯数字附加行——学时学分里的
            // 「3」、人数「42」等——就会先被误认成节次行占掉 periodLine，
            // 真正的 `(1-2节)1-14周` 反而再没机会提取，结果全表节次都错成同一个值。
            // 所以块内要求出现「节」「第」或括号这些明确标记。
            if (periodLine.Length == 0 && HasPeriodMarker(line)
                && (ParsePeriodLabel(line) ?? ExtractPeriodFromMixed(line)) is not null)
            {
                periodLine = line;
                continue;
            }

            rest.Add(line);
        }

        var weeks = ParseWeekRange(weekLine);
        // 节次优先取本块自己的节次行；本块没写才回退到行首列（网格视图的常见情形）。
        //
        // 注意这里**必须再从 weekLine 试着提一次节次**：真实页面把两者写在同一行
        // （`(1-2节)1-16周`），而上面的循环已经把这行归给了周次行并 continue 掉，
        // 于是 periodLine 为空、节次永远取不到——实测整页课程全部解析失败的根因就在这里。
        var period = ParsePeriodLabel(periodLine)
                     ?? ExtractPeriodFromMixed(periodLine)
                     ?? ExtractPeriodFromMixed(weekLine)
                     ?? ParsePeriodLabel(weekLine)
                     ?? basis.FallbackPeriod;
        if (name.Length == 0 || weeks.Count == 0 || period is null)
            return ([], $"「{(name.Length > 0 ? name : "空课块")}」无法识别周次或节次");

        // 教师与地点：真实页面里地点是「城市 楼宇房间」的形状，且**地点在教师之前**。
        // 不能简单按「第一行是教师、第二行是地点」取——课程代码、班级、考试类型、
        // 学时学分等附加行会混进来（实测真实页面每格有 15 行左右）。
        var teacher = string.Empty;
        var location = string.Empty;
        foreach (var line in rest)
        {
            if (location.Length == 0 && LooksLikeLocation(line)) { location = line.Trim(); continue; }
            if (teacher.Length == 0) { teacher = line.Trim(); }
        }

        var baseInfo = (name, teacher, location, basis.Weekday, period);

        return (weeks.Select(seg => BuildItem(baseInfo, seg, periods, term)).ToList(), string.Empty);
    }

    /// <summary>
    /// 一行是否带**明确的节次标记**（「节」「第」或括号）。
    /// <para>
    /// 用于块内识别节次行：只有明确标记才算，避免把学时、学分、人数那类裸数字
    /// （如单独一行 <c>3</c>、<c>42</c>）误认成节次。网格首列的节次判定不走这里
    /// （那里可以接受 <c>1-2</c> 甚至 <c>0102</c> 这类无标记写法）。
    /// </para>
    /// </summary>
    private static bool HasPeriodMarker(string line) =>
        line.Contains('节') || line.Contains('第') || line.Contains('(') || line.Contains('（');

    /// <summary>
    /// 判断一行是否像上课地点。
    /// <para>
    /// 真实页面的地点形如 <c>珠海 明德楼阶梯B107</c>、<c>珠海 六教W211</c>：
    /// 以地点词开头（珠海等校区/城市）且**不含数字学分那类纯数字行**的特征。
    /// 这里用「含楼/教/室/馆/场/厅/机房 等场所词」来判定——比按固定行号取稳，
    /// 因为不同学校的附加行数量并不一致。
    /// </para>
    /// </summary>
    private static bool LooksLikeLocation(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        ReadOnlySpan<string> placeWords =
            ["楼", "教", "室", "馆", "场", "厅", "机房", "实验室", "中心", "校区", "阶梯"];
        foreach (var w in placeWords)
        {
            if (line.Contains(w, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// 课表网格 → 条目。
    /// <para>表头含多个「星期…」单元格 → 建立列→星期映射；首列为节次；单元格内按行切块解析。</para>
    /// </summary>
    public static ParsedSchedule FromTableRows(
        IEnumerable<IReadOnlyList<string>> rows, int term, IReadOnlyList<PeriodSlot>? periods = null)
    {
        var list = periods ?? Timetable.DefaultPeriods;
        var items = new List<ScheduleItem>();
        var warnings = new List<string>();

        var rowList = rows.Select(r => r.ToList()).ToList();
        var headerIndex = -1;
        var weekdayByCol = new Dictionary<int, int>();

        for (var i = 0; i < rowList.Count; i++)
        {
            var map = new Dictionary<int, int>();
            for (var col = 0; col < rowList[i].Count; col++)
            {
                var weekday = ParseWeekday(null, rowList[i][col]);
                if (weekday is not null) map[col] = weekday.Value;
            }
            if (map.Count >= 2)
            {
                headerIndex = i;
                weekdayByCol = map;
                break;
            }
        }
        if (headerIndex < 0 || weekdayByCol.Count == 0)
            return new ParsedSchedule(items, ["未识别到课表星期表头"]);

        for (var i = headerIndex + 1; i < rowList.Count; i++)
        {
            var row = rowList[i];
            var rowPeriod = ParsePeriodLabel(row.Count > 0 ? row[0] : null);
            for (var col = 1; col < row.Count; col++)
            {
                if (!weekdayByCol.TryGetValue(col, out var weekday)) continue;
                var cell = row[col];
                if (string.IsNullOrEmpty(cell)) continue;

                foreach (var block in SplitCellBlocks(cell))
                {
                    var (blockItems, warning) = BlockToItems(block, (weekday, rowPeriod), list, term);
                    if (warning.Length > 0) warnings.Add(warning);
                    items.AddRange(blockItems);
                }
            }
        }
        return new ParsedSchedule(items, warnings);
    }

    /// <summary>
    /// 快照文本 → 条目。JSON（含 kbList）优先直取；HTML 页先取列表视图，无则按网格表格兜底。
    /// </summary>
    /// <param name="text">课表页 HTML 或接口返回的 JSON 文本。</param>
    /// <param name="term">学期标识。</param>
    /// <param name="periods">节次时间表；null = 内置默认。</param>
    public static ParsedSchedule ParseSnapshot(string? text, int term, IReadOnlyList<PeriodSlot>? periods = null)
    {
        var raw = UnwrapJsonString(text?.Trim() ?? string.Empty);
        if (raw.Length == 0) return new ParsedSchedule([], []);

        // ① JSON 直取
        if (raw[0] is '{' or '[')
        {
            try
            {
                if (raw[0] == '[')
                {
                    var array = JsonSerializer.Deserialize(raw, AppJsonContext.Default.ListZhengfangKbRow);
                    if (array is not null) return FromKbList(array, term, periods);
                }
                else
                {
                    var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("kbList", out var kbList))
                    {
                        var rows = kbList.Deserialize(AppJsonContext.Default.ListZhengfangKbRow);
                        return FromKbList(rows, term, periods);
                    }
                    // 是 JSON 但没有 kbList：不是课表响应，落到 HTML 路径也不会有结果，直接如实说明。
                    return new ParsedSchedule([], ["课表 JSON 缺少课程列表（kbList）"]);
                }
            }
            catch (JsonException)
            {
                // 形似 JSON 实则不是（例如以 { 开头的脚本片段）→ 继续按 HTML 处理。
            }
        }

        // ② 列表视图
        var blocks = ZhengfangHtml.ExtractListViewBlocks(raw);
        if (blocks.Count > 0) return FromListViewBlocks(blocks, term, periods);

        // ③ 网格兜底
        var merged = new List<ScheduleItem>();
        var allWarnings = new List<string>();
        foreach (var table in ZhengfangHtml.ExtractTableRows(raw))
        {
            var parsed = FromTableRows(table, term, periods);
            merged.AddRange(parsed.Items);
            allWarnings.AddRange(parsed.Warnings);
        }
        if (merged.Count == 0 && allWarnings.Count == 0)
            allWarnings.Add("未能从页面中识别出课表结构，请确认已打开课表查询页（或课表以列表/网格形式显示）");

        return new ParsedSchedule(merged, allWarnings);
    }

    /// <summary>
    /// 拆掉「整段文本被 JSON 字符串包了一层」的壳，还原成真正的 HTML/JSON。
    /// <para>
    /// 为什么解析器也要兜这一道：WebView 的 <c>InvokeScript</c> 会把脚本返回值序列化成
    /// JSON 字符串交出（正常链路由 <c>JwCaptureService</c> 统一拆壳）。但快照文件
    /// （<c>jw-last-capture.html</c>）是**先落盘再解析**的排查素材，用户拿它来离线复现时
    /// 走的是本方法，不该因为少拆一层壳就得出「解析不出来」的错误结论。
    /// </para>
    /// <para>实测依据：真实教务页的快照里 <c>&lt;html</c> 出现 0 次、字面 <c>\u003C</c> 出现 5601 次。</para>
    /// </summary>
    private static string UnwrapJsonString(string raw)
    {
        var value = raw;
        for (var depth = 0; depth < 3; depth++)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;

            string? decoded;
            try
            {
                // 用 JsonDocument 直读第一层字符串：不依赖源生成上下文，也不必先反序列化整个文档。
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind != JsonValueKind.String) return value;
                decoded = doc.RootElement.GetString();
            }
            catch (JsonException)
            {
                return value;
            }

            if (decoded is null || decoded == value) return value;
            value = decoded;
        }

        return value;
    }
}
