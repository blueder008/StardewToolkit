namespace StardewToolkit.Services;

/// <summary>
/// 本工具自己的操作日志（SMAPI 安装/更新、mod 扫描安装等操作流水）。
/// 写入 %APPDATA%\StardewToolkit\logs\app-yyyyMMdd.log，按天分文件。
/// 供日志查看器的「来源下拉」和排障使用。
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewToolkit", "logs");

    /// <summary>今天的日志文件完整路径。</summary>
    public static string TodayFile => Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>按修改时间倒序列出全部日志（今天的排最前），没有则空列表。</summary>
    public static List<FileInfo> GetFiles()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return [];
            return [.. Directory.GetFiles(LogDirectory, "app-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>追加一行。[HH:mm:ss] 前缀与 SMAPI 日志格式保持一致观感。</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(TodayFile,
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}",
                    System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去不影响功能（盘满/权限等）
        }
    }
}
