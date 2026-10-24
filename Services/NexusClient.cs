using System.Net.Http.Headers;
using System.Text.Json;

namespace StardewToolkit.Services;

/// <summary>validate.json 里返回的用户信息。</summary>
public sealed record NexusUser(
    int UserId,
    string Name,
    bool IsPremium,
    bool IsSupporter);

/// <summary>files.json 里的一个文件条目（只留我们用得到的字段）。</summary>
public sealed record NexusModFile(
    long FileId,
    string Name,
    string? Version,
    string? ModVersion,
    string? FileName,
    long UploadedTimestamp,
    string? Description);

/// <summary>mods/{id}.json 返回的 mod 元数据（网格视图封面用，只留需要的字段）。</summary>
public sealed record NexusModInfo(
    long ModId,
    string Name,
    string? Summary,
    string? Author,
    string? PictureUrl,
    int EndorsementCount);

/// <summary>Nexus API 出错（带尽快能说明清楚的中文消息）。</summary>
public sealed class NexusApiException : Exception
{
    public int StatusCode { get; }

    public NexusApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Nexus Mods 官方 API v1 的最小客户端：验证 key、查 mod 最新主文件、下载。
///
/// 鉴权走 personal API key（用户在 nexusmods.com「我的账户 → API 访问」页自己生成），
/// 请求头带 `apikey` + `Application-Version`（官方要求语义化版本）。
/// 下载分两步：download_link.json 拿预签名的 CDN 地址（**响应是镜像数组，字段名大写 URI**，
/// 第一条优先——官方 S3 才是正主，其他是历史镜像），再用一个不带鉴权头的独立 HttpClient 拉 CDN 文件
/// （S3 对多余的鉴权头敏感，绝不能把 apikey 带过去）。
///
/// 硬限制：download_link 只对 Premium 会员放开；非 Premium 会 403，
/// 由上层转成「打开文件页手动下载 + 安装 zip」的兜底流程。
/// </summary>
public sealed class NexusClient
{
    private const string Domain = "stardewvalley";
    private static readonly string BaseUrl = $"https://api.nexusmods.com/v1/games/{Domain}";

    private readonly HttpClient _http;

    /// <summary>一个不带 Nexus 鉴权头的共享下载客户端（给 CDN 预签名链接用）。</summary>
    private static readonly HttpClient CdnHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly HttpClient _cdn;

    /// <summary>和程序集版本一致的应用版本号（官方要求语义化 X.Y.Z）。</summary>
    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";

    public NexusClient(string apiKey = "", HttpClient? http = null, HttpClient? cdn = null)
    {
        _http = http ?? new HttpClient();
        _cdn = cdn ?? CdnHttp;
        _http.Timeout = TimeSpan.FromSeconds(45);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"StardewToolkit/{AppVersion} (+local mod manager)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.Add("Application-Version", AppVersion);
        _http.DefaultRequestHeaders.Add("Application-Name", "StardewToolkit");
        // key 可以为空：nxm 链接自带临时 key+expires 授权，那种下载走的是 URL 参数，
        // 存在一个无效/空的 apikey 头反而会把请求搅黄，所以没配 key 就不发这个头
        var trimmedKey = apiKey?.Trim() ?? "";
        if (trimmedKey.Length > 0)
            _http.DefaultRequestHeaders.Add("apikey", trimmedKey);
    }

