namespace StardewToolkit.Services;

/// <summary>一个 mod 文件夹的扫描结果类型（对齐 SMAPI 官方 ModScanner 的 ModType）。</summary>
public enum ModEntryType
{
    /// <summary>SMAPI 模组（有 EntryDll）。</summary>
    Smapi,

    /// <summary>内容包（挂在某个框架下，如 Content Patcher）。</summary>
    ContentPack,

    /// <summary>老的 XNB 资源包，不是 SMAPI 模组，SMAPI 不会加载它。</summary>
    Xnb,

    /// <summary>异常：没有 manifest、manifest 解析失败、类型字段矛盾等。</summary>
    Invalid,
}

/// <summary>扫描出来的一个 mod 文件夹（每个 manifest.json 一条记录）。</summary>
public sealed class ModEntry
{
    /*********
    ** 扫描时写入
    *********/
    /// <summary>manifest 所在文件夹（即 mod 的根目录）。</summary>
    public required string Folder { get; init; }

    /// <summary>Mods 总目录。</summary>
    public required string ModsRoot { get; init; }

    /// <summary>是否启用：不在以点开头的一级文件夹里（SMAPI 官方约定：点开头的文件夹会被忽略）。</summary>
    public bool Enabled { get; internal set; }

    public ModEntryType Type { get; internal set; }

    /// <summary>解析出来的 manifest（null = 没有 manifest 或解析失败）。</summary>
    public ModManifest? Manifest { get; internal set; }

    /// <summary>异常/损坏的原因说明（null = 正常）。</summary>
    public string? Error { get; internal set; }

    /// <summary>必需依赖里没装的（逗号分隔的 UniqueID 列表）。</summary>
    public string? MissingDependencies { get; internal set; }

    /// <summary>这个 UniqueID 和别的已启用 mod 重复（SMAPI 只会加载其中一个）。</summary>
    public bool IsDuplicateId { get; internal set; }

    /// <summary>manifest 写了 EntryDll 但文件不存在。</summary>
    public bool IsEntryDllMissing { get; internal set; }

    /*********
    ** 更新检查后写入
    *********/
    /// <summary>更新检查结果（null = 还没检查过）。</summary>
    public ModUpdateData? Update { get; internal set; }

    /*********
    ** 组合字段
    *********/
    /// <summary>Mods 根目录到本 mod 文件夹的相对路径。</summary>
    public string RelativeFolder => Path.GetRelativePath(ModsRoot, Folder);

    /// <summary>mod 文件夹名。</summary>
    public string FolderName => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>mod 自己声明的名字，没有就退化成文件夹名。</summary>
    public string Name =>
        Manifest?.Name is { Length: > 0 } name ? name : FolderName;

    public string? UniqueId =>
        string.IsNullOrWhiteSpace(Manifest?.UniqueID) ? null : Manifest!.UniqueID;

    public string VersionText => Manifest?.Version ?? "";

    public string AuthorText => Manifest?.Author ?? "";

    /// <summary>这是不是一条异常记录（没有 manifest 等）。</summary>
    public bool IsBroken => Type is ModEntryType.Invalid or ModEntryType.Xnb;

    /// <summary>SMAPI 会不会加载这个 mod（启用 + 有 manifest + 类型合法 + DLL 存在 + ID 不重复）。</summary>
    public bool WillLoad =>
        Enabled
        && Manifest is { }
        && !IsBroken
        && string.IsNullOrWhiteSpace(MissingDependencies)
        && !IsDuplicateId
        && !IsEntryDllMissing;

    /// <summary>确认了有可用更新的最新版本号（没有就 null）。</summary>
    public string? UpdateVersion => Update?.SuggestedVersion ?? Update?.LatestVersion;

    /// <summary>判定为确实有更新时才 true（版本号严格比已装的新，特殊版本不误报）。</summary>
    public bool HasUpdate =>
        Update is not null
        && ModVersionUtil.IsNewer(VersionText, UpdateVersion);

    /// <summary>打开 mod 所在文件夹。</summary>
    public void OpenInExplorer() =>
        System.Diagnostics.Process.Start("explorer.exe", $"\"{Folder}\"");
}

