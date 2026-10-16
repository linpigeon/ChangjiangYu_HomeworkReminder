using System;
using System.Collections.Generic;
using System.Linq;
using HomeworkReminder.Models;

namespace HomeworkReminder.Services;

/// <summary>合并导入的结果统计。</summary>
public sealed record ScheduleMergeResult(int Added, int Updated, int Skipped)
{
    /// <summary>是否有净变更（用于决定要不要落盘与提示）。</summary>
    public bool HasChanges => Added > 0 || Updated > 0;

    public override string ToString() => $"新增 {Added}、更新 {Updated}、跳过 {Skipped}";
}

/// <summary>
/// 课表**合并导入**。
/// <para>
/// 为什么是「合并」而不是「覆盖」：教务系统重新导出时未必包含全部课程
/// （可能只导了一部分、或用户手动补录过课程），覆盖会**静默删掉**这些条目。
/// 因此这里只做增/改，**绝不删除**既有条目。
/// </para>
/// <para>
/// 匹配键为 `课程名|星期|起始节|结束节|起始周|结束周`：
/// 命中且内容有变化 → 沿用既有 Id 更新；命中且完全一致 → 跳过；未命中 → 新增。
/// 一门课在多个时段/多个周次段上课会被拆成多条，这个键能精确区分它们。
/// </para>
/// </summary>
public static class ScheduleMerge
{
    /// <summary>构造匹配键（课程名归一化：去首尾空白 + 转小写，避免大小写/空格导致重复导入）。</summary>
    public static string MatchKey(ScheduleItem item) => string.Join('|',
        item.Name.Trim().ToLowerInvariant(),
        item.Weekday,
        item.StartPeriod,
        item.EndPeriod,
        item.StartWeek,
        item.EndWeek);

    /// <summary>匹配键一致时，判断其余可编辑字段是否也一致。</summary>
    public static bool SameContent(ScheduleItem a, ScheduleItem b) =>
        a.WeekParity == b.WeekParity
        && (a.Teacher ?? string.Empty) == (b.Teacher ?? string.Empty)
        && (a.Location ?? string.Empty) == (b.Location ?? string.Empty)
        && a.StartTime == b.StartTime
        && a.EndTime == b.EndTime;

    /// <summary>
    /// 把解析出的条目合并进既有课表。
    /// <para>
    /// **整批先校验再写入**：任一条目不合法就一条都不写，避免留下「导入了一半」的课表。
    /// 写入中途出错时回滚本次新建的条目（更新既有条目的沿用原 Id，不动其数据）。
    /// </para>
    /// </summary>
    public static (List<ScheduleItem> Items, ScheduleMergeResult Result) Merge(
        IReadOnlyList<ScheduleItem> existing, IReadOnlyList<ScheduleItem> incoming, int term)
    {
        var normalized = incoming
            .Select(i => { i.Term = term != 0 ? term : i.Term; return i; })
            .ToList();

        // 整批先校验：不合法就一条都不写。
        foreach (var item in normalized) ScheduleStore.ValidateOne(item);

        var result = existing.ToList();
        var byKey = new Dictionary<string, ScheduleItem>(StringComparer.Ordinal);
        foreach (var item in result) byKey[MatchKey(item)] = item;

        var added = 0;
        var updated = 0;
        var skipped = 0;
        var createdIds = new List<string>();

        try
        {
            foreach (var item in normalized)
            {
                var key = MatchKey(item);
                if (!byKey.TryGetValue(key, out var hit))
                {
                    if (string.IsNullOrWhiteSpace(item.Id)) item.Id = Guid.NewGuid().ToString("N");
                    result.Add(item);
                    byKey[key] = item;
                    createdIds.Add(item.Id);
                    added++;
                    continue;
                }

                if (SameContent(hit, item))
                {
                    skipped++;
                    continue;
                }

                // 沿用既有 Id 原地更新（保留 Id，避免外部引用失效）。
                item.Id = hit.Id;
                var index = result.IndexOf(hit);
                result[index] = item;
                byKey[key] = item;
                updated++;
            }
        }
        catch
        {
            // 回滚本次新建的条目，避免部分导入。
            foreach (var id in createdIds) result.RemoveAll(i => i.Id == id);
            throw;
        }

        return (result, new ScheduleMergeResult(added, updated, skipped));
    }
}