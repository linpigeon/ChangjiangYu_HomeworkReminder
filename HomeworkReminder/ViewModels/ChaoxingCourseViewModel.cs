using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkReminder.Models;

namespace HomeworkReminder.ViewModels;

/// <summary>
/// 「课程」视图里的一行：一门学习通课程 + 它的未完成任务点汇总。
/// <para>
/// 与 <see cref="TodoItemViewModel"/> 分开：任务点是课程维度的进度指标，
/// 没有截止时间、没有本地勾选、也不参与「新作业」判定，混进待办模型会污染那些派生逻辑。
/// </para>
/// </summary>
public sealed partial class ChaoxingCourseViewModel(ChaoxingCourse model) : ViewModelBase
{
    public ChaoxingCourse Model { get; } = model;

    /// <summary>是否是当前选中的课程（高亮用，由 TodoListViewModel 维护）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    public string CourseName => Model.CourseName;

    /// <summary>班级名，用于区分同一门课的多个班（可能为空）。</summary>
    public string ClazzName => Model.ClazzName;

    /// <summary>未完成任务点总数。</summary>
    public int PendingTaskPoints => Model.PendingTaskPoints;

    /// <summary>是否有未完成任务点。</summary>
    public bool HasPending => Model.PendingTaskPoints > 0;

    /// <summary>是否已结课（结课课程不再统计成绩，任务点数固定为 0）。</summary>
    public bool IsRetired => Model.IsRetired;

    /// <summary>任务点数字文本（右侧徽标）。</summary>
    public string PendingText => HasPending ? Model.PendingTaskPoints.ToString() : "—";

    /// <summary>副标题：未完成数量或结课说明。</summary>
    public string SummaryText => HasPending
        ? $"{Model.PendingTaskPoints} 个任务点未完成"
        : IsRetired ? "已结课" : "任务点已全部完成";

    /// <summary>班级名与结课说明的副行（空则不显示）。</summary>
    public string ClazzText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model.ClazzName)) parts.Add(Model.ClazzName);
            if (IsRetired) parts.Add("已结课");
            return string.Join(" · ", parts);
        }
    }

    public bool HasClazzText => ClazzText.Length > 0;

    /// <summary>
    /// 未完成任务点的明细文本（每行「章节号 章节名 +N」），
    /// 供详情栏展示；没有明细时给出说明。
    /// </summary>
    public string LeavesText => Model.Leaves.Count == 0
        ? (IsRetired ? "该课程已结课，不再统计任务点。" : "该课程没有未完成任务点。")
        : string.Join("\n", Model.Leaves.Select(l =>
            string.IsNullOrWhiteSpace(l.ChapterNumber)
                ? $"{l.Name}  +{l.PendingCount}"
                : $"{l.ChapterNumber}  {l.Name}  +{l.PendingCount}"));

    /// <summary>明细条目数（用于详情栏小标题）。</summary>
    public int LeafCount => Model.Leaves.Count;

    /// <summary>详情栏小标题文案。</summary>
    public string LeavesHeader => LeafCount == 0 ? "任务点" : $"未完成任务点（{LeafCount} 处）";
}
