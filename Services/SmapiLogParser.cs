using System.Text.RegularExpressions;

namespace StardewToolkit.Services;

/// <summary>SMAPI 日志里的一条消息（多行消息的原始行也算进来）。</summary>
/// <param name="LineNumber">文件里的行号（1 起），用于详情面板定位原文件。</param>
/// <param name="Time">HH:mm:ss 时间戳。</param>
/// <param name="Level">日志级别（TRACE/DEBUG/INFO/ALERT/WARN/ERROR/CRITICAL）。</param>
/// <param name="Source">来源（SMAPI / Game / mod 名称；分屏会带 screen_N 前缀）。</param>
/// <param name="Message">消息全文，含后续堆栈行。</param>
public sealed record SmapiLogEntry(
    int LineNumber,
    string Time,
    string Level,
    string Source,
    string Message)
{
    /// <summary>是否为错误/崩溃级别。</summary>
    public bool IsError => Level is "ERROR" or "CRITICAL";

    /// <summary>来源是不是某个 mod（而不是 SMAPI 本体或游戏本体）。</summary>
    public bool LogSourceIsMod =>
        Source is not { Length: 0 }
        && !Source.StartsWith("SMAPI", StringComparison.OrdinalIgnoreCase)
        && !Source.StartsWith("Game", StringComparison.OrdinalIgnoreCase)
        && !Source.StartsWith("game", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为警告或错误。</summary>
    public bool IsWarnOrAbove => IsError || Level is "WARN" or "ALERT";

    /// <summary>消息的第一行（列表里展示用）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FirstLine => Message.Split('\n')[0].TrimEnd();
}

/// <summary>日志头部解析出来的环境信息。</summary>
public sealed record SmapiLogMeta(
    string? SmapiVersion,
    string? GameVersion,
    string? Platform,
    int? ModCount,
    string? LogStartedAt,
    string? ModsFolder = null);

/// <summary>
/// 解析 SMAPI 日志文件（%APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt）。
///
/// 行格式来自 SMAPI 源码 Framework/Monitor.cs 的 WriteFileLine：
///   [HH:mm:ss LEVEL screen_N? source] message
/// 多行消息（异常堆栈等）是裸行，向上归并到最近一条消息里。
/// </summary>
public static class SmapiLogParser
{
    /// <summary>消息头格式。level 和 source 之间可能有分屏的 screen_N 标记，中间空格数不固定。</summary>
    private static readonly Regex HeadRegex = new(
        @"^\[(?<time>\d{1,2}:\d{2}:\d{2})\s+(?<level>[A-Za-z]+)\s+(?:(?<screen>screen_\d+)\s+)?(?<source>[^\]]+)\]\s?(?<message>.*)$",
        RegexOptions.Compiled);

    /// <summary>启动横幅：SMAPI 版本、游戏版本。</summary>
    private static readonly Regex VersionRegex = new(
        @"SMAPI\s+(?<smapi>\S+)\s+running\s+Stardew Valley\s+(?<game>[\w.\-+]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LoadedRegex = new(
        @"Loaded\s+(?<count>\d+)\s+mods?:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StartedRegex = new(
        @"Log started at\s+(?<time>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>启动横幅里的 Mods 目录提示（SMAPI 启动早期输出）。</summary>
    private static readonly Regex ModsPathRegex = new(
        @" Mods go here:\s*(?<path>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>解析一个日志文件。大文件（几 MB）也就几万行，后台线程一次读完足够快。</summary>
    public static async Task<(SmapiLogMeta Meta, List<SmapiLogEntry> Entries)> ParseFileAsync(
        string path, CancellationToken ct = default)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        return await Task.Run(() => Parse(lines, ct), ct);
    }

    /// <summary>列出日志目录里的 SMAPI 日志文件，新的在前。</summary>
    public static List<FileInfo> GetLogFiles()
    {
        var dir = new DirectoryInfo(GameInspector.ErrorLogDirectory);
        if (!dir.Exists)
            return [];

        return dir.GetFiles("*.txt")
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();
    }

    internal static (SmapiLogMeta Meta, List<SmapiLogEntry> Entries) Parse(
        IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        var meta = new SmapiLogMeta(null, null, null, null, null);
        var entries = new List<SmapiLogEntry>(Math.Min(lines.Count, 1 << 16));

        SmapiLogEntry? current = null;
        for (int i = 0; i < lines.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var line = lines[i];
            var match = HeadRegex.Match(line);

            if (match.Success)
            {
                var level = match.Groups["level"].Value.ToUpperInvariant();
                var source = match.Groups["screen"].Success
                    ? match.Groups["screen"].Value + " " + match.Groups["source"].Value.Trim()
                    : match.Groups["source"].Value.Trim();

                current = new SmapiLogEntry(i + 1, match.Groups["time"].Value, level, source,
                    match.Groups["message"].Value);
                entries.Add(current);
            }
            else if (current is not null)
            {
                // 裸行 = 上一条消息的续行（堆栈、多行消息）
                current = current with { Message = current.Message + "\n" + line };
                entries[^1] = current;
            }
            // 开头连不上任何消息的裸行直接丢弃

            // 头部信息横幅分散在启动阶段的几条消息里，找到了就不再重复找
            if (meta.SmapiVersion is null && VersionRegex.IsMatch(line))
            {
                var v = VersionRegex.Match(line);
                meta = meta with
                {
                    SmapiVersion = v.Groups["smapi"].Value,
                    GameVersion = v.Groups["game"].Value,
                };
            }
            if (meta.ModCount is null)
            {
                var loaded = LoadedRegex.Match(line);
                if (loaded.Success)
                    meta = meta with { ModCount = int.Parse(loaded.Groups["count"].Value) };
            }
            if (meta.LogStartedAt is null)
            {
                var started = StartedRegex.Match(line);
                if (started.Success)
                    meta = meta with { LogStartedAt = started.Groups["time"].Value.Trim() };
            }
            if (meta.ModsFolder is null)
            {
                var modsPath = ModsPathRegex.Match(line);
                if (modsPath.Success)
                    meta = meta with { ModsFolder = modsPath.Groups["path"].Value.Trim() };
            }
        }

        return (meta, entries);
    }
}