    /// <summary>manifest 里 Nexus update key 的 mod id（"Nexus:541" → 541）。没有就返回 null。</summary>
    public static long? FindNexusModId(IReadOnlyList<string>? updateKeys)
    {
        foreach (var key in updateKeys ?? [])
        {
            var i = key.IndexOf(':');
            if (i > 0 && key[..i].Trim().Equals("Nexus", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(key[(i + 1)..].Trim(), out var id) && id > 0)
                return id;
        }
        return null;
    }

    /// <summary>
    /// 验证 API key 并返回用户信息。401（key 无效/撤销）、403（IP 被封）都转成能看懂的异常。
    /// </summary>
    public async Task<NexusUser> ValidateAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("https://api.nexusmods.com/v1/users/validate.json", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new NexusApiException(401, "API Key 无效或已被撤销。请去 nexusmods.com「我的账户 → API 访问」重新生成。");
        resp.EnsureSuccessStatusCode();

        var doc = await ReadJsonAsync(resp, ct);
        return new NexusUser(
            UserId: doc.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.Number ? uid.GetInt32() : 0,
            Name: doc.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            IsPremium: doc.TryGetProperty("is_premium", out var prem) && prem.ValueKind == JsonValueKind.True,
            IsSupporter: doc.TryGetProperty("is_supporter", out var sup) && sup.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// 查一个 mod 的最新主文件（category=main，按上传时间降序取第一个）。
    /// 404 → mod id 写错或已隐藏；403 → mod 只对会员/被隐藏。
    /// </summary>
    public async Task<NexusModFile> GetLatestMainFileAsync(long modId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/mods/{modId}/files.json?category=main", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new NexusApiException(404, $"Nexus 上找不到 mod {modId}（ID 写错、mod 已隐藏或已被删除）。");
        resp.EnsureSuccessStatusCode();

        var doc = await ReadJsonAsync(resp, ct);
        if (!doc.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            throw new NexusApiException(0, "Nexus 返回的文件列表格式不对（没找到 files 数组）。");

        NexusModFile? latest = null;
        foreach (var f in files.EnumerateArray())
        {
            // 主类文件(category_id 1)。就算服务器忽略了 category 过滤参数也再保险一道
            var categoryId = f.TryGetProperty("category_id", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt32() : 1;
            if (categoryId != 1)
                continue;

            var file = new NexusModFile(
                FileId: f.TryGetProperty("file_id", out var fid) && fid.ValueKind == JsonValueKind.Number ? fid.GetInt64() : 0,
                Name: f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                Version: f.TryGetProperty("version", out var v) ? v.GetString() : null,
                ModVersion: f.TryGetProperty("mod_version", out var mv) ? mv.GetString() : null,
                FileName: f.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
                UploadedTimestamp: f.TryGetProperty("uploaded_timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
                Description: f.TryGetProperty("description", out var d) ? d.GetString() : null);

            if (latest is null || file.UploadedTimestamp > latest.UploadedTimestamp || file.FileId > latest.FileId)
                latest = file;
        }

        return latest ?? throw new NexusApiException(0, $"Nexus mod {modId} 没有可用的主文件（作者可能还没上传）。");
    }

    /// <summary>
    /// 拿下载链接并把文件下载到本地。仅 Premium 可用（非 Premium 会 403 → 转成带解释的异常）。
    /// 返回下载好的 zip 完整路径。
    /// </summary>
    public async Task<string> DownloadAsync(
        long modId, string destDir, Action<string>? log = null, CancellationToken ct = default,
        NexusModFile? file = null)
    {
        file ??= await GetLatestMainFileAsync(modId, ct);
        log?.Invoke($"Nexus 最新主文件：{file.FileName ?? file.Name}（版本 {file.ModVersion ?? file.Version ?? "?"}）");

        var linkUrl = $"{BaseUrl}/mods/{modId}/files/{file.FileId}/download_link.json";
        return await DownloadToAsync(linkUrl, file.FileName, modId, file.FileId, destDir, log,
            "这个 mod 需要 Premium 会员才能 API 直下。程序会把官方文件页打开，你在浏览器里下载后用「安装 zip…」导入即可（免费账号一样能用）。",
            ct);
    }

    /// <summary>
    /// 用 nxm:// 链接下载（key+expires 在链接参数里，是 Nexus 给免费账号也放行的官方授权路径）。
    /// key 有效期只有几分钟，过期/失效 → 403 → 中文提示重拿链接。
    /// </summary>
    public async Task<string> DownloadNxmAsync(
        NxmLink link, string destDir, Action<string>? log = null, CancellationToken ct = default)
    {
        var auth = "";
        if (!string.IsNullOrWhiteSpace(link.Key))
            auth = $"?key={Uri.EscapeDataString(link.Key!)}&expires={Uri.EscapeDataString(link.Expires ?? "")}";

        var linkUrl = $"{BaseUrl}/mods/{link.ModId}/files/{link.FileId}/download_link.json{auth}";
        log?.Invoke($"请求下载链接：mod {link.ModId} 文件 {link.FileId}（{link.Domain}）");

        return await DownloadToAsync(linkUrl, null, link.ModId, link.FileId, destDir, log,
            "这个 nxm 下载授权无效或已过期（Nexus 临时 key 一般只有效几分钟）。请回到 Nexus 网站的文件页重新点一次「Mod Manager Download」。",
            ct);
    }

    /// <summary>
    /// 查一个 mod 的元数据（网格视图封面用：picture_url 等）。
    /// 404 → mod id 写错或已隐藏；403 → 只对会员可见。需要 API key。
    /// </summary>
    public async Task<NexusModInfo> GetModInfoAsync(long modId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/mods/{modId}.json", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new NexusApiException(404, $"Nexus 上找不到 mod {modId}（ID 写错、mod 已隐藏或已被删除）。");
        resp.EnsureSuccessStatusCode();

        var doc = await ReadJsonAsync(resp, ct);
        return new NexusModInfo(
            ModId: doc.TryGetProperty("mod_id", out var mid) && mid.ValueKind == JsonValueKind.Number ? mid.GetInt64() : modId,
            Name: doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Summary: doc.TryGetProperty("summary", out var s) ? s.GetString() : null,
            Author: doc.TryGetProperty("author", out var a) ? a.GetString() : null,
            PictureUrl: doc.TryGetProperty("picture_url", out var p) ? p.GetString() : null,
            EndorsementCount: doc.TryGetProperty("endorsement_count", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0);
    }

    /// <summary>查一个具体文件的信息（名字/版本，确认弹窗和日志用）。需要 API key。</summary>
    public async Task<NexusModFile> GetFileInfoAsync(long modId, long fileId, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{BaseUrl}/mods/{modId}/files/{fileId}.json", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new NexusApiException(404, $"Nexus 上找不到 mod {modId} 的文件 {fileId}（可能已被删除）。");
        resp.EnsureSuccessStatusCode();

        var doc = await ReadJsonAsync(resp, ct);
        return new NexusModFile(
            FileId: doc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : fileId,
            Name: doc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Version: doc.TryGetProperty("version", out var v) ? v.GetString() : null,
            ModVersion: doc.TryGetProperty("mod_version", out var mv) ? mv.GetString() : null,
            FileName: doc.TryGetProperty("file_name", out var fn) ? fn.GetString() : null,
            UploadedTimestamp: doc.TryGetProperty("uploaded_timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
            Description: doc.TryGetProperty("description", out var d) ? d.GetString() : null);
    }

    /*********
    ** 内部
    *********/
    /// <summary>
    /// 共享下载核心：请求 download_link → 挑镜像 → CDN 落盘。
    /// fileName 为 null 时用 nexus-{modId}-file{fileId}.zip 兜底。
    /// </summary>
    private async Task<string> DownloadToAsync(
        string linkUrl, string? fileName, long modId, long fileId, string destDir,
        Action<string>? log, string on403Message, CancellationToken ct)
    {
        using var linkResp = await _http.GetAsync(linkUrl, ct);
        if (linkResp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new NexusApiException(403, on403Message);
        if (linkResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new NexusApiException(401, "API Key 无效或已被撤销，请重新生成。");
        linkResp.EnsureSuccessStatusCode();

        using var linkDoc = await JsonDocument.ParseAsync(await linkResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var uri = GetFirstMirrorUri(linkDoc.RootElement)
                  ?? throw new NexusApiException(0, "Nexus 没返回可用的下载镜像。");

        // CDN 下载：绝对不能带 apikey 头（S3 预签名对多余鉴权头敏感），用干净的共享客户端
        Directory.CreateDirectory(destDir);
        var clean = SanitizeFileName(fileName) ?? $"nexus-{modId}-file{fileId}.zip";
        if (!clean.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !clean.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            clean += ".zip";
        var destPath = Path.Combine(destDir, clean);
        for (int i = 2; File.Exists(destPath); i++)
            destPath = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(clean)} ({i}){Path.GetExtension(clean)}");

        log?.Invoke($"从 Nexus CDN 下载…");
        using var dlResp = await _cdn.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        dlResp.EnsureSuccessStatusCode();
        await using var stream = await dlResp.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(destPath);
        await stream.CopyToAsync(fs, ct);

        log?.Invoke($"下载完成：{Path.GetFileName(destPath)}（{new FileInfo(destPath).Length / 1024.0:F0} KB）");
        return destPath;
    }

    /// <summary>download_link 响应是镜像数组 [{URI, name, short_name}]；取第一条。防御性兼容单个对象的情况。</summary>
    private static string? GetFirstMirrorUri(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.TryGetProperty("URI", out var u) && u.GetString() is { Length: > 0 } s)
                    return s;
                if (item.TryGetProperty("uri", out var u2) && u2.GetString() is { Length: > 0 } s2)
                    return s2;
            }
            return null;
        }
        if (root.ValueKind == JsonValueKind.Object)
            return root.TryGetProperty("URI", out var u) ? u.GetString()
                 : root.TryGetProperty("uri", out var u2) ? u2.GetString()
                 : null;
        return null;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Split(invalid, StringSplitOptions.RemoveEmptyEntries).SelectMany(s => s).ToArray());
        return clean.Length == 0 ? null : clean.Trim();
    }
}
