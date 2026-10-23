using System.Web;

namespace StardewToolkit.Services;

/// <summary>
/// 解析后的 nxm:// 下载链接。
/// Nexus 网站上点「Mod Manager Download」时浏览器会拉起形如：
///   nxm://stardewvalley/mods/266/files/14756?key=xxx&amp;expires=1751234567&amp;user_id=123
/// 其中 key + expires 是**官方给免费账号也放行的临时下载授权**（本来就是给 Mod 管理器用的），
/// 过期时间一般几分钟到半小时。
/// </summary>
public sealed record NxmLink(
    string Domain,
    long ModId,
    long FileId,
    string? Key,
    string? Expires)
{
    /// <summary>从命令行参数里解析 nxm 链接；不是链接或格式不对返回 null。</summary>
    public static NxmLink? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim().Trim('"');
        if (!trimmed.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
            return null;

        var rest = trimmed["nxm://".Length..];

        // 拆 path / query
        var queryIndex = rest.IndexOf('?');
        var pathPart = queryIndex < 0 ? rest : rest[..queryIndex];
        var queryPart = queryIndex < 0 ? "" : rest[(queryIndex + 1)..];

        var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        // nxm://stardewvalley/mods/... 或 nxm://www.nexusmods.com/stardewvalley/mods/... 两种都有人发
        if (segments[0].Contains('.'))
            segments = segments[1..];

        if (segments.Length < 5)
            return null;
        if (!segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals("files", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!long.TryParse(segments[2], out var modId) || !long.TryParse(segments[4], out var fileId))
            return null;

        // query 参数：key/expires（user_id 可能没有，忽略）
        string? key = null, expires = null;
        foreach (var pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = HttpUtility.UrlDecode(pair[..eq]).ToLowerInvariant();
            var value = HttpUtility.UrlDecode(pair[(eq + 1)..]);
            if (name == "key")
                key = value;
            else if (name == "expires")
                expires = value;
        }

        return new NxmLink(segments[0].ToLowerInvariant(), modId, fileId, key, expires);
    }
}
