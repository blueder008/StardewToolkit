namespace StardewToolkit.Services;

/// <summary>备份目录里的一个条目（备份下来的一个 mod 文件夹）。</summary>
public sealed class BackupItem
{
    /// <summary>文件夹名（也是恢复出来时的默认名字）。</summary>
    public required string Name { get; init; }

    /// <summary>备份里的完整路径。</summary>
    public required string SourceFolder { get; init; }

    /// <summary>文件夹完整内容的大小（字节，递归统计）。</summary>
    public required long SizeBytes { get; init; }

    /// <summary>manifest 里读到的 mod 名（读不到就 null）。</summary>
    public string? ModName { get; init; }

    /// <summary>manifest 的 UniqueID（读不到就 null）。</summary>
    public string? ModId { get; init; }

    /// <summary>manifest 的版本（读不到就 null）。</summary>
    public string? ModVersion { get; init; }

    /// <summary>备份文件夹不是 mod（没有 manifest）时的说明。</summary>
    public string? Remark => ModName is null ? "，没有 manifest.json" : null;

    public string SizeText => FormatBytes(SizeBytes);

    internal static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        string[] units = ["KB", "MB", "GB"];
        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>一份快照 = Mods/.backup/ 下按时间戳命名的一个目录（ModPackageInstaller 替换旧版时写入）。</summary>
public sealed class BackupSnapshot
{
    /// <summary>快照目录的完整路径。</summary>
    public required string Folder { get; init; }

    /// <summary>目录名（yyyyMMdd-HHmmss）。</summary>
    public required string RawName { get; init; }

    /// <summary>从目录名解析出的时间；不是合法时间戳（别的工具写的）就 null。</summary>
    public required DateTime? Time { get; init; }

    public required long SizeBytes { get; init; }
    public required List<BackupItem> Items { get; init; }

    public string DisplayName => Time?.ToString("yyyy-MM-dd HH:mm:ss") ?? RawName;
    public string SizeText => BackupItem.FormatBytes(SizeBytes);
}

/// <summary>恢复时遇到 mod 名冲突的处理方式。</summary>
public enum BackupRestoreMode
{
    /// <summary>回滚：把当前已装的版本（同 ID 优先，否则同名目录）存进新的备份快照，再恢复旧版。</summary>
    Replace,

    /// <summary>并存：加（2）编号后缀一起恢复（SMAPI 只会加载其中一个，事后自己取舍）。</summary>
    KeepBoth,
}

/// <summary>
/// Mods/.backup/ 快照的管理：列举、恢复（支持「回滚旧版」与「并存」两种冲突策略）、删除。
///
/// 备份语义与 ModPackageInstaller 写入的一致（替换旧版时先把旧目录移进 Mods/.backup/时间戳/），
/// 里面每个一级子目录就是备份下来的一个 mod 文件夹，恢复 = 把它移回 Mods。
/// </summary>
public static class BackupManager
{
    /// <summary>备份根目录：Mods/.backup（点开头，SMAPI 不会当 mod 加载）。</summary>
    public static string BackupRoot(string modsDir) => Path.Combine(modsDir, ".backup");

    /// <summary>后台线程列举所有快照（含条目清单与大小统计）。</summary>
    public static Task<List<BackupSnapshot>> ListSnapshotsAsync(string modsDir, CancellationToken ct = default) =>
        Task.Run(() => ListSnapshots(modsDir, ct), ct);

    private static List<BackupSnapshot> ListSnapshots(string modsDir, CancellationToken ct)
    {
        var snapshots = new List<BackupSnapshot>();
        var root = new DirectoryInfo(BackupRoot(modsDir));
        if (!root.Exists)
            return snapshots;

        List<DirectoryInfo> folders;
        try
        {
            folders = root.EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return snapshots; // 备份根读不了就当没有
        }

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            var items = new List<BackupItem>();
            long size = 0;

            List<FileSystemInfo> children;
            try
            {
                children = folder.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue; // 这个快照目录读不了就跳过
            }

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                long childSize = GetTreeSize(child.FullName);
                size += childSize;
                if (child is DirectoryInfo)
                    items.Add(ReadItem(child.FullName, child.Name, childSize));
            }

            // 时间戳解析不了也能列出（按名字排序展示），不丢弃别的工具写的备份目录
            var time = DateTime.TryParseExact(
                folder.Name, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed : (DateTime?)null;

            snapshots.Add(new BackupSnapshot
            {
                Folder = folder.FullName,
                RawName = folder.Name,
                Time = time,
                SizeBytes = size,
                Items = items,
            });
        }

        return snapshots;
    }

    /// <summary>读一个备份 mod 的 manifest（读不到不致命，只是 UI 少显示点信息）。</summary>
    private static BackupItem ReadItem(string folder, string name, long sizeBytes)
    {
        ModManifest? manifest = null;
        try
        {
            manifest = ModManifestParser.ParseFileAsync(
                Path.Combine(folder, "manifest.json")).GetAwaiter().GetResult();
        }
        catch
        {
            // manifest 读不了就算了
        }

        return new BackupItem
        {
            Name = name,
            SourceFolder = folder,
            SizeBytes = sizeBytes,
            ModName = manifest?.Name,
            ModId = manifest?.UniqueID,
            ModVersion = manifest?.Version,
        };
    }

    /*********
    ** 恢复
    *********/
    /// <summary>恢复结果。</summary>
    public sealed record RestoreResult(bool Success, string Message);

