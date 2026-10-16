using System;
using System.Collections.Generic;
using System.Linq;

namespace HomeworkReminder.Services;

/// <summary>
/// 周视图网格的**纯几何计算**：把「第 N 周某天某节次有课」换算成画布上的像素矩形。
/// <para>
/// <b>为什么要有这一层</b>：课表必须是**时间轴**——「1-2 节」的课块要真的占两行高，
/// 第 3 节的课紧接在它下面，而不是各占一个等高的格子。用「每行一个等高单元格 +
/// 课块塞进起始节那一格」的做法无法表达跨节次：格子等高，跨节次的课块就只占一行，
/// 第 2 节的位置留出一个空洞，整张表看起来是断的。
/// </para>
/// <para>
/// 一次性算好绝对坐标（<see cref="BlockRect"/>）后，视图只需要一个 <c>Canvas</c> 摆放，
/// 跨节次、同格多门课（左右分栏）、行高一致这三件事都不必再跟布局控件较劲。
/// </para>
/// <para>
/// 本类不含 I/O、不new任何控件，全部可在 <c>--jsoncheck</c> 里离线断言。
/// </para>
/// </summary>
public static class TimetableLayout
{
    /// <summary>单个节次行的基本高度（与视图里的行高一致）。</summary>
    public const double PeriodRowHeight = 56;

    /// <summary>行间距（视觉分隔，计算块高时必须算进去）。</summary>
    public const double RowGap = 4;

    /// <summary>相邻列之间的水平间距。</summary>
    public const double ColumnGap = 4;

    /// <summary>
    /// 一天的**最小可读宽度**。
    /// <para>
    /// 同一天同一节有多门课时要分栏，栏宽 = 天宽 / 栏数；两边都窄到看不清就没意义了
    /// （实测：4 门课挤一列时每栏不到 30px，课程名断成单字、地点全被截掉）。
    /// 因此天宽不小于这个值，宁可让网格横向滚动。
    /// </para>
    /// </summary>
    public const double MinDayWidth = 116;

    /// <summary>分栏后单栏的最小可读宽度；低于它就让整张网格横向滚动。</summary>
    public const double MinColumnWidth = 78;

    /// <summary>
    /// 相邻两节之间的**行距**（行顶到行顶）。
    /// <para>
    /// 必须含行间距，否则「第 1 节行高 56 + 行间距 4」的下一节会从 56 开始，
    /// 与上一节的底部（56）重合——课块之间无缝，视觉上黏成一片；
    /// 而跨 2 节的课块高 116 又比两节的行距 120 矮，接缝处露出 4px 的空洞。
    /// </para>
    /// </summary>
    public static double RowPitch => PeriodRowHeight + RowGap;

    /// <summary>第 <paramref name="period"/> 节（1 基）行顶部的 Y 坐标。</summary>
    public static double RowTop(int period) => (period - 1) * RowPitch;

    /// <summary>
    /// 跨 <paramref name="periodCount"/> 个节次的行高。
    /// <para>
    /// 用「行距」而不是「行高」累加：跨 2 节的块必须正好占到下一节的行顶
    /// （<c>RowTop(n+1)</c>），否则与下面那节的课之间会露出空缝。
    /// </para>
    /// </summary>
    public static double RowSpanHeight(int periodCount)
    {
        var count = Math.Max(1, periodCount);
        return count * PeriodRowHeight + (count - 1) * RowGap;
    }

    /// <summary>整张网格（节次行部分）的总高度。</summary>
    public static double GridHeight(int periodCount) =>
        periodCount <= 0 ? 0 : RowTop(periodCount) + PeriodRowHeight;

    /// <summary>把天数换算成「一行铺满」所需的宽度。</summary>
    public static double WidthForDays(int dayCount) =>
        Math.Clamp(dayCount, 1, 7) * MinDayWidth + (Math.Clamp(dayCount, 1, 7) - 1) * ColumnGap;

    /// <summary>
    /// 7 天区域实际需要的宽度。
    /// <para>
    /// 取「可用宽度」与「按栏数算出的最小可读宽度」的较大者：
    /// 平时铺满可用宽度，一旦某天挤了多门课就撑宽并交给横向滚动，
    /// 而不是把每一栏压成读不出来的窄条。
    /// </para>
    /// </summary>
    /// <param name="availableWidth">可用宽度（控件宽度 - 节次刻度列）。</param>
    /// <param name="dayCount">显示几天。</param>
    /// <param name="maxColumnsInAnyDay">任一天里的最大分栏数（无课冲突时为 1）。</param>
    public static double RequiredDaysWidth(double availableWidth, int dayCount, int maxColumnsInAnyDay)
    {
        var days = Math.Clamp(dayCount, 1, 7);
        var columns = Math.Max(1, maxColumnsInAnyDay);
        var perDay = Math.Max(MinDayWidth, availableWidth <= 0
            ? MinDayWidth
            : (availableWidth - (days - 1) * ColumnGap) / days);
        var total = days * perDay + (days - 1) * ColumnGap;
        return Math.Max(total, days * (columns * MinColumnWidth + (columns - 1) * ColumnGap) + (days - 1) * ColumnGap);
    }

