using System.Diagnostics;

namespace StardewToolkit.Services;

/// <summary>检查游戏目录的状态：游戏是否有效、SMAPI 是否已装、版本号。</summary>
public static class GameInspector
{
    private const string GameDll = "Stardew Valley.dll";
    private const string GameExe = "Stardew Valley.exe";
    private const string SmapiExe = "StardewModdingAPI.exe";
    private const string SmapiDll = "StardewModdingAPI.dll";
    private const string SmapiInternalDir = "smapi-internal";

    /// <summary>目录里是否存在有效游戏本体。</summary>
    public static bool HasGameFiles(string dir) =>
        File.Exists(Path.Combine(dir, GameDll)) && File.Exists(Path.Combine(dir, GameExe));

    /// <summary>目录里是否已装 SMAPI（exe + smapi-internal 都在才算完整）。</summary>
    public static bool HasSmapiFiles(string dir) =>
        File.Exists(Path.Combine(dir, SmapiExe)) && Directory.Exists(Path.Combine(dir, SmapiInternalDir));

    /// <summary>SMAPI 文件残缺（有 exe 没有 smapi-internal）。</summary>
    public static bool IsSmapiBroken(string dir) =>
        File.Exists(Path.Combine(dir, SmapiExe)) && !Directory.Exists(Path.Combine(dir, SmapiInternalDir));

    /// <summary>读取已装 SMAPI 的版本号。</summary>
    public static Version? GetSmapiVersion(string dir) => ReadFileVersion(Path.Combine(dir, SmapiDll));

    /// <summary>读取游戏本体的版本号。</summary>
    public static Version? GetGameVersion(string dir) => ReadFileVersion(Path.Combine(dir, GameDll));

    /// <summary>进程名匹配。</summary>
    public static bool IsGameRunning()
    {
        foreach (var name in new[] { "Stardew Valley", "StardewModdingAPI" })
        {
            if (Process.GetProcessesByName(name).Length > 0)
                return true;
        }
        return false;
    }

    /// <summary>SMAPI 日志目录（游戏跑过一次才有）。</summary>
    public static string ErrorLogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "ErrorLogs");

    private static Version? ReadFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var info = FileVersionInfo.GetVersionInfo(path);
            // ProductVersion 可能写成 "4.5.2+abc123"，只取前三段
            var raw = info.ProductVersion ?? info.FileVersion;
            var clean = raw?.Split('+', '-')[0].Trim();
            return Version.TryParse(clean, out var version) ? version : null;
        }
        catch
        {
            return null;
        }
    }
}
