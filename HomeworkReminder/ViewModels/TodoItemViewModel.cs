using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkReminder.Models;

namespace HomeworkReminder.ViewModels;

/// <summary>
/// 列表中的一条待办。
/// <para>
/// <see cref="IsDone"/> 是应用内的本地勾选，不代表雨课堂的完成状态——
/// <see cref="Status"/> 才是接口给出的真实进度。两者分开显示，避免把
/// "我勾掉了" 误读成 "雨课堂认为我做完了"。
/// </para>
/// </summary>
public partial class TodoItemViewModel(TodoItem model) : ViewModelBase
{
    public TodoItem Model { get; } = model;

    [ObservableProperty]
    private bool _isDone;

    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(DoneLabel));

    /// <summary>应用内标记的可读文本。</summary>
    public string DoneLabel => IsDone ? "已在应用内标记" : "未标记";

    [ObservableProperty]
    private bool _isSelected;

    public string Id => Model.Id;
    public TodoKind Kind => Model.Kind;
    public string Title => Model.Title;
    public string CourseName => Model.CourseName;
    public string DueText => Model.DueText;
    public string RemainingText => Model.RemainingText;
    public string ProgressText => Model.ProgressText;
    public string StatusText => Model.StatusText;
    public bool IsOverdue => Model.IsOverdue;
    public string? SourceUrl => Model.SourceUrl;
    public long ClassroomId => Model.ClassroomId;

    /// <summary>作业的真实完成状态（来自接口）。</summary>
    public bool IsCompletedOnServer => Model.Status == TodoStatus.Completed;

    /// <summary>是否显示剩余时间。已完成的作业不显示，避免出现「已过期」这种无意义的提示。</summary>
    public bool ShowRemaining => !IsCompletedOnServer;

    public bool ShowRemainingNotOverdue => ShowRemaining && !IsOverdue;

    public bool ShowRemainingOverdue => ShowRemaining && IsOverdue;

    /// <summary>试卷类作业在标题上带「作业」字样，用它选图标。</summary>
    public string Glyph => Kind == TodoKind.Announcement ? "\uF0A1" : "\uF19D";

    /// <summary>未开始 vs 进行中，用于列表里的进度徽标着色。</summary>
    public bool ShowProgress => Kind == TodoKind.Homework && Model.Total is > 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(RemainingText));
    }
}
