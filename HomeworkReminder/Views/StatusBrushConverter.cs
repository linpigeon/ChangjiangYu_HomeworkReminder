using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HomeworkReminder.Views;

/// <summary>
/// <c>bool isError</c> → 前景色。校验失败标红、成功用次级文字色。
/// <para>
/// 用转换器而不是把两套样式都写进 XAML：颜色是跟着主题走的
/// （<c>TodoOverdue</c> / <c>TodoTextSecondary</c> 在深浅主题里各有一套），
/// 在转换器里查一次资源比在模板里挂两套 <c>Classes</c> 更好维护。
/// </para>
/// </summary>
public sealed class StatusBrushConverter : IValueConverter
{
    /// <summary>失败时的画刷（浅色 <c>#C42B1C</c> / 深色 <c>#F1707B</c>）。</summary>
    private static readonly Color ErrorColor = Color.Parse("#C42B1C");

    private static readonly Color ErrorColorDark = Color.Parse("#F1707B");

    private static readonly Color NormalColor = Color.Parse("#5C5C5C");

    private static readonly Color NormalColorDark = Color.Parse("#C8C6C4");

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isError = value is true;
        var dark = Application.Current?.ActualThemeVariant
                   == Avalonia.Styling.ThemeVariant.Dark;
        var color = isError
            ? dark ? ErrorColorDark : ErrorColor
            : dark ? NormalColorDark : NormalColor;
        return new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("状态色是只读展示，不支持反向转换。");
}
