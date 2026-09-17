using System;
using System.IO;

namespace HomeworkReminder.Services;

/// <summary>
/// 应用数据的存放位置。
/// <para>
/// 默认在 <c>%LOCALAPPDATA%\HomeworkReminder</c>。可以用环境变量
/// <c>HOMEWORKREMINDER_DATA_DIR</c> 覆盖——受限环境（例如把应用跑在沙箱里）
/// 常常不允许写入 AppData，此时退回到临时目录，而不是直接崩溃。
/// </para>
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvironmentVariable = "HOMEWORKREMINDER_DATA_DIR";

    private static readonly Lazy<string> LazyDataDirectory = new(Resolve);

    public static string DataDirectory => LazyDataDirectory.Value;

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && TryPrepare(overridden, out var custom))
            return custom;

        var preferred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeworkReminder");
        if (TryPrepare(preferred, out var ok)) return ok;

        // 兜底：临时目录。数据在重启后可能丢失，但应用仍可用。
        var fallback = Path.Combine(Path.GetTempPath(), "HomeworkReminder");
        if (TryPrepare(fallback, out var temp)) return temp;

        // 连临时目录都写不了时，至少要有一个不会抛异常的路径。
        return preferred;
    }

    private static bool TryPrepare(string dir, out string resolved)
    {
        resolved = dir;
        try
        {
            Directory.CreateDirectory(dir);
            // CreateDirectory 对已存在的只读目录不会报错，所以再实际探测一次可写性。
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
