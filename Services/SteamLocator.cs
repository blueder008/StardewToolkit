using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StardewToolkit.Services;

/// <summary>一个通过 Steam 库找到的游戏目录。</summary>
public sealed record SteamGame(string GamePath, string LibraryPath);

/// <summary>
/// 只处理 Steam 版 Stardew Valley 的安装路径探测：
/// 读取 Steam 注册表 + 解析 libraryfolders.vdf。
/// </summary>
public static class SteamLocator
{
    private const string SteamRegistryKey = @"Software\Valve\Steam";
    private const string StardewFolderName = "Stardew Valley";
    public const string SteamLaunchOptionsUrl = "steam://open/games";

    /// <summary>获取 Steam 的安装根目录（注册表里是正斜杠，这里统一换成反斜杠）。</summary>
    public static string? GetSteamPath()
    {
        using var hkcu = Registry.CurrentUser.OpenSubKey(SteamRegistryKey);
        if (hkcu?.GetValue("SteamPath") is string user && Directory.Exists(user))
            return user.Replace('/', '\\');

        using var hklm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        if (hklm?.GetValue("InstallPath") is string machine && Directory.Exists(machine))
            return machine;

        return null;
    }

    /// <summary>解析所有 Steam 库目录（libraryfolders.vdf）。</summary>
    public static IReadOnlyList<string> GetLibraryPaths(string steamPath)
    {
        var libraries = new List<string>();

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var text = File.ReadAllText(vdfPath, Encoding.UTF8);
            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(UnescapeVdfPath(match.Groups[1].Value));
        }

        // 兜底：vdf 缺失或没写根库
        if (!libraries.Contains(steamPath, StringComparer.OrdinalIgnoreCase))
            libraries.Insert(0, steamPath);

        return libraries;
    }

    /// <summary>在所有 Steam 库里找 Stardew Valley，返回所有有效目录。</summary>
    public static IReadOnlyList<SteamGame> FindInstalls()
    {
        var found = new List<SteamGame>();

        var steamPath = GetSteamPath();
        if (steamPath is null)
            return found;

        foreach (var library in GetLibraryPaths(steamPath))
        {
            var gameDir = Path.Combine(library, "steamapps", "common", StardewFolderName);
            if (!Directory.Exists(gameDir))
                continue; // 库盘可能没挂载，直接跳过
            if (!GameInspector.HasGameFiles(gameDir))
                continue; // 目录存在但不是有效游戏

            found.Add(new SteamGame(gameDir, library));
        }

        // 路径去重（大小写不敏感）
        return found
            .GroupBy(g => Path.GetFullPath(g.GamePath).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string UnescapeVdfPath(string raw) => raw.Replace(@"\\", @"\");
}
