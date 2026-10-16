using System;
using System.Collections.Generic;

namespace HomeworkReminder.Services;

/// <summary>
/// 课程配色：把课程名映射到一组固定的「底色 + 边线 + 文字」。
/// <para>
/// <b>为什么按课程名取色</b>：课表里最有用的一眼信息是「这门课在哪里」——
/// 同一门课（哪怕是不同周次段拆出来的多条）必须是同一个颜色，不同课尽量不同色。
/// 用课程名的稳定哈希取模即可，不需要用户配置，课程改名/重导入也不会串色。
/// </para>
/// <para>
/// 调色板是**手挑**的，不是随机生成：浅色主题下底色要够淡（上面还要压深色文字），
/// 深色主题下要够沉（压浅色文字）。因此两个主题各有一套，而非同一套调透明度——
/// 半透明色叠在深色底上会糊成一团，对比度不可控。
/// </para>
/// <para>
/// 取色只依赖课程名，**同一次运行、跨次运行都稳定**，可离线断言。
/// </para>
/// </summary>
public static class CourseColor
{
    /// <summary>调色板条目数（与下面的表一一对应）。</summary>
    public const int PaletteSize = 8;

    /// <summary>
    /// 浅色主题：<c>(底色, 边线, 文字)</c>。
    /// 底色取同色系的很淡一档，文字取同色系的深一档，保证正文字号 11px 也读得清。
    /// </summary>
    private static readonly (string Fill, string Line, string Text)[] LightPalette =
    [
        ("#E3F0FC", "#7FB2E5", "#14456F"),   // 蓝
        ("#E6F6EC", "#7FC99B", "#14522F"),   // 绿
        ("#FDEDE4", "#E9A97F", "#7A3410"),   // 橙
        ("#F3E9FB", "#B58FE0", "#4A2373"),   // 紫
        ("#FDE9EF", "#E58FAC", "#7A1636"),   // 玫红
        ("#E4F5F6", "#7CC2C6", "#0F4A4E"),   // 青
        ("#FBF3DC", "#D9BC6A", "#5F4A0B"),   // 黄
        ("#ECEEF2", "#9AA6B8", "#2C3542"),   // 灰蓝
    ];

    /// <summary>深色主题：底色要沉、文字要亮，避免半透明叠色导致的糊底。</summary>
    private static readonly (string Fill, string Line, string Text)[] DarkPalette =
    [
        ("#1B3A5C", "#4C8DFF", "#D6E6FF"),
        ("#1B4530", "#4FBF85", "#D3F3E1"),
        ("#4A2E17", "#E0954F", "#FBE4CE"),
        ("#38235C", "#A57BE8", "#E8DBFB"),
        ("#4E1D31", "#E07A9E", "#FBD9E4"),
        ("#123F45", "#4FB3BC", "#D2F1F3"),
        ("#43390F", "#D4B44A", "#F7ECC7"),
        ("#2E3540", "#93A2B8", "#DFE5ED"),
    ];

    /// <summary>一门课的三色。</summary>
    /// <param name="Fill">课块底色。</param>
    /// <param name="Line">边线/强调色。</param>
    /// <param name="Text">文字色。</param>
    public readonly record struct Palette(string Fill, string Line, string Text);

    /// <summary>
    /// 取课程配色。
    /// </summary>
    /// <param name="courseName">课程名（空则用「未知课程」占位，保证仍有稳定颜色）。</param>
    /// <param name="dark">是否深色主题。</param>
    public static Palette For(string? courseName, bool dark)
    {
        var table = dark ? DarkPalette : LightPalette;
        return ToPalette(table[IndexOf(courseName)]);
    }

    /// <summary>课程名 → 调色板下标（稳定、与运行环境无关）。</summary>
    public static int IndexOf(string? courseName)
    {
        var name = (courseName ?? string.Empty).Trim();
        if (name.Length == 0) return 0;

        // FNV-1a：实现简单、分布够匀，且**不依赖 string.GetHashCode()**
        // ——后者在 .NET 上每次进程启动都会加盐，跨次运行不稳定，会导致颜色天天变。
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            var hash = offset;
            foreach (var ch in name)
            {
                hash ^= ch;
                hash *= prime;
            }
            return (int)(hash % PaletteSize);
        }
    }

    private static Palette ToPalette((string Fill, string Line, string Text) entry) =>
        new(entry.Fill, entry.Line, entry.Text);

    /// <summary>调色板全部配色（自检用：确认不重复、格式合法）。</summary>
    public static IReadOnlyList<Palette> All(bool dark)
    {
        var table = dark ? DarkPalette : LightPalette;
        var list = new List<Palette>(table.Length);
        foreach (var entry in table) list.Add(ToPalette(entry));
        return list;
    }
}
