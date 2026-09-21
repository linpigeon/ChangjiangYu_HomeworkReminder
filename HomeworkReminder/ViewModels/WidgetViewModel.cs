using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HomeworkReminder.Models;
using HomeworkReminder.Services;

namespace HomeworkReminder.ViewModels;

/// <summary>小组件顶部的分组选项。Icon 是 Segoe Fluent/MDL2 图标字形。</summary>
public sealed record WidgetGroup(string Key, string Label, string Icon);

/// <summary>
/// 桌面小组件的 ViewModel。
/// <para>
/// 数据直接取自 <see cref="MainViewModel"/> 的缓存（不额外请求网络），
/// 因此小组件与主界面永远一致；主界面同步完成后调用 <see cref="Refresh"/> 即可。
/// </para>
/// </summary>
public sealed partial class WidgetViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public WidgetViewModel(MainViewModel main)
    {
        _main = main;

        Groups =
        [
            new WidgetGroup("myday", "我的一天", "\uF185"),    // 太阳
            new WidgetGroup("homework", "作业", "\uF19D"),     // 学士帽
            new WidgetGroup("planned", "计划内", "\uF133"),    // 日历
            new WidgetGroup("all", "所有", "\uF03A"),          // 列表
        ];

        _selectedGroup = Groups.FirstOrDefault(g => g.Key == AppSettings.Current.WidgetGroup) ?? Groups[0];

        // 转发主界面的同步状态：IsSyncing 驱动同步按钮动画，SyncStatus 是页脚的进度文字。
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSyncing))
                OnPropertyChanged(nameof(IsSyncing));
            else if (e.PropertyName == nameof(MainViewModel.SyncStatus))
                OnPropertyChanged(nameof(SyncStatus));
        };
    }

    public IReadOnlyList<WidgetGroup> Groups { get; }

    public ObservableCollection<TodoItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private WidgetGroup _selectedGroup;

    [ObservableProperty]
    private string _emptyText = "没有待办事项";

    /// <summary>同步进行中（转发主界面状态），同步按钮据此旋转。</summary>
    public bool IsSyncing => _main.IsSyncing;

    /// <summary>主界面的同步状态文字（「获取课程…」之类），同步中显示在页脚。</summary>
    public string SyncStatus => _main.SyncStatus;

    /// <summary>发起一次手动同步；在途时主界面会忽略。同步完成后经 Synced 事件刷新列表。</summary>
    public void RequestSync() => _main.RequestManualSync();

    partial void OnSelectedGroupChanged(WidgetGroup value)
    {
        var settings = AppSettings.Current;
        settings.WidgetGroup = value.Key;
        settings.Save();
        Refresh();
    }

    /// <summary>
    /// 按分组 key 切换（供自检使用）。找不到该 key 时保持当前分组不变。
    /// </summary>
    public void SelectGroup(string key)
    {
        var match = Groups.FirstOrDefault(g => g.Key == key);
        if (match is not null) SelectedGroup = match;
    }

    /// <summary>从主界面缓存重建列表。由主界面在同步完成后调用。</summary>
    public void Refresh()
    {
        var pending = _main.WidgetSource
            .Where(i => i.Kind == TodoKind.Announcement || i.Model.Status != TodoStatus.Completed)
            .Where(i => !i.IsDone)                       // 已在应用内勾掉的不再出现在小组件里
            .ToList();

        var query = SelectedGroup.Key switch
        {
            "myday" => pending
                .Where(i => i.Kind == TodoKind.Homework)
                .Where(i => i.IsOverdue || (i.Model.DueAt is { } d && d.Date <= DateTimeOffset.Now.Date))
                .OrderBy(i => i.Model.DueAt ?? DateTimeOffset.MaxValue),

            "planned" => pending
                .Where(i => i.Model.DueAt is not null)
                .OrderBy(i => i.Model.DueAt),

            "homework" => pending
                .Where(i => i.Kind == TodoKind.Homework)
                .OrderBy(i => i.Model.DueAt ?? DateTimeOffset.MaxValue),

            _ => pending.OrderBy(i => i.Model.DueAt ?? DateTimeOffset.MaxValue),
        };

        Items.Clear();
        foreach (var item in query.Take(30)) Items.Add(item);

        EmptyText = SelectedGroup.Key switch
        {
            "myday" => "今天没有到期事项",
            "planned" => "没有带截止时间的事项",
            "homework" => "没有待完成的作业",
            _ => "没有待办事项",
        };
    }

    /// <summary>勾选：复用主界面的本地勾选逻辑，保证两边状态一致。由视图直接调用。</summary>
    public async System.Threading.Tasks.Task ToggleAsync(TodoItemViewModel? item)
    {
        if (item is null) return;
        await _main.ToggleDoneAsync(item).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>在浏览器中打开该事项对应的雨课堂页面。由视图直接调用。</summary>
    public void Open(TodoItemViewModel? item)
    {
        if (item is null) return;
        _main.OpenSource(item);
    }
}