/// <summary>smapi.io 更新检查接口对单个 mod 的响应。</summary>
public sealed record ModUpdateData(
    /// <summary>官方推荐更新的版本（考虑了版本映射规则；可能为空）。</summary>
    string? SuggestedVersion,

    /// <summary>推荐更新的 Mod 页面地址。</summary>
    string? SuggestedUrl,

    /// <summary>最新正式版（来自扩展元数据）。</summary>
    string? LatestVersion,

    /// <summary>smapi.io 维基的兼容性状态（OK、Broken、Abandoned……原始枚举字符串）。</summary>
    string? CompatibilityStatus,

    /// <summary>兼容性状态的人类可读说明，比如「官方不支持了，社区还维护着」。</summary>
    string? CompatibilitySummary,

    /// <summary>查询这个 mod 时 API 报告的问题。</summary>
    string[] Errors);

/*********
** 扫描器
*********/
/// <summary>
/// 扫描一个 Mods 目录里的所有 SMAPI mod / 内容包。
///
/// 语义对齐 SMAPI 官方的 ModScanner（src/SMAPI.Toolkit/Framework/ModScanning/ModScanner.cs）：
///   - 名字以点开头的文件夹被 SMAPI 忽略 → 我们用 Mods/.disabled/ 存放被禁用的 mod；
///   - mod 不会只装在 Mods 根目录第一层，内容包常嵌套在 mod 目录里，所以要递归找 manifest.json；
///   - 没有标志文件无论有意义与否都当作系统垃圾文件，别报无效 mod。
/// </summary>
public sealed class ModScanner
{
    /// <summary>存放已禁用 mod 的目录（点开头，SMAPI 自动忽略）。</summary>
    public const string DisabledFolderName = ".disabled";

    /// <summary>任何目录里一眼是系统/管理器垃圾文件的，不当作 mod 内容。</summary>
    private static readonly string[] IgnoreFileNames =
    [
        "desktop.ini",
        "Thumbs.db",
        ".DS_Store",
        "__folder_managed_by_vortex",
        "__MACOSX",
    ];

    /// <summary>
    /// 这些扩展名与 mod 判定无关（官方 IgnoreFileExtensions：文档、占位图片、压缩包、快捷方式等）。
    /// 一个文件夹里只有这些内容时，SMAPI 视同它没有文件。
    /// </summary>
    private static readonly string[] IgnoreFileExtensions =
    [
        ".doc", ".docx", ".md", ".rtf", ".txt",
        ".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".psd", ".tif", ".xcf",
        ".rar", ".zip", ".7z", ".tar",
        ".backup", ".bak", ".old",
        ".url", ".lnk",
    ];

    /// <summary>manifest 搜索的最深层数，防止一个 symlink 死循环把程序卡住。</summary>
    private const int MaxDepth = 8;

    /// <summary>禁用 mod 的存放目录。</summary>
    public static string DisabledDirectory(string modsDir) =>
        Path.Combine(modsDir, DisabledFolderName);

    /// <summary>扫描 Mods 目录（后台线程执行，返回包含正常/异常/禁用区的全部条目）。</summary>
    public static Task<List<ModEntry>> ScanAsync(string modsDir, CancellationToken ct = default) =>
        Task.Run(() => Scan(modsDir, ct), ct);

    private static List<ModEntry> Scan(string modsDir, CancellationToken ct)
    {
        var entries = new List<ModEntry>();
        var root = new DirectoryInfo(modsDir);
        if (!root.Exists)
            return entries;

        // 一级目录里的点文件夹（.disabled 或其他管理器的备份目录）都算「禁用区」，
        // SMAPI 官方行为就是忽略它们，但里面的 manifest 我们照样读出来展示
        WalkDirectory(root, modsDir, depth: 0, disabledZone: false, entries, ct);
        PostProcess(entries);

        return entries;
    }

