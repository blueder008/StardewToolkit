using System.IO.Compression;
using System.Text.RegularExpressions;

namespace StardewToolkit.Services;

/// <summary>一次 mod zip 安装的结果（单个 mod）。</summary>
public sealed record ModPackageInstallResult(
    bool Success,
    string ModName,
    string? ModId,
    string? InstalledFolder,
    string? BackupFolder,
    string? OldVersion,
    string? NewVersion,
    string Message);

/// <summary>
/// 一个 zip 的整体安装报告。函数式 zip（GitHub release、合并包）经常一次带多个 mod（或
/// 框架 mod 里嵌着内容包），这里逐个 mod 出结果，UI 汇总展示。
/// </summary>
public sealed record ModPackageInstallReport(
    bool Success,
    /// <summary>一行人话总结（「安装了 2 个 mod」/「失败：…」）。</summary>
    string Summary,
    IReadOnlyList<ModPackageInstallResult> Results)
{
    /// <summary>单 mod zip 时的主结果（调用方老 API 的便利字段）。</summary>
    public ModPackageInstallResult Primary =>
        Results.Count > 0
            ? Results[0]
            : new ModPackageInstallResult(false, "（空包）", null, null, null, null, null, "zip 里没有可安装的内容");
}

/// <summary>
/// 把一个 mod 安装包（zip）装进 Mods 目录。
///
/// 流程对齐社区通用做法（Stardrop 的手动安装同款）：
///   1. 在 zip 里找所有 manifest.json（根目录或任意子目录）；
///   2. 一个 zip 里有多个 mod 时逐个安装（同一 UniqueID 取 zip 里最浅的那份，重复的跳过）；
///   3. 嵌在别人的 mod 文件夹里的内容包不单独恢复（装父级时已包含）；
///   4. 每个 mod 按 manifest 的 UniqueID 找 Mods 里已装的同一个 mod：
///      启用中原位替换（旧目录移到 Mods/.backup/时间戳/），禁用中或全新则装到 Mods 根目录。
///
/// manifest 由调用方校验过才动手；zip-slip 路径穿越有防护；同时间只允许一个安装（UI 层控制）。
/// </summary>
public static class ModPackageInstaller
{
    /// <summary>zip 里一眼是垃圾的路径段（macOS 打包垃圾、Vortex 标记），解压时跳过。</summary>
    private static readonly string[] JunkSegments =
    [
        "__MACOSX",
        "__folder_managed_by_vortex",
        "desktop.ini",
        "thumbs.db",
        ".ds_store",
    ];

    /// <summary>从 zip 安装全部 mod。日志会写到 UI。</summary>
    public static Task<ModPackageInstallReport> InstallFromZipAsync(
        string zipPath, string modsDir, Action<string>? log = null, CancellationToken ct = default) =>
        Task.Run(() => InstallFromZip(zipPath, modsDir, log, ct), ct);

