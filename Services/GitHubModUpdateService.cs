using System.Net.Http.Json;
using System.Text.Json;

namespace StardewToolkit.Services;

/// <summary>GitHub 上一个 mod 的最新 release（只关心合适的 zip asset）。</summary>
public sealed record GitHubModRelease(
    string TagName,
    string AssetName,
    string AssetUrl,
    long Size);

/// <summary>
/// GitHub mod 更新：解析 GitHub:owner/repo 这个 update key，
/// 拉最新 release 并挑选 zip asset。
///
/// 挑选策略：作者的仓库可能同时发布多个 mod（比如 Pathoschild/StardewMods 一个仓库出十几个 mod），
/// 所以 asset 名要和 manifest 里的 Name 归一化匹配（去掉空格、大小写一致）；
/// 匹配不上但恰好只有一个 zip 时按单 mod 仓库处理直接用。
/// </summary>
public sealed class GitHubModUpdateService
{
    private readonly HttpClient _http;

    public GitHubModUpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(45);
        // GitHub API 强制要求 User-Agent（403 就是缺它/被限流）
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StardewToolkit/0.5 (+mod update; local tool)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>manifest 里 GitHub update key 的内容：owner/repo。解析失败返回 null。</summary>
    public static string? FindGitHubRepo(IReadOnlyList<string>? updateKeys)
    {
        foreach (var key in updateKeys ?? [])
        {
            var i = key.IndexOf(':');
            if (i > 0 && key[..i].Trim().Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            {
                var repo = key[(i + 1)..].Trim();
                if (repo.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2)
                    return repo;
            }
        }
        return null;
    }

    /// <summary>拉最新 release（不含预发布）。仓库不存在、私有限制等直接抛异常由上层展示。</summary>
    public async Task<GitHubModRelease> GetLatestZipAsync(
        string repo, string modName, Action<string>? log = null, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{repo}/releases/latest";
        log?.Invoke($"查询 GitHub release：{repo} …");

        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"GitHub 仓库 {repo} 没有正式发布的 Release（可能作者只在 Nexus 发布）。请用「更新页面」手动下载。");
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);

        var tagName = doc.GetProperty("tag_name").GetString() ?? "";

        // 归一化后的 mod 名（Content Patcher → contentpatcher），asset 匹配用
        var normalizedName = new string(modName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        List<(string Name, string Url, long Size)> zips = [];
        foreach (var asset in doc.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;
            zips.Add((name, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64()));
        }

        if (zips.Count == 0)
            throw new InvalidOperationException($"{repo} 的最新 release（{tagName}）里没有 zip 安装包。");

        // 归一化匹配
        var matched = zips.FirstOrDefault(z =>
        {
            var n = new string(Path.GetFileNameWithoutExtension(z.Name).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return n.StartsWith(normalizedName, StringComparison.Ordinal)
                   || n.Contains(normalizedName, StringComparison.Ordinal);
        });

        string chosen = matched.Name
            ?? (zips.Count == 1 ? zips[0].Name : throw new InvalidOperationException(
                $"{repo} 的最新 release 有 {zips.Count} 个 zip，且名字对不上「{modName}」。" +
                "请在 mod 管理器里用「更新页面」去官方下载页手动下载，再用「安装 zip…」导入。"));

        var pick = zips.First(z => z.Name == chosen);
        log?.Invoke($"选中安装包：{pick.Name}（{pick.Size / 1024.0:F0} KB，版本 {tagName}）");

        return new GitHubModRelease(tagName, pick.Name, pick.Url, pick.Size);
    }

    /// <summary>
    /// 下载 release zip 到本地目录，返回完整路径。
    /// mirrorPrefix 传镜像前缀（如 https://gh-proxy.com/）时走该镜像下载；
    /// 留空走 GitHub 直连。
    /// </summary>
    public async Task<string> DownloadAsync(
        GitHubModRelease release, string destDir, Action<string>? log = null,
        CancellationToken ct = default, string? mirrorPrefix = null)
    {
        var url = string.IsNullOrEmpty(mirrorPrefix) ? release.AssetUrl : mirrorPrefix + release.AssetUrl;
        var mirrorLabel = string.IsNullOrEmpty(mirrorPrefix) ? "GitHub 直连" : new Uri(mirrorPrefix).Host;

        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, release.AssetName);
        for (int i = 2; File.Exists(dest); i++)
            dest = Path.Combine(destDir, $"{Path.GetFileNameWithoutExtension(release.AssetName)} ({i}).zip");

        log?.Invoke($"下载安装包（{mirrorLabel}）…");
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        await stream.CopyToAsync(file, ct);

        log?.Invoke($"下载完成：{dest}（{new FileInfo(dest).Length / 1024.0:F0} KB）");
        return dest;
    }
}
