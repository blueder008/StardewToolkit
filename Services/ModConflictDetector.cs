namespace StardewToolkit.Services;

/// <summary>冲突组里的一个 mod 参与者。</summary>
public sealed record ConflictEntry(ModEntry Mod, string Detail);

/// <summary>
/// 一组冲突：「N 个启用中的 mod 正在争夺同一份资源」。
/// </summary>
public sealed class ConflictGroup
{
    /// <summary>大类：重复ID / 资源覆盖 / 内容包Patch。</summary>
    public required string Category { get; init; }

    /// <summary>各方争夺的资源标识（UniqueID / 相对路径 / patch 的 Target）。</summary>
    public required string Target { get; init; }

    /// <summary>标题（列表首列显示）。</summary>
    public string Title => Category switch
    {
        "重复ID" => $"重复 ID：{Target}",
        "资源覆盖" => $"XNB 资源覆盖：{Target}",
        _ => $"内容包 Patch 冲突：{Target}",
    };

    /// <summary>给用户看的处置建议。</summary>
    public required string Advice { get; init; }

    public required List<ConflictEntry> Entries { get; init; }
}

/// <summary>
/// Mod 冲突检测：找「多个启用中的 mod 改同一份资源」的组合。
///
/// 覆盖三类（都只看启用中的 mod，SMAPI 只加载它们）：
///   1. 重复 UniqueID —— SMAPI 只加载其中一个，其余静默忽略；
///   2. XNB 资源覆盖 —— 两个 mod 都带同相对路径的 .xnb/.xgs/.xsb/.xwb（老式资源替换），
///      谁后加载谁生效；
///   3. Content Patcher 同 Target —— 两个内容包都 patch 同一个 Target（如 Maps/FarmHouse），
///      依 SMAPI 加载顺序后者覆盖前者，是兼容性问题的常见根源。
/// </summary>
public static class ModConflictDetector
{
    /// <summary>老式 XNB 资源替换用到的扩展名。</summary>
    private static readonly string[] XnbExtensions = [".xnb", ".xgs", ".xsb", ".xwb"];

    /// <summary>Content Patcher 框架的 UniqueID。</summary>
    private const string ContentPatcherId = "Pathoschild.ContentPatcher";

    /// <summary>递归搜文件的深度上限（XNB 资源替换包通常很浅）。</summary>
    private const int MaxWalkDepth = 6;

    /// <summary>后台线程跑完整检测。mods 传 Mod 管理器扫好的列表（含禁用），只挑启用中的比。</summary>
    public static Task<List<ConflictGroup>> DetectAsync(IEnumerable<ModEntry> mods, CancellationToken ct = default) =>
        Task.Run(() => Detect(mods), ct);

    private static List<ConflictGroup> Detect(IEnumerable<ModEntry> mods)
    {
        var groups = new List<ConflictGroup>();
        var enabled = mods
            .Where(m => m.Enabled && m.Manifest is { } && !m.IsBroken)
            .ToList();
        if (enabled.Count == 0)
            return groups;

        DetectDuplicateIds(enabled, groups);
        DetectXnbOverwrites(enabled, groups);
        DetectContentPatcherTargets(enabled, groups);
        return groups;
    }

