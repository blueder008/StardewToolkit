using System.Security.Cryptography;
using System.Text.Json;

namespace StardewToolkit.Services;

/// <summary>磁盘上的一份封面记录：mod id → 缓存文件与元数据。</summary>
public sealed record CoverEntry(long ModId, string FileName, string? Url, DateTime FetchedAt);

/// <summary>
/// Nexus 封面缓存服务：
///   1. 内存字典挡并发重复请求；
///   2. 磁盘缓存（%APPDATA%\StardewToolkit\covers\）按 mod id 存图 + index.json 存 URL 映射；
///   3. 未命中且无 API key / 下载失败 → 返回 null（网格画占位块），绝不让封面影响主功能。
///
/// 限流：信号量把并发 API 请求压到 3 个（Nexus 对高频元数据请求会临时 429）。
/// </summary>
public static class CoverCache
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ApiGate = new(3, 3);

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewToolkit", "covers");

    private static string IndexPath => Path.Combine(CacheDir, "index.json");

    private static Dictionary<string, CoverEntry> _index = [];
    private static bool _indexLoaded;

    /// <summary>内存中已加载的位图（key = mod id），退出时随进程回收，不做 LRU（封面是小图，量级有限）。</summary>
    private static readonly Dictionary<long, Image?> MemoryCache = [];

    /// <summary>某 mod 的封面是否已经在缓存里（有图）。</summary>
    public static bool HasCover(long modId) => LoadCoverFile(modId) is not null;

    /// <summary>取封面位图。只查缓存，不发网络请求；没有返回 null（调用方画占位块）。</summary>
    public static Image? GetCached(long modId)
    {
        lock (Gate)
        {
            if (MemoryCache.TryGetValue(modId, out var img))
                return img;
        }
        var path = LoadCoverFile(modId);
        if (path is null)
            return null;
        try
        {
            // 先读字节再建图，避免 Image 对象锁住文件导致以后删不掉
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var image = Image.FromStream(ms);
            lock (Gate)
                MemoryCache[modId] = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 确保封面已缓存：有 → 直接返回 true；无 → 查 Nexus 元数据拿 picture_url 并下载。
    /// 全程吞异常（返回 false），调用方决定降级展示。cancellationToken 取消批量任务。
    /// </summary>
    public static async Task<bool> EnsureAsync(
        long modId, string? apiKey, Action<string>? log = null, CancellationToken ct = default)
    {
        // 已经有图就完事
        if (LoadCoverFile(modId) is not null)
            return true;

        if (string.IsNullOrWhiteSpace(apiKey))
            return false;   // 没有 key 拿不到元数据，网格走占位块

        try
        {
            var url = await GetPictureUrlAsync(modId, apiKey, ct);
            if (string.IsNullOrWhiteSpace(url))
                return false;

            Directory.CreateDirectory(CacheDir);
            var ext = Path.GetExtension(new Uri(url).AbsolutePath) is { Length: > 1 } e ? e : ".jpg";
            var fileName = $"cover-{modId}{ext.ToLowerInvariant()}";
            var destPath = Path.Combine(CacheDir, fileName);

            await ApiGate.WaitAsync(ct);
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("StardewToolkit/0.9 (+cover cache)");
                using var resp = await http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(destPath);
                await resp.Content.CopyToAsync(fs, ct);
            }
            finally
            {
                ApiGate.Release();
            }

            // 元数据记录（URL 变了可以重拉）
            var entry = new CoverEntry(modId, fileName, url, DateTime.Now);
            lock (Gate)
            {
                _index[modId.ToString()] = entry;
                SaveIndex();
            }
            lock (Gate)
                MemoryCache.Remove(modId);   // 让下次 GetCached 重新读新文件

            log?.Invoke($"封面已缓存：mod {modId}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"⚠ 封面获取失败（mod {modId}）：{ex.Message}");
            return false;
        }
    }

    /// <summary>查元数据里的 picture_url（带一层磁盘 index 缓存，URL 没变就不打 API）。</summary>
    private static async Task<string?> GetPictureUrlAsync(long modId, string apiKey, CancellationToken ct)
    {
        lock (Gate)
        {
            LoadIndex();
            if (_index.TryGetValue(modId.ToString(), out var cached) && !string.IsNullOrEmpty(cached.Url))
                return cached.Url;
        }

        var client = new NexusClient(apiKey);
        var info = await client.GetModInfoAsync(modId, ct);

        lock (Gate)
        {
            // 只记 URL，不记图（图还没下）
            _index[modId.ToString()] = new CoverEntry(modId, "", info.PictureUrl, DateTime.Now);
            SaveIndex();
        }
        return info.PictureUrl;
    }

    private static string? LoadCoverFile(long modId)
    {
        try
        {
            lock (Gate)
                LoadIndex();
            if (!_index.TryGetValue(modId.ToString(), out var entry) || entry.FileName.Length == 0)
                return null;
            var path = Path.Combine(CacheDir, entry.FileName);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static void LoadIndex()
    {
        if (_indexLoaded)
            return;
        _indexLoaded = true;
        try
        {
            if (File.Exists(IndexPath))
            {
                var json = File.ReadAllText(IndexPath);
                _index = JsonSerializer.Deserialize<Dictionary<string, CoverEntry>>(json) ?? [];
            }
        }
        catch
        {
            _index = [];
        }
    }

    private static void SaveIndex()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(IndexPath,
                JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 缓存索引写不进去不影响功能
        }
    }
}
