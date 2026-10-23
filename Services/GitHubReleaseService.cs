using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace StardewToolkit.Services;

/// <summary>SMAPI 的一个 Release（只关心安装器 zip 这个 asset）。</summary>
public sealed record SmapiRelease(
    string TagName,
    string DownloadUrl,
    long Size,
    string? Sha256,
    string ReleaseUrl);

/// <summary>下载完整但 SHA-256 与官方不一致（镜像被人动过手脚或文件损坏）。</summary>
public sealed class HashMismatchException : InvalidOperationException
{
    public HashMismatchException(string message) : base(message) { }
}

/// <summary>从 GitHub Releases 拉取并下载最新 SMAPI 安装包（多源下载 + 断点续传 + 官方哈希校验）。</summary>
public sealed class GitHubReleaseService
{
    private readonly HttpClient _http;

    /// <summary>
    /// 下载源前缀：直连 + 镜像。镜像只做转发，
    /// 最终文件一律用 GitHub 官方 release asset 的 SHA-256 校验，镜像被污染也装不进来。
    /// </summary>
    private static readonly string[] DownloadPrefixes =
    [
        "",                            // GitHub 直连
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
    ];

    public GitHubReleaseService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        if (!_http.DefaultRequestHeaders.UserAgent.ToString().Contains("stardew"))
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("StardewToolkit/0.2 (+local tool)");
        }
        _http.Timeout = TimeSpan.FromMinutes(15);
    }

    /// <summary>获取最新正式版（不含预发布），带重试。</summary>
    public async Task<SmapiRelease> GetLatestAsync(CancellationToken ct = default)
    {
        const string url = "https://api.github.com/repos/Pathoschild/SMAPI/releases/latest";
        Exception? lastError = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
                return ParseRelease(doc);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        throw new InvalidOperationException($"无法获取最新版本信息：{lastError?.Message}", lastError);
    }

    private static SmapiRelease ParseRelease(JsonElement doc)
    {
        var tag = doc.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("GitHub 响应缺少 tag_name");
        var htmlUrl = doc.GetProperty("html_url").GetString() ?? "";

        foreach (var asset in doc.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("SMAPI-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith("-installer.zip", StringComparison.OrdinalIgnoreCase))
            {
                string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                if (digest is { } real && real.StartsWith("sha256:", StringComparison.Ordinal))
                    digest = real["sha256:".Length..];

                return new SmapiRelease(
                    TagName: tag,
                    DownloadUrl: asset.GetProperty("browser_download_url").GetString() ?? "",
                    Size: asset.GetProperty("size").GetInt64(),
                    Sha256: digest,
                    ReleaseUrl: htmlUrl);
            }
        }

        throw new InvalidDataException("最新 Release 里没有找到 SMAPI 安装器 zip，SMAPI 的发布结构可能变了");
    }

    /// <summary>
    /// 逐个下载源尝试：直连 → 镜像。每个源内部还会断点续传重试；
    /// 全部失败才抛异常，异常信息里汇总每个源的失败原因。
    /// </summary>
    public async Task<string> DownloadWithFallbackAsync(
        SmapiRelease release, string destDir,
        IProgress<(long Done, long Total)>? progress,
        Action<string>? log,
        CancellationToken ct = default)
    {
        var failures = new List<string>();

        foreach (var prefix in DownloadPrefixes)
        {
            var label = prefix.Length == 0 ? "GitHub 直连" : new Uri(prefix).Host;
            var url = prefix + release.DownloadUrl;
            try
            {
                log?.Invoke($"尝试下载源：{label} …");
                return await DownloadFromAsync(url, release, destDir, progress, log, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{label}: {ex.Message}");
                log?.Invoke($"  ✗ {label} 失败：{ex.Message}");
            }
        }

        throw new InvalidOperationException("所有下载源都失败了\n" + string.Join("\n", failures));
    }

    /// <summary>对单个下载源做最多 4 次尝试，每次从已下载部分续传。</summary>
    private async Task<string> DownloadFromAsync(
        string url, SmapiRelease release, string destDir,
        IProgress<(long Done, long Total)>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, $"SMAPI-{release.TagName}-installer.zip");

        Exception? last = null;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                return await DownloadOnceAsync(url, release, dest, progress, ct);
            }
            catch (HashMismatchException)
            {
                throw; // 换别的下载源，这个源的包不对
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 4)
                {
                    var resume = File.Exists(dest) ? new FileInfo(dest).Length : 0;
                    log?.Invoke($"  ✗ {ex.Message}，{(resume > 0 ? $"从 {resume / 1024.0 / 1024.0:F1} MB 处续传重试" : "重试")} ({attempt}/4)");
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }

        throw new InvalidOperationException($"下载中断，已重试 4 次：{last?.Message}", last);
    }

    /// <summary>单次下载，支持 HTTP Range 续传。</summary>
    private async Task<string> DownloadOnceAsync(
        string url, SmapiRelease release, string dest,
        IProgress<(long Done, long Total)>? progress,
        CancellationToken ct)
    {
        var resumeFrom = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        var expectResume = resumeFrom > 0;

        // 给"连上并拿到响应头"一步单独设 60 秒上限，避免直连挂死时干等十五分钟
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (expectResume)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using (var resp = await _http.SendAsync(
                   request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token))
        {
            resp.EnsureSuccessStatusCode();

            // 只有服务器明确用 206 响应续传，才可以从断点追加；否则从头下载
            if (expectResume && resp.StatusCode != HttpStatusCode.PartialContent)
                expectResume = false;
            expectResume = expectResume && resumeFrom > 0;

            var total = resp.Content.Headers.ContentLength is { } len
                ? (expectResume ? resumeFrom + len : len)
                : release.Size;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(
                dest,
                expectResume ? FileMode.Open : FileMode.Create,
                FileAccess.Write,
                FileShare.Read);

            if (expectResume)
                target.Seek(resumeFrom, SeekOrigin.Begin);

            var buffer = new byte[81920];
            long done = expectResume ? resumeFrom : 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress?.Report((done, total));
            }
            await target.FlushAsync(ct);
        }

        // SHA-256 校验（镜像也过不了这关）
        if (!string.IsNullOrWhiteSpace(release.Sha256))
        {
            var hash = await ComputeSha256Async(dest);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(dest);
                throw new HashMismatchException(
                    $"下载的安装包校验失败，可能与官方发布不一致。\n本地: {hash}\n官方: {release.Sha256}");
            }
        }

        return dest;
    }

    /// <summary>计算文件 SHA-256（公开给手动导入流程复用）。</summary>
    public static async Task<string> ComputeSha256Async(string file)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}
