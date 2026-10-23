using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewToolkit.Services;

/// <summary>
/// 调 smapi.io 的第三版更新检查接口（SMAPI 官方 web API）。
///
/// 为什么用它而不是直连 Nexus：它一个请求能查所有 mod，不需要任何 API key，
/// 统一支持 Nexus / GitHub / CurseForge / ModDrop / Xbox 等全部 update key，
/// 还附带官方维护的兼容性状态（SMAPI 维基在持续人工维护这套数据）。
/// Stardrop 和 SMAPI 自己的启动更新检查用的也是这个接口。
/// </summary>
public sealed class SmapiModCheckClient
{
    private static readonly Uri ApiUrl = new("https://smapi.io/api/v3.0/mods");
    private readonly HttpClient _http;

    public SmapiModCheckClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(45);
    }

    /// <summary>
    /// 批量检查 mod 更新。返回字典：key = UniqueID 的小写形式。
    /// 一个可查的条目都没有时直接返回空字典。
    /// </summary>
    /// <param name="apiVersion">已装 SMAPI 版本（决定官方的版本映射建议；空着就不给建议版本，只给最新版本）。</param>
    /// <param name="gameVersion">游戏本体版本。</param>
    /// <param name="mods">要查的 mod（id、已装版本、update keys）。</param>
    public async Task<Dictionary<string, ModUpdateData>> CheckAsync(
        string? apiVersion, string? gameVersion,
        IReadOnlyList<(string Id, string InstalledVersion, IReadOnlyList<string> UpdateKeys)> mods,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (mods.Count == 0)
            return [];

        var body = new WebSearchData
        {
            Mods = mods.Select(m => new WebSearchEntry
            {
                Id = m.Id,
                InstalledVersion = string.IsNullOrWhiteSpace(m.InstalledVersion) ? null : m.InstalledVersion,
                UpdateKeys = m.UpdateKeys.Count > 0 ? m.UpdateKeys.ToArray() : [],
            }).ToArray(),
            IncludeExtendedMetadata = true,
            ApiVersion = string.IsNullOrWhiteSpace(apiVersion) ? null : apiVersion,
            GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion,
            Platform = "Windows",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.UserAgent.ParseAdd($"StardewToolkit/{AppVersion} (+local tool)");
        request.Headers.TryAddWithoutValidation("Application-Name", "StardewToolkit");
        request.Headers.TryAddWithoutValidation("Application-Version", AppVersion);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, WebJsonOptions.WriteOptions), Encoding.UTF8, "application/json");

        log?.Invoke($"向 smapi.io 查询 {mods.Count} 个 mod 的更新状态…");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(ct);
        var entries = await JsonSerializer.DeserializeAsync<List<WebEntry>>(stream, WebJsonOptions.ReadOptions, ct)
                      ?? [];

        var result = new Dictionary<string, ModUpdateData>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            var suggested = entry.SuggestedUpdate;
            var metadata = entry.Metadata;
            result[entry.Id] = new ModUpdateData(
                SuggestedVersion: suggested?.Version,
                SuggestedUrl: suggested?.Url,
                LatestVersion: metadata?.Main?.Version,
                CompatibilityStatus: metadata?.CompatibilityStatus,
                CompatibilitySummary: metadata?.CompatibilitySummary,
                Errors: entry.Errors ?? []);
        }

        log?.Invoke($"smapi.io 返回了 {result.Count} 条结果。");
        return result;
    }

    /// <summary>和程序集版本一致的应用版本号。</summary>
    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0";

    /*********
    ** 接口契约模型
    *********/
    private sealed class WebSearchData
    {
        [JsonPropertyName("Mods")] public WebSearchEntry[] Mods { get; set; } = [];
        [JsonPropertyName("IncludeExtendedMetadata")] public bool IncludeExtendedMetadata { get; set; }
        [JsonPropertyName("ApiVersion")] public string? ApiVersion { get; set; }
        [JsonPropertyName("GameVersion")] public string? GameVersion { get; set; }
        [JsonPropertyName("Platform")] public string Platform { get; set; } = "Windows";
    }

    private sealed class WebSearchEntry
    {
        [JsonPropertyName("Id")] public string Id { get; set; } = "";
        [JsonPropertyName("InstalledVersion")] public string? InstalledVersion { get; set; }
        [JsonPropertyName("UpdateKeys")] public string[] UpdateKeys { get; set; } = [];
    }

    private sealed class WebEntry
    {
        [JsonPropertyName("ID")] public string? Id { get; set; }
        [JsonPropertyName("SuggestedUpdate")] public WebSuggestedUpdate? SuggestedUpdate { get; set; }
        [JsonPropertyName("Metadata")] public WebMetadata? Metadata { get; set; }
        [JsonPropertyName("Errors")] public string[]? Errors { get; set; }
    }

    private sealed class WebSuggestedUpdate
    {
        [JsonPropertyName("Version")] public string? Version { get; set; }
        [JsonPropertyName("Url")] public string? Url { get; set; }
    }

    private sealed class WebMetadata
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Main")] public WebSuggestedUpdate? Main { get; set; }
        [JsonPropertyName("Unofficial")] public WebSuggestedUpdate? Unofficial { get; set; }
        [JsonPropertyName("CompatibilityStatus")] public string? CompatibilityStatus { get; set; }
        [JsonPropertyName("CompatibilitySummary")] public string? CompatibilitySummary { get; set; }
    }

    /// <summary>
    /// smapi.io 的字段大小写历史上换过，读写都用宽容选项，别再把 API 升级当成故障源。
    /// 请求体字段名按 Stardrop 已验证可用的 PascalCase 发送，同时序列化时显式指定名称。
    /// </summary>
    private static class WebJsonOptions
    {
        public static readonly JsonSerializerOptions WriteOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };
    }
}