    /// <summary>
    /// 按官方 ModScanner 的语义递归扫描一个目录：
    ///   - 一层里有实际文件的目录 = mod 文件夹（有 manifest 就是一个 mod；没有就是异常目录），不再下钻；
    ///   - 一层里没有任何实际文件的目录 = 搜索文件夹，只往子目录里找 mod；
    ///   - 名字以点开头的目录 SMAPI 一律忽略；一级目录的点文件夹我们当作「禁用区」读里面的 mod。
    /// </summary>
    /// <param name="dir">当前目录。</param>
    /// <param name="modsDir">Mods 根目录。</param>
    /// <param name="depth">相对 Mods 根的深度。</param>
    /// <param name="disabledZone">当前是否已在禁用区（一级点目录之内）。</param>
    private static void WalkDirectory(
        DirectoryInfo dir, string modsDir, int depth, bool disabledZone,
        List<ModEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
            return;

        // 系统垃圾目录直接跳过
        if (depth > 0 && IsJunkName(dir.Name))
            return;

        // 点文件夹：更深的点是 macOS 垃圾/隐藏目录，整个跳过；一级目录里只有我们的
        // .disabled 才算「禁用区」（进去读被禁用 mod 的 manifest），其他点目录（比如 .backup）
        // 是管理器自己的工作数据，静默跳过，别把它们刷进 mod 列表
        if (dir.Name.StartsWith(".", StringComparison.Ordinal))
        {
            if (depth > 1)
                return;
            if (depth == 1)
            {
                if (!string.Equals(dir.Name, DisabledFolderName, StringComparison.Ordinal))
                    return;
                disabledZone = true;
            }
        }

        var (relevantDirs, relevantFiles) = GetRelevantEntries(dir, ct);

        bool isRoot = depth == 0;
        bool isSearchFolder = isRoot || (relevantFiles.Count == 0 && relevantDirs.Count > 0);

        if (isSearchFolder)
        {
            // 纯分组目录：每个子目录里找 mod（官方行为）
            foreach (var sub in relevantDirs)
                WalkDirectory(sub, modsDir, depth + 1, disabledZone, entries, ct);
            return;
        }

        // ===== mod 文件夹：这一层有实际文件 =====
        var manifestFile = relevantFiles.FirstOrDefault(
            f => string.Equals(f.Name, "manifest.json", StringComparison.OrdinalIgnoreCase));

        if (manifestFile is not null)
        {
            // 带 manifest 的目录就是一个 mod 本体，官方不会再下钻它的子目录——
            // （SVE 这类大 mod 的资源子目录不是 mod，之前误把它们全报成异常目录就是少了这条规则）
            ReadModFolder(manifestFile, modsDir, disabledZone, entries, ct);
            return;
        }

        // 没有 manifest：整棵子树找类型线索（XNB 资源包、官方安装器残留），都没有就标记异常
        var problem = ClassifyInvalidFolder(CollectRelevantFiles(dir, ct));
        if (problem is not null)
            entries.Add(CreateInvalidEntry(dir, modsDir, problem, disabledZone));
    }

    /// <summary>读一个带 manifest.json 的 mod 文件夹，生成条目（含类型、错误、DLL 是否缺失）。</summary>
    private static void ReadModFolder(
        FileInfo manifestFile, string modsDir, bool disabledZone,
        List<ModEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ModManifest? manifest = null;
        string? parseError = null;
        try
        {
            manifest = ModManifestParser.Parse(File.ReadAllText(manifestFile.FullName));
            if (manifest is null)
                parseError = "manifest.json 解析失败（格式非法）";
        }
        catch (Exception ex)
        {
            parseError = $"读 manifest.json 失败：{ex.Message}";
        }

        var type = Classify(manifest, parseError);
        var entry = new ModEntry
        {
            Folder = manifestFile.Directory!.FullName,
            ModsRoot = modsDir,
            Enabled = !disabledZone,
            Type = type,
            Manifest = manifest,
            Error = ResolveError(type, manifest, parseError),
        };
        if (type == ModEntryType.Smapi
            && manifest?.EntryDll is { Length: > 0 } dllName
            && !File.Exists(Path.Combine(entry.Folder, dllName)))
        {
            entry.IsEntryDllMissing = true;
        }
        entries.Add(entry);
    }

    /// <summary>分类一个 manifest：SMAPI 模组、内容包、还是无效（两者互斥，写一个那种都是无效）。</summary>
    private static ModEntryType Classify(ModManifest? manifest, string? parseError)
    {
        if (parseError is not null || manifest is null)
            return ModEntryType.Invalid;

        bool hasDll = !string.IsNullOrWhiteSpace(manifest.EntryDll);
        bool isPack = !string.IsNullOrWhiteSpace(manifest.ContentPackFor?.UniqueID);

        if (hasDll && isPack)
            return ModEntryType.Invalid;
        if (isPack)
            return ModEntryType.ContentPack;
        if (hasDll)
            return ModEntryType.Smapi;
        return ModEntryType.Invalid;
    }

