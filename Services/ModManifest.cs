using System.Text.Json;
using System.Text.Json.Serialization;

namespace StardewToolkit.Services;

/// <summary>SMAPI mod 的 manifest.json 数据模型（字段与 SMAPI Toolkit 的 Manifest 对应）。</summary>
public sealed class ModManifest
{
    /*********
    ** 序列化字段
    *********/
    /// <summary>Mod 名称。</summary>
    public string Name { get; set; } = "";

    /// <summary>一句话描述。</summary>
    public string Description { get; set; } = "";

    /// <summary>作者。</summary>
    public string Author { get; set; } = "";

    /// <summary>Mod 版本（字符串形式，如 "1.6.15-beta.2"）。</summary>
    public string Version { get; set; } = "";

    /// <summary>全局唯一的 Mod ID（如 SpaceChase0.SpaceCore）。</summary>
    public string UniqueID { get; set; } = "";

    /// <summary>入口 DLL（SMAPI 模组有；内容包没有）。</summary>
    public string? EntryDll { get; set; }

    /// <summary>所属框架（内容包有；SMAPI 模组没有）。两者互斥。</summary>
    public ModManifestPackFor? ContentPackFor { get; set; }

    /// <summary>更新检查键（如 Nexus:10455、GitHub:Pathoschild/StardewMods）。</summary>
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? UpdateKeys { get; set; }

    /// <summary>依赖的其他 Mod。</summary>
    [JsonConverter(typeof(FlexibleDependencyListConverter))]
    public List<ModManifestDependency>? Dependencies { get; set; }

    /*********
    ** 组合字段
    *********/
    /// <summary>这个 manifest 是内容包（有框架没有入口 DLL）。</summary>
    [JsonIgnore]
    public bool IsContentPack =>
        !string.IsNullOrWhiteSpace(ContentPackFor?.UniqueID) && string.IsNullOrWhiteSpace(EntryDll);
}

/// <summary>manifest.json 里 ContentPackFor 字段的模型。</summary>
public sealed class ModManifestPackFor
{
    public string UniqueID { get; set; } = "";

    /// <summary>需要的最低 SMAPI 版本（展示用）。</summary>
    public string? MinimumApiVersion { get; set; }
}

/// <summary>manifest.json 里 Dependencies 字段的模型。</summary>
public sealed class ModManifestDependency
{
    public string UniqueID { get; set; } = "";

    /// <summary>需要的最低版本（展示用）。</summary>
    public string? MinimumVersion { get; set; }

    /// <summary>是否为必需依赖（默认 true）。</summary>
    public bool IsRequired { get; set; } = true;
}

/*********
** 序列化
*********/
/// <summary>
/// SMAPI 生态的 manifest.json 经常不规范：注释、尾逗号、把数组写成单个值都有。
/// 参考官方 Toolkit 和 Stardrop 的做法做宽容解析，别让一个写错字位的 manifest 整页报错。
/// </summary>
public static class ModManifestParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析 manifest 文本，失败返回 null（调用方把 mod 标记为【manifest 无效】）。</summary>
    public static ModManifest? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ModManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>从文件解析 manifest，文件不存在或解析失败都返回 null。</summary>
    public static async Task<ModManifest?> ParseFileAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return Parse(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>将 JSON 的字符串或字符串数组宽容地读成 List&lt;string&gt;。</summary>
public sealed class FlexibleStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                reader.Skip();
                return null;

            case JsonTokenType.String:
                var single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? null : [single];

            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var item = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(item))
                            list.Add(item);
                    }
                    else if (reader.TokenType != JsonTokenType.Null && reader.TokenType != JsonTokenType.Comment)
                    {
                        throw new JsonException("更新键数组里出现了非字符串元素");
                    }
                }
                reader.Skip();
                return list.Count > 0 ? list : null;

            default:
                throw new JsonException($"UpdateKeys 字段类型不正确（{reader.TokenType}）");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>将 JSON 的单个对象或对象数组读成 List&lt;ModManifestDependency&gt;。</summary>
public sealed class FlexibleDependencyListConverter : JsonConverter<List<ModManifestDependency>?>
{
    public override List<ModManifestDependency>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                reader.Skip();
                return null;

            case JsonTokenType.StartObject:
            {
                var single = JsonSerializer.Deserialize<ModManifestDependency>(ref reader, options);
                return single is null ? null : [single];
            }

            case JsonTokenType.StartArray:
            {
                var list = new List<ModManifestDependency>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.StartObject)
                        list.Add(JsonSerializer.Deserialize<ModManifestDependency>(ref reader, options)!);
                }
                reader.Skip();
                return list.Count > 0 ? list : null;
            }

            default:
                throw new JsonException($"Dependencies 字段类型不正确（{reader.TokenType}）");
        }
    }

    public override void Write(Utf8JsonWriter writer, List<ModManifestDependency>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
