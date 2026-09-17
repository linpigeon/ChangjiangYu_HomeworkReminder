using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkReminder.Models;

namespace HomeworkReminder.ViewModels;

/// <summary>左侧导航项。</summary>
public sealed partial class NavItem(string key, string label, string glyph, string description) : ViewModelBase
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    /// <summary>Segoe Fluent Icons / Segoe MDL2 Assets 字形。</summary>
    public string Glyph { get; } = glyph;
    public string Description { get; } = description;

    /// <summary>是否是当前选中的导航项（由 MainViewModel 维护）。</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>右侧的计数角标，空字符串表示不显示。</summary>
    [ObservableProperty]
    private string _badgeText = string.Empty;

    public bool HasBadge => !string.IsNullOrEmpty(BadgeText);

    partial void OnBadgeTextChanged(string value) => OnPropertyChanged(nameof(HasBadge));
}

/// <summary>
/// 中间栏的待办列表：持有一个过滤器下的全部事项，并派生计数。
/// </summary>
public sealed partial class TodoListViewModel : ViewModelBase
{
    public ObservableCollection<TodoItemViewModel> Items { get; } = [];

    public ObservableCollection<TodoItemViewModel> VisibleItems { get; } = [];

    /// <summary>勾选后隐藏（仅作业视图有关闭开关时使用）。</summary>
    [ObservableProperty]
    private bool _hideLocallyDone = true;

    [ObservableProperty]
    private string _emptyMessage = "这里没有待办事项。";

    public int Count => VisibleItems.Count;

    /// <summary>列表是否有内容，用于在空列表时隐藏滚动区域、只显示空状态文案。</summary>
    public bool HasItems => VisibleItems.Count > 0;

    public int OverdueCount => VisibleItems.Count(i => i.IsOverdue);

    public int DueTodayCount => VisibleItems.Count(i =>
        i.Model.DueAt is { } d && d.Date == DateTimeOffset.Now.Date);

    public void Replace(IEnumerable<TodoItemViewModel> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        Rebuild();
    }

    public void Rebuild()
    {
        var query = Items.AsEnumerable();
        if (HideLocallyDone) query = query.Where(i => !i.IsDone);

        VisibleItems.Clear();
        foreach (var i in query) VisibleItems.Add(i);

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(OverdueCount));
        OnPropertyChanged(nameof(DueTodayCount));
    }

    partial void OnHideLocallyDoneChanged(bool value) => Rebuild();
}