    /// <summary>一个课块在画布上的矩形（X/Y/宽/高，像素）。</summary>
    /// <param name="X">左边距。</param>
    /// <param name="Y">上边距。</param>
    /// <param name="Width">宽度。</param>
    /// <param name="Height">高度。</param>
    public readonly record struct BlockRect(double X, double Y, double Width, double Height);

    /// <summary>
    /// 课块占用的列区间（同一格里有课冲突时用来分栏）。
    /// <para>惰性计算：先按「天 + 节次区间」分组，再把相交的区间拆成互不重叠的列。</para>
    /// </summary>
    /// <param name="spans">每门课占用的「起始节 / 结束节」。</param>
    /// <returns>与输入等长的数组，每项是「该课占第几列、共分几列」。</returns>
    public static (int Column, int ColumnCount)[] AssignColumns(IReadOnlyList<(int StartPeriod, int EndPeriod)> spans)
    {
        var result = new (int Column, int ColumnCount)[spans.Count];
        if (spans.Count == 0) return result;

        // 按起始节排序，逐个分配最左可用列（区间图着色的贪心，贪心列数即最大重叠数）。
        var order = Enumerable.Range(0, spans.Count)
            .OrderBy(i => spans[i].StartPeriod)
            .ThenBy(i => spans[i].EndPeriod)
            .ToArray();

        var columnEnds = new List<int>();   // 每列已占用到的最大结束节次
        var assigned = new int[spans.Count];

        foreach (var index in order)
        {
            var (start, end) = spans[index];
            var column = -1;
            for (var c = 0; c < columnEnds.Count; c++)
            {
                if (columnEnds[c] < start)
                {
                    column = c;
                    break;
                }
            }

            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(end);
            }
            else
            {
                columnEnds[column] = end;
            }

            assigned[index] = column;
        }

        var columnCount = Math.Max(1, columnEnds.Count);
        for (var i = 0; i < spans.Count; i++) result[i] = (assigned[i], columnCount);
        return result;
    }

    /// <summary>
    /// 把一个课块换算成矩形。
    /// </summary>
    /// <param name="weekday">星期（1 = 周一 … 7 = 周日）。</param>
    /// <param name="startPeriod">起始节次（1 基）。</param>
    /// <param name="endPeriod">结束节次（含）。</param>
    /// <param name="gridWidth">7 天区域的可用宽度。</param>
    /// <param name="column">占第几列（同格多课时分栏）。</param>
    /// <param name="columnCount">该格共分几列。</param>
    /// <param name="dayCount">显示几天（5 = 只显示工作日，7 = 含周末）。</param>
    public static BlockRect Measure(
        int weekday, int startPeriod, int endPeriod, double gridWidth,
        int column = 0, int columnCount = 1, int dayCount = 7)
    {
        var days = Math.Clamp(dayCount, 1, 7);
        var day = Math.Clamp(weekday, 1, days);
        var from = Math.Max(1, Math.Min(startPeriod, endPeriod));
        var to = Math.Max(from, endPeriod);

        var dayWidth = (gridWidth - (days - 1) * ColumnGap) / days;
        var slotWidth = Math.Max(1, (dayWidth - (columnCount - 1) * ColumnGap) / columnCount);
        var index = Math.Clamp(column, 0, Math.Max(0, columnCount - 1));

        return new BlockRect(
            X: (day - 1) * (dayWidth + ColumnGap) + index * (slotWidth + ColumnGap),
            Y: RowTop(from),
            Width: slotWidth,
            Height: RowSpanHeight(to - from + 1));
    }

    /// <summary>
    /// 这些课块里用到的最大节次（决定网格要有多少行）；无课块时返回 0。
    /// </summary>
    public static int MaxPeriodOf(IEnumerable<(int StartPeriod, int EndPeriod)> spans) =>
        spans.Select(s => Math.Max(s.StartPeriod, s.EndPeriod)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// 网格实际要画多少行。
    /// <para>
    /// 下限 <paramref name="minimumRows"/>（只画两行会像出错），上限是节次表长度；
    /// 同时跳过末尾所有**停用**节次行，避免课表下方拖出一片空行。
    /// </para>
    /// </summary>
    public static int VisibleRowCount(int maxUsedPeriod, int periodSlotCount, Func<int, bool> isDisabled, int minimumRows = 10)
    {
        if (periodSlotCount <= 0) return 0;

        var last = Math.Clamp(Math.Max(maxUsedPeriod, minimumRows), 1, periodSlotCount);
        while (last > 1 && isDisabled(last)) last--;
        return last;
    }
}