    private static string? ResolveError(ModEntryType type, ModManifest? manifest, string? parseError)
    {
        if (parseError is not null)
            return parseError;

        if (type == ModEntryType.Invalid)
        {
            bool hasDll = !string.IsNullOrWhiteSpace(manifest?.EntryDll);
            bool isPack = !string.IsNullOrWhiteSpace(manifest?.ContentPackFor?.UniqueID);
            if (hasDll && isPack)
                return "manifest 里 EntryDll 和 ContentPackFor 只能填一个";
            return "manifest 缺少 EntryDll 或 ContentPackFor 字段";
        }

        return null;
    }

    /// <summary>
    /// 给没有 manifest 的文件夹分类（基于整棵子树的相关文件）：
    /// XNB 资源包、SMAPI 官方安装器残留、或纯粹的无效 mod 目录。
    /// 里面全是无关内容（纯图片等）时返回 null，不值得报。
    /// </summary>
    private static string? ClassifyInvalidFolder(List<FileInfo> recursiveFiles)
    {
        var relevant = recursiveFiles
            .Where(f => !IgnoreFileExtensions.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (relevant.Count == 0)
            return null;

        if (relevant.Any(f =>
                f.Name is "install on Windows.bat" or "install on Windows.exe"
                       or "install on Linux.sh" or "install on macOS.command"))
            return "里面是 SMAPI 官方安装器，它不是 mod（装完可以把这些删掉）";

        if (relevant.Any(f => IsXnbExtension(f.Name)))
            return "XNB资源包，不是 SMAPI mod（它会和本体的文件冲突，见 smapi.io/xnb）";

        return "只有文件没有 manifest.json（不是有效的 SMAPI mod 目录）";
    }

    private static bool IsXnbExtension(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext is ".xgs" or ".xnb" or ".xsb" or ".xwb";
    }

    private static ModEntry CreateInvalidEntry(DirectoryInfo dir, string modsDir, string error, bool disabledZone) =>
        new()
        {
            Folder = dir.FullName,
            ModsRoot = modsDir,
            Enabled = !disabledZone,
            Type = ModEntryType.Invalid,
            Manifest = null,
            Error = error,
        };

    /// <summary>扫完之后做全局检查：重复 ID、没装的必需依赖。</summary>
    private static void PostProcess(List<ModEntry> entries)
    {
        // 重复 ID：只在已启用且 SMAPI 真的会加载的一组里标
        var enabledWithId = entries
            .Where(e => e.Enabled && e.Manifest is { } && !e.IsBroken && !string.IsNullOrWhiteSpace(e.UniqueId))
            .ToList();
        var idGroups = enabledWithId
            .GroupBy(e => e.UniqueId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in idGroups)
            foreach (var entry in group)
                entry.IsDuplicateId = true;

        // 必需依赖：SMAPI 只加载已启用的 mod，所以依赖检查只看已启用集合；
        // 内容包还依赖它的框架（ContentPackFor）本身已启用
        var loadedIds = enabledWithId
            .Where(e => e.WillLoad)
            .Select(e => e.UniqueId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in enabledWithId)
        {
            var missing = new List<string>();
            foreach (var dep in entry.Manifest!.Dependencies ?? [])
            {
                if (!dep.IsRequired)
                    continue;
                if (string.IsNullOrWhiteSpace(dep.UniqueID))
                    continue;
                if (!loadedIds.Contains(dep.UniqueID))
                    missing.Add(dep.UniqueID);
            }

            var packFor = entry.Manifest.ContentPackFor?.UniqueID;
            if (entry.Manifest.IsContentPack && !string.IsNullOrWhiteSpace(packFor) && !loadedIds.Contains(packFor))
                missing.Add(packFor);

            if (missing.Count > 0)
                entry.MissingDependencies = string.Join(", ", missing);
        }
    }

    /*********
    ** 文件访问兜底（异常目录、被占用的文件都不该让扫描崩掉）
    *********/
    /// <summary>聚拢「没意义」的文件/目录名：系统文件 + macOS 的 ._" 资源叉副本。</summary>
    private static bool IsJunkName(string name) =>
        IgnoreFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || name.StartsWith("._", StringComparison.Ordinal);

    /// <summary>
    /// 收集某目录一层里的「相关」子目录和文件（官方 IsRelevant 语义）：
    /// 文件要排除系统文件和无关扩展名；子目录只排除系统文件——
    /// 点目录不在这里排除，因为一级点目录（禁用区）必须进来。
    /// </summary>
    private static (List<DirectoryInfo> Dirs, List<FileInfo> Files) GetRelevantEntries(
        DirectoryInfo dir, CancellationToken ct)
    {
        var dirs = new List<DirectoryInfo>();
        var files = new List<FileInfo>();
        try
        {
            dirs.AddRange(dir.EnumerateDirectories().Where(d => !IsJunkName(d.Name)));
            files.AddRange(dir.EnumerateFiles().Where(f => !IsJunkName(f.Name)
                && !IgnoreFileExtensions.Contains(Path.GetExtension(f.Name), StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 目录读不了就当空目录，交给上层逻辑静默跳过
        }
        return (dirs, files);
    }

    /// <summary>递归收集目录里所有非垃圾文件（用于给没有 manifest 的目录做类型判断）。</summary>
    private static List<FileInfo> CollectRelevantFiles(DirectoryInfo dir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<FileInfo>();
        Collect(dir, 0);
        return result;

        void Collect(DirectoryInfo folder, int depth)
        {
            if (depth > MaxDepth + 2)
                return;
            try
            {
                foreach (var sub in folder.EnumerateDirectories())
                {
                    if (IsJunkName(sub.Name))
                        continue;
                    Collect(sub, depth + 1);
                }
                result.AddRange(folder.EnumerateFiles().Where(f => !IsJunkName(f.Name)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 读不了的子树跳过
            }
        }
    }
}

/*********
** 启用 / 禁用（移动文件夹）
*********/
/// <summary>
/// 通过移动文件夹实现启用/禁用（SMAPI 官方语义：点开头的文件夹被忽略）。
/// 禁用 = 移到 Mods/.disabled/；启用 = 移回原位（位置记在 %APPDATA% 的索引文件里）。
/// </summary>
public static class ModFolderMover
{
    private static readonly string IndexPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StardewToolkit", "disabled-index.json");

    /// <summary>禁用一个 mod：移动到 .disabled 目录，返回新位置。</summary>
    public static string Disable(ModEntry entry)
    {
        var disabledDir = ModScanner.DisabledDirectory(entry.ModsRoot);
        var target = GetUniqueTarget(disabledDir, entry.FolderName);
        MoveFolder(entry.Folder, target);

        SaveIndex(target, entry.RelativeFolder);
        return target;
    }

    /// <summary>启用一个 mod：按索引移回原位（索引丢了就放回 Mods 根目录），返回新位置。</summary>
    public static string Enable(ModEntry entry)
    {
        var originalRelative = LoadIndex().TryGetValue(Normalize(entry.Folder), out var relative)
            ? relative
            : entry.FolderName;

        var target = GetUniqueTarget(entry.ModsRoot, originalRelative);
        MoveFolder(entry.Folder, target);

        RemoveIndex(entry.Folder);
        return target;
    }

    /// <summary>从磁盘删掉一个 mod 文件夹（带确认对话框由 UI 层负责）。</summary>
    public static void Delete(ModEntry entry)
    {
        RemoveIndex(entry.Folder);
        Directory.Delete(entry.Folder, recursive: true);
    }

    /*********
    ** 内部
    *********/
    private static void MoveFolder(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"移动文件夹失败（{from} → {to}）：{ex.Message}\n" +
                "常见原因：游戏正在运行占用了文件，或该文件夹被杀毒软件锁定。", ex);
        }
    }

    /// <summary>
    /// 拼出目标路径（parentDir 相对子目录支持多级，比如启用嵌套内容包时移回原位）。
    /// 目标已存在就给最后一段加 (2)、(3) 后缀。
    /// </summary>
    private static string GetUniqueTarget(string parentDir, string relativeName)
    {
        var target = Path.Combine(parentDir, relativeName);
        var targetParent = Path.GetDirectoryName(target)
            ?? throw new InvalidOperationException($"目标路径没有父目录：{target}");
        Directory.CreateDirectory(targetParent);

        for (int i = 2; Directory.Exists(target) || File.Exists(target); i++)
        {
            var fileName = Path.GetFileName(target);
            target = Path.Combine(Path.GetDirectoryName(target)!, $"{fileName} ({i})");
        }
        return target;
    }

    private static Dictionary<string, string> LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                           File.ReadAllText(IndexPath)) ?? [];
        }
        catch
        {
            // 索引读不出来不影响主流程，退化为放到 Mods 根目录
        }
        return [];
    }

    private static void SaveIndex(string currentPath, string originalRelative)
    {
        try
        {
            var index = LoadIndex();
            index[Normalize(currentPath)] = originalRelative;
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);
            File.WriteAllText(
                IndexPath,
                System.Text.Json.JsonSerializer.Serialize(index, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 索引写不进去不致命
        }
    }

    private static void RemoveIndex(string currentPath)
    {
        try
        {
            var index = LoadIndex();
            if (index.Remove(Normalize(currentPath)))
                File.WriteAllText(IndexPath, System.Text.Json.JsonSerializer.Serialize(index));
        }
        catch
        {
            // 索引删不掉不致命
        }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}

/*********
** 版本比较
*********/
/// <summary>
/// 宽容的 mod 版本比较。SMAPI mod 的版本号五花八门：
/// "1.6.15"、"2.11.0"、"1.26.0-win"、"3.0.0-beta.2"、"V1.2" 都可能见到。
/// 这里做纯数字分段比较，比较不了的部分用字符串兜底，别让一个奇怪的版本号把更新检查搞崩。
/// </summary>
public static class ModVersionUtil
{
    /// <summary>latest 比 installed 新返回 true。</summary>
    public static bool IsNewer(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
            return false;
        return Compare(installed, latest) < 0;
    }

    /// <summary>比较两个版本字符串，a&lt;b 返回负数。</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);

        int common = Math.Min(pa.Numeric.Length, pb.Numeric.Length);
        for (int i = 0; i < common; i++)
        {
            if (pa.Numeric[i] != pb.Numeric[i])
                return pa.Numeric[i].CompareTo(pb.Numeric[i]);
            var suffix = string.CompareOrdinal(pa.Suffixes[i], pb.Suffixes[i]);
            if (suffix != 0)
                return suffix;
        }

        // 数字段谁多谁新（1.2 之后能容纳 1.2.1）
        if (pa.Numeric.Length != pb.Numeric.Length)
            return pa.Numeric.Length.CompareTo(pb.Numeric.Length);

        // 数字部分完全一样才比预发布标记：正式版 > 预发布版（semver 规则）
        int pre = string.CompareOrdinal(pa.Prerelease, pb.Prerelease);
        if (pre != 0)
        {
            if (pa.Prerelease.Length == 0) return 1;
            if (pb.Prerelease.Length == 0) return -1;
            return pre;
        }
        return 0;
    }

    /// <summary>把版本字符串拆成数字段 + 尾段 + 预发布标记。</summary>
    private static (long[] Numeric, string[] Suffixes, string Prerelease) Parse(string? version)
    {
        var text = version?.Trim() ?? "";
        // "V1.2.3" / "v1.2.3" 前缀
        if (text.Length > 1 && (text[0] is 'v' or 'V') && char.IsDigit(text[1]))
            text = text[1..];

        // 丢掉构建号（+abc123.4）
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        // 拆出预发布（-beta.2）
        string prerelease = "";
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..].Trim();
            text = text[..dash];
        }

        var parts = text.Split('.');
        var numeric = new long[parts.Length];
        var suffixes = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].TrimStart('0', 'v'); // "01" → "1"
            int digits = 0;
            while (digits < part.Length && char.IsDigit(part[digits]))
                digits++;
            numeric[i] = digits > 0 ? long.Parse(part[..digits]) : -1;
            suffixes[i] = part[digits..];
        }
        return (numeric, suffixes, prerelease);
    }
}