    private static ModPackageInstallReport InstallFromZip(
        string zipPath, string modsDir, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(modsDir);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries
            .Where(e => !IsJunkEntry(e.FullName))
            .Where(e => e.Name.Length > 0) // 目录型 entry 跳过
            .ToList();

        // 1. 找出 zip 里全部 manifest.json（排在最浅层的前面；解析失败的也记录）
        var manifests = FindManifests(entries, zipPath);
        if (manifests.Count == 0)
        {
            var result = Fail("zip 里没有 manifest.json",
                "这不是标准的 SMA mod 安装包（压缩根目录或子目录里都找不到 manifest.json）。",
                "（未知 mod）", null, null, null);
            return new ModPackageInstallReport(false, result.Message, [result]);
        }

        // 2. 同一个 UniqueID 在 zip 里出现多份（比如同时放了 1.0 和 2.0）：取最浅的一份，其余跳过
        var manifestsToInstall = new List<(ZipArchiveEntry Entry, string Prefix, ModManifest Manifest, string ZipLabel)>();
        var parseFailures = new List<ModPackageInstallResult>();
        var skippedDuplicates = new List<ModPackageInstallResult>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifests)
        {
            ct.ThrowIfCancellationRequested();
            if (m.Manifest is null)
            {
                parseFailures.Add(new ModPackageInstallResult(
                    Success: false, ModName: $"（{m.ZipLabel}）", ModId: null,
                    InstalledFolder: null, BackupFolder: null,
                    OldVersion: null, NewVersion: null,
                    Message: $"{m.ZipLabel} 的 manifest.json 解析失败——这个 mod 没装，其他不受影响。"));
                continue;
            }

            var id = m.Manifest.UniqueID?.Trim() ?? "";
            if (id.Length > 0 && !seenIds.Add(id))
            {
                skippedDuplicates.Add(new ModPackageInstallResult(
                    Success: false, ModName: m.Manifest.Name ?? "(未命名)", ModId: id,
                    InstalledFolder: null, BackupFolder: null,
                    OldVersion: null, NewVersion: null,
                    Message: $"ID 重复，zip 里已安装最浅层的版本，跳过 {m.Prefix} 的副本。"));
                continue;
            }
            manifestsToInstall.Add((m.Entry, m.Prefix, m.Manifest, m.ZipLabel));
        }

        // 3. 嵌套内容包（在另一个要安装的 mod 目录里面）不单独恢复，装父级时已经带上
        var nestedNotInstalled = manifestsToInstall
            .Where(x => manifestsToInstall.Any(p =>
                p.Prefix.Length > 0 && p.Prefix.Length < x.Prefix.Length
                && x.Prefix.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var installs = manifestsToInstall.Except(nestedNotInstalled).ToList();

        var results = new List<ModPackageInstallResult>();
        foreach (var install in installs)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(InstallOne(
                entries, install.Prefix, install.Manifest,
                modsDir, install.ZipLabel, log, ct));
        }

        // 嵌套的提示（随父级安装，不算错误；装父级时的日志已带上内容）
        var nestedSkips = new List<ModPackageInstallResult>();
        foreach (var skip in manifestsToInstall.Except(installs))
        {
            nestedSkips.Add(new ModPackageInstallResult(
                Success: true, ModName: skip.Manifest.Name ?? "（未命名）", ModId: skip.Manifest.UniqueID,
                InstalledFolder: null, BackupFolder: null, OldVersion: null, NewVersion: null,
                Message: $"内容包嵌在 {GetTopSegment(skip.Prefix)} 里，装那个 mod 时会一起带上，不单独安装。"));
        }

        var allResults = results.Concat(parseFailures).Concat(skippedDuplicates).Concat(nestedSkips).ToList();
        var okCount = results.Count(r => r.Success);
        var success = results.Count > 0 && okCount == results.Count;

        if (allResults.Count == 1)
            return new ModPackageInstallReport(
                allResults[0].Success,
                allResults[0].Success ? $"{allResults[0].ModName} 安装完成" : $"{allResults[0].ModName} 安装失败",
                allResults);

        var summary = results.Count > 0
            ? success
                ? $"{okCount} 个 mod 全部安装完成"
                : $"{okCount} 个安装成功 / {results.Count - okCount} 个失败"
            : "zip 里没有可独立安装的 mod（都是嵌套的或读不出 manifest）";
        return new ModPackageInstallReport(success, summary, allResults);
    }