    /*********
    ** 1. 重复 ID
    *********/
    private static void DetectDuplicateIds(List<ModEntry> enabled, List<ConflictGroup> groups)
    {
        foreach (var group in enabled
            .Where(m => !string.IsNullOrWhiteSpace(m.UniqueId))
            .GroupBy(m => m.UniqueId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            groups.Add(new ConflictGroup
            {
                Category = "重复ID",
                Target = group.Key!,
                Advice = "SMAPI 只会加载其中一个，其余被静默忽略（加载哪个取决于文件夹顺序）。" +
                         "把不要的那份删掉或移进 .disabled（旧版还在备份管理里找得到）。",
                Entries = group
                    .Select(m => new ConflictEntry(m, $"版本 {m.VersionText}，{m.RelativeFolder}"))
                    .ToList(),
            });
        }
    }

    /*********
    ** 2. XNB 资源覆盖
    *********/
    private static void DetectXnbOverwrites(List<ModEntry> enabled, List<ConflictGroup> groups)
    {
        // 相对路径 → 参与的 mod 列表（路径大小写归一化，Windows 不分大小写）
        var byPath = new Dictionary<string, List<(ModEntry Mod, long Size)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in enabled)
        {
            foreach (var (relative, size) in CollectXnbFiles(mod.Folder, prefix: "", depth: 0))
            {
                if (!byPath.TryGetValue(relative, out var list))
                    byPath[relative] = list = [];
                list.Add((mod, size));
            }
        }

        var conflicts = byPath
            .Where(kv => kv.Value
                .Select(v => v.Mod.UniqueId is { Length: > 0 } id ? id : v.Mod.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .OrderBy(kv => kv.Key);

        foreach (var (path, participants) in conflicts)
        {
            groups.Add(new ConflictGroup
            {
                Category = "资源覆盖",
                Target = path,
                Advice = "多个 mod 都在替换同一份游戏资源（老式 XNB 替换），实际生效的是加载顺序最后一个，" +
                         "换加载顺序或删掉其中一个（资源类 mod 通常二选一）。",
                Entries = participants
                    .Select(v => new ConflictEntry(v.Mod, $"{v.Size / 1024.0:0.#} KB"))
                    .ToList(),
            });
        }
    }

    /// <summary>枚举一个 mod 目录里所有的 XNB 家族文件，返回（相对 mod 根的路径, 大小）。</summary>
    private static IEnumerable<(string Relative, long Size)> CollectXnbFiles(string folder, string prefix, int depth)
    {
        if (depth > MaxWalkDepth)
            yield break;

        string[] subDirs;
        try { subDirs = Directory.GetDirectories(folder); }
        catch { yield break; }

        string[] files;
        try { files = Directory.GetFiles(folder); }
        catch { yield break; }

        foreach (var file in files)
        {
            if (!XnbExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            long size = 0;
            try { size = new FileInfo(file).Length; }
            catch { /* 大小拿不到也不影响判定 */ }

            var name = Path.GetFileName(file);
            yield return (prefix.Length == 0 ? name : $"{prefix}/{name}", size);
        }

        foreach (var sub in subDirs)
        {
            var nextPrefix = prefix.Length == 0 ? Path.GetFileName(sub) : $"{prefix}/{Path.GetFileName(sub)}";
            foreach (var hit in CollectXnbFiles(sub, nextPrefix, depth + 1))
                yield return hit;
        }
    }

    /*********
    ** 3. Content Patcher patch 冲突
    *********/
    private static void DetectContentPatcherTargets(List<ModEntry> enabled, List<ConflictGroup> groups)
    {
        var packs = enabled
            .Where(m => string.Equals(m.Manifest!.ContentPackFor?.UniqueID, ContentPatcherId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (packs.Count < 2)
            return;

        // Target → （mod, 同 mod 的 patch 数量）
        var byTarget = new Dictionary<string, List<(ModEntry Mod, int PatchCount)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            foreach (var (target, count) in ReadContentTargets(pack.Folder))
            {
                if (!byTarget.TryGetValue(target, out var list))
                    byTarget[target] = list = [];

                var existingIndex = list.FindIndex(v =>
                    string.Equals(v.Mod.UniqueId, pack.UniqueId, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                    list[existingIndex] = (list[existingIndex].Mod, list[existingIndex].PatchCount + count);
                else
                    list.Add((pack, count));
            }
        }

        var conflicting = byTarget
            .Where(kv => kv.Value
                .Select(v => v.Mod.UniqueId is { Length: > 0 } id ? id : v.Mod.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
            .OrderBy(kv => kv.Key);

        foreach (var (target, participants) in conflicting)
        {
            groups.Add(new ConflictGroup
            {
                Category = "内容包Patch",
                Target = target,
                Advice = "两个内容包都在 patch 同一个游戏资产：SMAPI 里后加载的会覆盖先加载的" +
                         "（加载顺序按 mod 文件夹排序），两者不能同时全部生效。" +
                         "很多 mod 页面会提供单独的兼容版本，优先用官方给的。",
                Entries = participants
                    .Select(v => new ConflictEntry(v.Mod, $"patch {v.PatchCount} 处"))
                    .ToList(),
            });
        }
    }

    /// <summary>读 content.json 的 Changes[].Target（宽容：整个读不出就丢，单条缺 Target 跳过；Include 不递归）。</summary>
    private static List<(string Target, int Count)> ReadContentTargets(string modFolder)
    {
        var contentJson = Path.Combine(modFolder, "content.json");
        try
        {
            if (!File.Exists(contentJson))
                return [];

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(contentJson));
            var result = new List<(string Target, int Count)>();

            if (doc.RootElement.TryGetProperty("Changes", out var changes)
                && changes.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var change in changes.EnumerateArray())
                {
                    // "Include" 的条目另有文件，不递归解析（少见且主文件声明已足够提示）
                    if (!change.TryGetProperty("Target", out var target)
                        || target.ValueKind != System.Text.Json.JsonValueKind.String)
                        continue;

                    var key = target.GetString()?.Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(key))
                        continue;
                    if (result.Count > 0 && result[^1].Target == key)
                        result[^1] = (key, result[^1].Count + 1);
                    else
                        result.Add((key, 1));
                }
            }

            return result;
        }
        catch
        {
            return []; // content.json 读不了就当这个包没声明
        }
    }
}