    /// <summary>
    /// 恢复一个备份条目到 Mods 根目录。冲突策略见 <see cref="BackupRestoreMode"/>。
    /// 恢复的目录可能与已装 mod 平级并存，SMAPI 的重复 ID 界面会在下次扫描时提示。
    /// </summary>
    public static async Task<RestoreResult> RestoreItemAsync(
        string modsDir, BackupItem item, BackupRestoreMode mode, Action<string>? log = null)
    {
        if (!Directory.Exists(item.SourceFolder))
            return new RestoreResult(false, $"备份条目已不存在：{item.Name}");

        var conflict = await FindConflictAsync(modsDir, item);
        string target;

        try
        {
            if (conflict is not null && mode == BackupRestoreMode.Replace)
            {
                var archived = MoveCurrentToBackup(modsDir, conflict.Folder, log);
                log?.Invoke($"当前已装的 {Path.GetFileName(conflict.Folder.TrimEnd('\\'))} 已转移到 {archived}");
            }

            target = mode == BackupRestoreMode.Replace || conflict is null
                ? Path.Combine(modsDir, item.Name)
                : UniquePath(Path.Combine(modsDir, item.Name));

            Directory.Move(item.SourceFolder, target);
        }
        catch (Exception ex)
        {
            return new RestoreResult(false,
                $"移动文件夹失败：{ex.Message}\n常见原因：游戏正在运行占用了文件，或杀毒软件锁定。");
        }

        return new RestoreResult(true, $"已恢复到 {Path.GetRelativePath(modsDir, target)}");
    }

    /// <summary>恢复一份快照里的全部条目（逐条按同一策略处理）。返回 (恢复成功数, 失败数)。</summary>
    public static async Task<(int Restored, int Failed)> RestoreSnapshotAsync(
        string modsDir, BackupSnapshot snapshot, BackupRestoreMode mode, Action<string>? log = null)
    {
        int restored = 0, failed = 0;
        foreach (var item in snapshot.Items.ToList())
        {
            var result = await RestoreItemAsync(modsDir, item, mode, log);
            if (result.Success)
                restored++;
            else
            {
                failed++;
                log?.Invoke($"⚠ {item.Name}：{result.Message}");
            }
        }
        return (restored, failed);
    }

    /*********
    ** 删除
    *********/
    /// <summary>删除一份快照进回收站（可在回收站找回）。modsDir 用来说明快照确实在 Mods/.backup 下。</summary>
    public static void DeleteSnapshot(string modsDir, BackupSnapshot snapshot)
    {
        if (!snapshot.Folder.StartsWith(
                BackupRoot(modsDir) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("快照路径不在 Mods/.backup 下，拒绝删除。");
        RecycleDirectory(snapshot.Folder);
    }

    /// <summary>清空所有备份：逐份快照移进回收站（每份都可在回收站单独找回）。返回快照份数（供提示语用）。</summary>
    public static int DeleteAllBackups(string modsDir)
    {
        var root = BackupRoot(modsDir);
        if (!Directory.Exists(root))
            return 0;

        int count = 0;
        foreach (var folder in Directory.EnumerateDirectories(root).ToList())
        {
            RecycleDirectory(folder);
            count++;
        }
        return count;
    }

    /// <summary>把一个目录移进回收站（SHFileOperation 的 FOF_ALLOWUNDO 路径，长目录树也能整棵还原）。</summary>
    public static void RecycleDirectory(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /*********
    ** 内部
    *********/
    /// <summary>
    /// 找恢复会撞上的已装 mod：优先同 UniqueID 的已启用 mod（旧版回滚的目标），
    /// 其次是 Mods 根目录下的同名文件夹。撞不上返回 null。
    /// </summary>
    private static async Task<ModEntry?> FindConflictAsync(string modsDir, BackupItem item)
    {
        var installed = await ModScanner.ScanAsync(modsDir);

        // 先看已启用的里有没有同一个 mod（回滚语义要替换的就是它）
        if (!string.IsNullOrWhiteSpace(item.ModId))
        {
            var byId = installed.FirstOrDefault(m =>
                m.Enabled
                && string.Equals(m.UniqueId, item.ModId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        // 同名文件夹（比如备份时 manifest 已坏、读不到 ID）
        var root = Path.Combine(modsDir, item.Name);
        return Directory.Exists(root)
            ? installed.FirstOrDefault(m => string.Equals(
                Path.GetFullPath(m.Folder), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>把当前已装的一份移进新的备份时间戳目录（复制 MoveOldToBackup 的约定）。</summary>
    private static string MoveCurrentToBackup(string modsDir, string currentFolder, Action<string>? log)
    {
        var backupRoot = Path.Combine(modsDir, ".backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backupRoot);
        var backupPath = Path.Combine(backupRoot, Path.GetFileName(currentFolder.TrimEnd('\\')));
        for (int i = 2; Directory.Exists(backupPath); i++)
            backupPath = Path.Combine(backupRoot, $"{Path.GetFileName(currentFolder.TrimEnd('\\'))} ({i})");

        Directory.Move(currentFolder, backupPath);
        log?.Invoke($"已备份当前版本：{Path.GetRelativePath(modsDir, backupPath)}");
        return backupPath;
    }

    /// <summary>递归统计一个文件夹/文件的大小（读不了的子树跳过，别让一个怀子目录卡住列举）。</summary>
    private static long GetTreeSize(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }

        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* 单个文件读不了就跳过 */ }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    stack.Push(sub);
            }
            catch
            {
                // 子目录读不了就跳过
            }
        }
        return total;
    }

    /// <summary>目标已存在就用 (2)、(3) 编号让位（与安装器的命名约定一致）。</summary>
    private static string UniquePath(string path)
    {
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++)
            path = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileName(path)} ({i})");
        return path;
    }
}
