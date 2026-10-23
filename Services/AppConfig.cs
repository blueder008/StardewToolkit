using System.Text.Json;

namespace StardewToolkit.Services;

/// <summary>保存到 %APPDATA% 的程序配置。</summary>
public sealed class AppConfig
{
    public string? LastGamePath { get; set; }
    public string? SkippedVersion { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Nexus personal API key（明文存本地，见 README 的限制说明）。</summary>
    public string? NexusApiKey { get; set; }

    /// <summary>上次验证 Nexus API key 时记下的用户名（只用来在界面显示「已连接为 xxx」）。</summary>
    public string? NexusUserName { get; set; }

    /// <summary>
    /// 用户给 mod 写的备注，key = mod 的 UniqueID，value = 备注内容。
    /// 以 UniqueID 为键，mod 改名、换文件夹都不会丢。
    /// </summary>
    public Dictionary<string, string> ModNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取某个 mod 的备注（没有返回 null）。</summary>
    public string? GetNote(string? uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
            return null;
        return ModNotes.TryGetValue(uniqueId, out var note) && !string.IsNullOrWhiteSpace(note) ? note : null;
    }

    /// <summary>写入/清除某个 mod 的备注（传空或空白即清除），并立即落盘。</summary>
    public void SetNote(string? uniqueId, string? note)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
            return;

        if (string.IsNullOrWhiteSpace(note))
            ModNotes.Remove(uniqueId);
        else
            ModNotes[uniqueId] = note.Trim();

        Save();
    }

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewToolkit", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var data = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
                return JsonSerializer.Deserialize<AppConfig>(data) ?? new AppConfig();
            }
        }
        catch
        {
            // 配置坏了就用默认值
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 配置写不进去不致命，忽略
        }
    }
}
