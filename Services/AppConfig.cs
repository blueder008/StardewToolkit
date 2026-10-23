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