    /*********
    ** zip 内 manifest 定位
    *********/
    private static List<(ZipArchiveEntry Entry, string Prefix, ModManifest? Manifest, string? ParseError, string ZipLabel)>
        FindManifests(List<ZipArchiveEntry> entries, string zipPath)
    {
        var found = new List<(ZipArchiveEntry, string, ModManifest?, string?, string)>();

        foreach (var item in entries
            .Where(e => string.Equals(e.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
            .Select(e => (entry: e, prefix: GetDirectoryPrefix(e.FullName)))
            .OrderBy(x => x.prefix.Length)) // 最浅的排前面（同 ID 时取浅层副本）
        {
            string manifestText;
            using (var stream = item.entry.Open())
            using (var reader = new StreamReader(stream))
                manifestText = reader.ReadToEnd();

            var manifest = ModManifestParser.Parse(manifestText);
            found.Add((item.entry, item.prefix, manifest,
                manifest is null ? "manifest.json 无法解析（内容不是合法 JSON）" : null,
                item.prefix.Length == 0 ? "（zip 根目录）" : item.prefix.TrimEnd('/')));
        }

        return found;
    }

    /*********
    ** 单个 mod 的安装（原单 mod 版的主体逻辑）
    *********/
    private static ModPackageInstallResult InstallOne(
        List<ZipArchiveEntry> entries, string prefix, ModManifest manifest,
        string modsDir, string zipLabel,
        Action<string>? log, CancellationToken ct)
    {
        string modId = manifest.UniqueID ?? "";
        string modName = string.IsNullOrWhiteSpace(manifest.Name) ? "（未命名 mod）" : manifest.Name!;
        string? oldVersion = null;

        try
        {
            // 1. 找已安装的同 ID mod（ModScanner 扫到即算，包括禁用区的）
            var installed = ModScanner.ScanAsync(modsDir, ct).Result // 同步等：安装器自己已经在后台线程
                .FirstOrDefault(m => string.Equals(m.UniqueId, modId, StringComparison.OrdinalIgnoreCase));

            string targetFolder;
            string? backupFolder = null;
            oldVersion = installed?.VersionText;

            // 已启用 mod：原目录原位替换，保留用户熟悉的名字
            if (installed is { Enabled: true })
            {
                targetFolder = installed.Folder;
                backupFolder = MoveOldToBackup(installed.Folder, modsDir, log);
            }
            else if (installed is not null)
            {
                // 旧版是禁用状态：装到 Mods 根目录并提示，旧目录照样备份腾位置（.disabled 里不回写，免得管理者状态混乱）
                targetFolder = Path.Combine(modsDir, SanitizeFolderName(installed.FolderName));
                backupFolder = MoveOldToBackup(installed.Folder, modsDir, log);
            }
            else
            {
                // 全新安装
                var zipFolderName = GetTopSegment(prefix);
                var baseName = !string.IsNullOrEmpty(zipFolderName)
                    ? zipFolderName
                    : SanitizeFolderName(modName);
                targetFolder = Path.Combine(modsDir, SanitizeFolderName(baseName));
            }

            for (int i = 2; Directory.Exists(targetFolder); i++)
                targetFolder = Path.Combine(modsDir, $"{SanitizeFolderName(Path.GetFileName(targetFolder.TrimEnd('\\')))} ({i})");

            Directory.CreateDirectory(targetFolder);

            // 2. 解压 mod 子树：manifest 所在目录就是 mod 根；根目录的 manifest 表示整个 zip 都是这个 mod
            log?.Invoke($"安装 {modName}（{zipLabel}）：解压到 {Path.GetFileName(targetFolder)}（旧版 {(backupFolder is null ? "无" : "已备份到 .backup")}）…");

            foreach (var sub in entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();

                var relative = sub.FullName[prefix.Length..];
                if (relative.Length == 0)
                    continue;

                var target = Path.Combine(targetFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                var full = Path.GetFullPath(target);
                if (!full.StartsWith(Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue; // zip-slip 防护：路径跑出目标目录的直接丢

                if (sub.FullName.EndsWith("/", StringComparison.Ordinal) || sub.Name.Length == 0)
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                sub.ExtractToFile(full, overwrite: true);
            }

            // 3. 校验：安装目录里 manifest 要能读出来
            var newManifest = ModManifestParser.ParseFileAsync(Path.Combine(targetFolder, "manifest.json"), ct).Result;
            if (newManifest is null)
            {
                return new ModPackageInstallResult(
                    Success: false, ModName: modName, ModId: modId,
                    InstalledFolder: targetFolder, BackupFolder: backupFolder,
                    OldVersion: oldVersion, NewVersion: null,
                    Message: "解压完成但 manifest.json 读不出来，已保留目录供排查（可能是 zip 损坏）。");
            }

            log?.Invoke($"✅ {modName} 安装完成：{Path.GetFileName(targetFolder)}（{manifest.Version}）");

            return new ModPackageInstallResult(
                Success: true, ModName: modName, ModId: modId,
                InstalledFolder: targetFolder, BackupFolder: backupFolder,
                OldVersion: oldVersion, NewVersion: manifest.Version,
                Message: backupFolder is null ? "全新安装" : "已替换旧版本（旧版备份在 Mods/.backup 下）");
        }
        catch (OperationCanceledException)
        {
            return new ModPackageInstallResult(false, modName, modId, null, null, oldVersion, null, "已取消。");
        }
        catch (Exception ex)
        {
            return new ModPackageInstallResult(false, modName, modId, null, null, oldVersion, null, $"安装失败：{ex.Message}");
        }
    }

    /*********
    ** 内部
    *********/
    /// <summary>把旧的 mod 目录移到 Mods/.backup/<时间戳>/（同目录避免跨盘，点开头不会被 SMAPI 加载）。返回备份位置。</summary>
    private static string? MoveOldToBackup(string oldFolder, string modsDir, Action<string>? log)
    {
        try
        {
            var backupRoot = Path.Combine(modsDir, ".backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backupRoot);
            var backupPath = Path.Combine(backupRoot, Path.GetFileName(oldFolder.TrimEnd('\\')));
            for (int i = 2; Directory.Exists(backupPath); i++)
                backupPath = Path.Combine(backupRoot, $"{Path.GetFileName(oldFolder.TrimEnd('\\'))} ({i})");

            Directory.Move(oldFolder, backupPath);
            log?.Invoke($"旧版本已备份：{Path.GetRelativePath(modsDir, backupPath)}");
            return backupPath;
        }
        catch (Exception ex)
        {
            // 备份失败一般因为目标位置被占用（残留文件夹锁着）；换个全新目录名继续装
            log?.Invoke($"⚠ 旧版本备份失败（{ex.Message}），新版本将装到新的目录以避开冲突。");
            return null;
        }
    }

    /// <summary>manifest entry 在 zip 里的目录前缀（含尾部 /；根目录时为空）。</summary>
    private static string GetDirectoryPrefix(string entryFullName)
    {
        var dir = Path.GetDirectoryName(entryFullName.Replace('/', '\\')) ?? "";
        return dir.Length == 0 ? "" : dir.Replace('\\', '/') + "/";
    }

    /// <summary>mod 根目录在 zip 里最外一层的哪一段（"SomeMod/" → "SomeMod"；空表示根目录）。</summary>
    private static string GetTopSegment(string prefix)
    {
        if (prefix.Length == 0)
            return "";
        return prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).First();
    }

    private static bool IsJunkEntry(string fullName)
    {
        var segments = fullName.Split('/');
        foreach (var segment in segments)
        {
            if (JunkSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
                return true;
            if (segment.StartsWith("._", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>把字符串变成安全的 Windows 文件夹名（去非法字符，空名退化为 unnamed）。</summary>
    private static string SanitizeFolderName(string name)
    {
        var invalid = new Regex(@"[\\/:*?""<>|]");
        var clean = invalid.Replace(name.Trim(), "").Trim();
        foreach (var reserved in new[] { "CON", "PRN", "AUX", "NUL" })
            if (string.Equals(clean, reserved, StringComparison.OrdinalIgnoreCase))
                clean += "_";
        return clean.Length == 0 ? "unnamed" : clean;
    }

    private static ModPackageInstallResult Fail(
        string title, string detail, string modName, string? oldVersion, string? newVersion, string? modId) =>
        new(false, modName, modId, null, null, oldVersion, newVersion, $"{title}：{detail}");
}
