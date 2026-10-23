using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// Mod 管理器窗口：
/// 扫描 Mods 目录 → 解析 manifest → 展示启停/更新状态/兼容性 → 启停（移动文件夹）。
///
/// 更新检查走 smapi.io 的官方 web API（免 apiKey、批量查全部 update key 来源），
/// 启停用 SMAPI 官方约定（点开头的文件夹被忽略），语义与 Stardrop / SMAPI 保持一致。
/// </summary>
internal sealed class ModManagerForm : Form
{
    /*********
    ** 状态
    *********/
    private readonly string _gamePath;
    private string ModsDir => Path.Combine(_gamePath, "Mods");

    /// <summary>打开本窗口前想聚焦的 mod 名（日志查看器联动跳转用）；扫描完成后顶到筛选框里。</summary>
    private readonly string? _focusMod;

    private List<ModEntry> _mods = [];
    private readonly Dictionary<string, ModUpdateData> _updateMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _focusApplied;

    private readonly SmapiModCheckClient _client = new();
    private readonly GitHubModUpdateService _gitHub = new();
    private AppConfig _config = AppConfig.Load();

    /*********
    ** 控件
    *********/
    private TextField _filterField = null!;
    private ModernCheckBox _showDisabled = null!;
    private ModernButton _btnRescan = null!;
    private ModernButton _btnCheckUpdates = null!;
    private ModernButton _btnToggle = null!;
    private ModernButton _btnOpenFolder = null!;
    private ModernButton _btnOpenPage = null!;
    private ModernButton _btnDelete = null!;
    private ModernButton _btnOpenModsDir = null!;
    private ModernButton _btnInstallZip = null!;
    private ModernButton _btnAutoUpdate = null!;
    private ModernButton _btnBackups = null!;
    private ModernButton _btnConflicts = null!;
    private ThemedListView _list = null!;
    private Label _summary = null!;
    private Pill _pillOn = null!;
    private Pill _pillUpdates = null!;
    private Pill _pillBroken = null!;
    private Pill _pillDisabled = null!;
    private LogPanel _logPanel = null!;

    public ModManagerForm(string gamePath, string? focusMod = null)
    {
        _gamePath = gamePath;
        _focusMod = string.IsNullOrWhiteSpace(focusMod) ? null : focusMod.Trim();

        Theme.StyleForm(this);
        Text = $"Mod 管理器 — {Path.GetFileName(gamePath.TrimEnd('\\'))}";
        MinimumSize = new Size(1040, 700);
        Size = new Size(1220, 820);
        StartPosition = FormStartPosition.CenterParent;

        BuildUi();
        Shown += async (_, _) => await RescanAsync();
    }

    /*********
    ** 布局
    *********/
    private void BuildUi()
    {
        var hero = BuildHero();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(18, 14, 18, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 0: 筛选
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 1: 主操作
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));   // 2: 条目操作
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 3: 列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));   // 4: 统计
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));  // 5: 日志

        // 0: 筛选
        var filterRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 6),
        };
        _filterField = new TextField("输入名称 / 作者 / MOD ID")
        {
            Width = 300,
            Height = 36,
            Margin = new Padding(0, 0, 14, 0),
        };
        _filterField.Box.TextChanged += (_, _) => RefreshList();
        filterRow.Controls.Add(_filterField);

        _showDisabled = new ModernCheckBox
        {
            Text = "显示已禁用",
            Checked = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        _showDisabled.CheckedChanged += (_, _) => RefreshList();
        filterRow.Controls.Add(_showDisabled);

        filterRow.Controls.Add(new Label
        {
            Text = "提示：双击条目直接打开所在文件夹",
            AutoSize = true,
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(18, 10, 0, 0),
        });
        root.Controls.Add(filterRow, 0, 0);

        // 1: 主操作
        var primaryRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 6),
        };
        _btnCheckUpdates = MakeButton("检查更新", ButtonVariant.Primary, 130, 0);
        _btnCheckUpdates.Font = Theme.Ui(9.5f, FontStyle.Bold);
        _btnCheckUpdates.Click += async (_, _) => await CheckUpdatesAsync();
        _btnAutoUpdate = MakeButton("自动更新", ButtonVariant.Secondary, 110);
        _btnAutoUpdate.Click += async (_, _) => await AutoUpdateSelectedAsync();
        _btnInstallZip = MakeButton("安装 zip…", ButtonVariant.Secondary, 110);
        _btnInstallZip.Click += async (_, _) => await InstallZipAsync();
        _btnRescan = MakeButton("重新扫描", ButtonVariant.Ghost, 100);
        _btnRescan.Click += async (_, _) => await RescanAsync();
        primaryRow.Controls.AddRange([_btnCheckUpdates, _btnAutoUpdate, _btnInstallZip, _btnRescan]);
        root.Controls.Add(primaryRow, 0, 1);

        // 2: 条目操作
        var itemRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 6),
        };
        _btnToggle = MakeButton("禁用所选", ButtonVariant.Secondary, 110, 0);
        _btnToggle.Click += async (_, _) => await ToggleSelectedAsync();
        _btnOpenFolder = MakeButton("打开文件夹", ButtonVariant.Secondary, 110);
        _btnOpenFolder.Click += (_, _) => OpenSelectedFolder();
        _btnOpenPage = MakeButton("更新页面", ButtonVariant.Secondary, 105);
        _btnOpenPage.Click += (_, _) => OpenSelectedPage();
        _btnDelete = MakeButton("删除…", ButtonVariant.Danger, 88);
        _btnDelete.Click += async (_, _) => await DeleteSelectedAsync();
        _btnBackups = MakeButton("备份管理…", ButtonVariant.Secondary, 112);
        _btnBackups.Click += async (_, _) => await ShowBackupManagerAsync();
        _btnConflicts = MakeButton("冲突检测", ButtonVariant.Secondary, 105);
        _btnConflicts.Click += async (_, _) => await RunConflictCheckAsync();
        itemRow.Controls.AddRange([_btnToggle, _btnOpenFolder, _btnOpenPage, _btnDelete, _btnBackups, _btnConflicts]);
        root.Controls.Add(itemRow, 0, 2);

        // 3: 列表
        var listCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            Padding = new Padding(1),
            Margin = new Padding(0, 2, 0, 0),
        };
        _list = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = true,
            ShowStatusDot = true,
        };
        _list.Columns.AddRange(
        [
            new ColumnHeader { Text = "Mod 名称", Width = 240 },
            new ColumnHeader { Text = "已装版本", Width = 90 },
            new ColumnHeader { Text = "最新版本", Width = 95 },
            new ColumnHeader { Text = "状态", Width = 320 },
            new ColumnHeader { Text = "作者", Width = 140 },
            new ColumnHeader { Text = "MOD ID", Width = 190 },
            new ColumnHeader { Text = "位置", Width = 220 },
        ]);
        _list.DoubleClick += (_, _) => OpenSelectedFolder();
        _list.SelectedIndexChanged += (_, _) => SyncActionButtons();
        BuildContextMenu();
        listCard.Controls.Add(_list);
        root.Controls.Add(listCard, 0, 3);

        // 4: 统计
        var summaryRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 0),
        };
        _summary = new Label
        {
            Text = "扫描中…",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(2, 6, 12, 0),
        };
        _pillOn = new Pill("在用 0", Theme.Success) { Margin = new Padding(0, 2, 6, 0) };
        _pillUpdates = new Pill("可更新 0", Theme.Accent) { Margin = new Padding(0, 2, 6, 0) };
        _pillBroken = new Pill("异常 0", Theme.Danger) { Margin = new Padding(0, 2, 6, 0) };
        _pillDisabled = new Pill("已禁用 0", Theme.TextMuted) { Margin = new Padding(0, 2, 6, 0) };
        summaryRow.Controls.AddRange([_summary, _pillOn, _pillUpdates, _pillBroken, _pillDisabled]);
        root.Controls.Add(summaryRow, 0, 4);

        // 5: 日志
        _logPanel = new LogPanel("操作日志") { Dock = DockStyle.Fill };
        root.Controls.Add(_logPanel, 0, 5);

        Controls.Add(root);
        Controls.Add(hero);
    }

    private HeroPanel BuildHero()
    {
        var hero = new HeroPanel { Dock = DockStyle.Top, Height = 92 };

        hero.Controls.Add(new LogoBadge
        {
            Location = new Point(20, 19),
            Size = new Size(52, 52),
        });
        hero.Controls.Add(new Label
        {
            Text = "Mod 管理器",
            Font = Theme.Ui(16f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(88, 15),
        });
        var path = new Label
        {
            Text = ModsDir,
            AutoSize = false,
            Bounds = new Rectangle(90, 46, 560, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            Font = Theme.Mono(8.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        hero.Controls.Add(path);
        // 右侧按钮区固定 430px + 20px 边距；路径标签绝不能延伸到按钮下方（透明标签会盖住按钮绘制）
        void FitPath() => path.Width = Math.Max(160, hero.Width - 90 - 450);
        hero.Resize += (_, _) => FitPath();
        FitPath();

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 430,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 29, 20, 0),
        };
        var btnNexusKey = new ModernButton
        {
            Name = "btnNexusKey",
            Text = "Nexus 密钥…",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(8, 0, 0, 0),
        };
        btnNexusKey.Click += (_, _) => ShowNexusKeyDialog();
        SyncNexusButton(btnNexusKey);

        var btnViewLog = new ModernButton
        {
            Text = "日志…",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(8, 0, 0, 0),
        };
        btnViewLog.Click += (_, _) => new LogViewerForm().ShowDialog(this);

        _btnOpenModsDir = new ModernButton
        {
            Text = "打开 Mods 目录",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            Height = 32,
            Margin = new Padding(0),
        };
        _btnOpenModsDir.Click += (_, _) => TryOpenModsDir();

        right.Controls.Add(btnNexusKey);
        right.Controls.Add(btnViewLog);
        right.Controls.Add(_btnOpenModsDir);
        hero.Controls.Add(right);

        return hero;
    }

    private static ModernButton MakeButton(string text, ButtonVariant variant, int width, int leftGap = 8) => new()
    {
        Text = text,
        Variant = variant,
        AutoSize = true,
        MinimumSize = new Size(width, 32),
        Height = 32,
        Margin = new Padding(leftGap, 0, 0, 0),
    };

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        Theme.StyleMenu(menu);
        menu.Items.Add("禁用 / 启用", null, async (_, _) => await ToggleSelectedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开所在文件夹", null, (_, _) => OpenSelectedFolder());
        menu.Items.Add("打开更新页面", null, (_, _) => OpenSelectedPage());
        menu.Items.Add("复制 MOD ID", null, (_, _) => CopySelectedId());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除…", null, async (_, _) => await DeleteSelectedAsync());
        _list.ContextMenuStrip = menu;
    }

    /*********
    ** 扫描
    *********/
    private async Task RescanAsync()
    {
        if (_busy)
            return;

        if (!Directory.Exists(ModsDir))
        {
            Log($"⚠ 目录不存在：{ModsDir}");
            Log("SMAPI 还没装的话先回主窗口装一次 SMAPI，装完后这里才有 Mods 目录。");
            _summary.Text = "没有可用的 Mods 目录";
            return;
        }

        SetBusy(true);
        try
        {
            Log($"正在扫描 {ModsDir} …");
            _mods = await ModScanner.ScanAsync(ModsDir);
            ApplyUpdateCache();
            RefreshList();

            int mods = _mods.Count(m => m.Type is ModEntryType.Smapi or ModEntryType.ContentPack && m.Error is null);
            int packs = _mods.Count(m => m.Type == ModEntryType.ContentPack && m.Error is null);
            int disabled = _mods.Count(m => !m.Enabled && !m.IsBroken);
            int broken = _mods.Count(m => m.Error is not null);
            Log($"扫描完成：{mods} 个 mod（其中 {packs} 个内容包）、{disabled} 个已禁用、{broken} 个异常目录。");

            // 日志查看器联动：把要定位的 mod 名顶进筛选框并选中对应行
            if (_focusMod is not null && !_focusApplied)
            {
                _focusApplied = true;
                _filterField.Box.Text = _focusMod;
                if (_list.Items.Count > 0)
                {
                    _list.Items[0].Selected = true;
                    _list.Items[0].Focused = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"❌ 扫描失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /*********
    ** 更新检查
    *********/
    private async Task CheckUpdatesAsync()
    {
        if (_busy)
            return;

        SetBusy(true);
        try
        {
            var candidates = _mods
                .Where(m => m.Enabled && m.Error is null && m.Manifest is { } && !string.IsNullOrEmpty(m.UniqueId))
                .Where(m => (m.Manifest!.UpdateKeys?.Count ?? 0) > 0)
                .ToList();

            if (candidates.Count == 0)
            {
                Log("没有带更新键的 mod 可查（绝大多数 mod 的 manifest 里会写 UpdateKeys 字段）。");
                return;
            }

            var entries = candidates
                .Select(m => (
                    Id: m.UniqueId!,
                    InstalledVersion: string.IsNullOrWhiteSpace(m.VersionText) ? "" : m.VersionText,
                    Keys: (IReadOnlyList<string>)m.Manifest!.UpdateKeys!))
                .ToList();

            string? apiVersion = GameInspector.GetSmapiVersion(_gamePath)?.ToString();
            string? gameVersion = GameInspector.GetGameVersion(_gamePath)?.ToString();

            try
            {
                var result = await _client.CheckAsync(apiVersion, gameVersion, entries, Log);
                foreach (var (id, data) in result)
                    _updateMap[id] = data;

                ApplyUpdateCache();
                RefreshList();

                int updates = _mods.Count(m => m.HasUpdate);
                int brokenCompat = _mods.Count(m => IsBadCompatibility(m));
                Log($"✅ 更新检查完成：{updates} 个 mod 有版本的更新可用，{brokenCompat} 个与当前游戏版本不兼容。");
                Log("提示：绝大多数 mod 直接去它们的官方页面（Nexus / GitHub）下载新版 zip 解压覆盖即可。");
            }
            catch (Exception ex)
            {
                Log($"❌ 更新检查失败：{ex.Message}");
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyUpdateCache()
    {
        foreach (var entry in _mods)
        {
            entry.Update = entry.UniqueId is { } id && _updateMap.TryGetValue(id, out var data)
                ? data
                : null;
        }
    }

    /*********
    ** 启用 / 禁用 / 删除
    *********/
    private ModEntry? SelectedEntry =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ModEntry : null;

    private async Task ToggleSelectedAsync()
    {
        if (_busy)
            return;

        var entry = SelectedEntry;
        if (entry is null)
        {
            Msg.Show(this, "先在列表里选一个 mod。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (entry.IsBroken && entry.Enabled)
        {
            Msg.Show(this,
                "这个目录本身无效（没有 manifest.json），SMAPI 本来就不会加载它，\n" +
                "不需要禁用。确认不要了可以用「删除…」清掉。",
                "无效目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this,
                "游戏或 SMAPI 正在运行，Mod 可能正在被占用。\n请先完全退出游戏再启停 Mod。",
                "游戏运行中", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (entry.Enabled)
        {
            // 禁用前置提示：别人依赖它 / 它是框架
            var dependents = _mods
                .Where(m => m.Enabled && m.WillLoad && RequiresMod(m, entry.UniqueId))
                .Select(m => m.Name)
                .ToList();
            if (dependents.Count > 0)
            {
                var warn = $"以下 {dependents.Count} 个 mod 依赖「{entry.Name}」，禁用它以后它们也无法加载：\n\n" +
                           $"{string.Join("、", dependents.Take(8))}{(dependents.Count > 8 ? " 等" : "")}\n\n仍要禁用吗？";
                if (Msg.Show(this, warn, "存在依赖", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
            }
        }

        _busy = true;
        SetBusy(true);
        try
        {
            var target = entry.Enabled
                ? ModFolderMover.Disable(entry)
                : ModFolderMover.Enable(entry);
            Log($"✅ 已{(entry.Enabled ? "禁用" : "启用")} {entry.Name}：{target}");

            await RescanSilentAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ 操作失败：{ex.Message}");
            Msg.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private static bool RequiresMod(ModEntry mod, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        return (mod.Manifest?.Dependencies ?? [])
            .Where(d => d.IsRequired)
            .Any(d => string.Equals(d.UniqueID, id, StringComparison.OrdinalIgnoreCase))
            || string.Equals(mod.Manifest?.ContentPackFor?.UniqueID, id, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DeleteSelectedAsync()
    {
        if (_busy)
            return;

        var entry = SelectedEntry;
        if (entry is null)
        {
            Msg.Show(this, "先在列表里选一个 mod 或无效目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏正在运行，先完全退出再删除文件。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = Msg.Show(this,
            $"确定要彻底删除这个文件夹吗？删了就找不回来了（不会进回收站）：\n\n{entry.Folder}",
            "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        _busy = true;
        SetBusy(true);
        try
        {
            ModFolderMover.Delete(entry);
            Log($"🗑 已删除 {entry.Folder}");
            await RescanSilentAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ 删除失败：{ex.Message}");
            Msg.Show(this, $"删除失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    /// <summary>启停/删除之后重新扫描，但不重复刷屏（成功消息已经打过）。</summary>
    private async Task RescanSilentAsync()
    {
        _mods = await ModScanner.ScanAsync(ModsDir);
        ApplyUpdateCache();
        RefreshList();
    }

    /*********
    ** 安装 zip / 自动更新
    *********/
    /// <summary>
    /// 「安装 zip…」：任何渠道下载的（Nexus 官方下载 / 更新页面箱子 / GitHub 都行），
    /// 程序按 manifest 的 UniqueID 自动找到已装的旧版本替换，不靠用户人肉对文件夹。
    /// </summary>
    private async Task InstallZipAsync()
    {
        if (_busy)
            return;

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，Mod 文件可能被占用。\n请先完全退出游戏再安装。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择 mod 安装包 zip（Nexus / GitHub / 更新页面下载的都可以）",
            Filter = "Mod 安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _busy = true;
        SetBusy(true);
        try
        {
            Log($"—— 安装 zip：{Path.GetFileName(dialog.FileName)} ——");
            var report = await ModPackageInstaller.InstallFromZipAsync(dialog.FileName, ModsDir, Log);
            Log(report.Success ? $"✅ {report.Summary}" : $"❌ {report.Summary}");
            foreach (var r in report.Results.Where(r => !r.Success || r.BackupFolder is not null && r.OldVersion is not null))
                Log($"· {r.ModName}：{(r.Success ? $"{r.OldVersion ?? "首次安装"} → {r.NewVersion}" : r.Message)}");
            if (!report.Success)
                Msg.Show(this, report.Summary +
                    (report.Results.Count > 1 ? $"\n\n详情见下方日志。" : $"\n\n{report.Results[0].Message}"),
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);

            await RescanSilentAsync();
        }
        catch (Exception ex)
        {
            Log($"❌ 安装失败：{ex.Message}");
            Msg.Show(this, $"安装失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    /// <summary>
    /// 打开备份管理小窗；有恢复/删除操作的话回主列表重新扫描一遍。
    /// </summary>
    private async Task ShowBackupManagerAsync()
    {
        if (_busy)
            return;

        using var dialog = new BackupManagerForm(ModsDir);
        dialog.ShowDialog(this);
        if (dialog.ChangedSomething)
        {
            Log("—— Mods 目录有变动（备份恢复过），已重新扫描 ——");
            SetBusy(true);
            try
            {
                await RescanSilentAsync();
            }
            finally
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>
    /// 冲突检测：把扫描列表交给 ModConflictDetector（重复 ID / XNB 资源覆盖 / CP 同 Target），结果弹窗展示。
    /// </summary>
    private async Task RunConflictCheckAsync()
    {
        if (_busy)
            return;

        SetBusy(true);
        try
        {
            var groups = await ModConflictDetector.DetectAsync(_mods);
            Log(groups.Count == 0
                ? "✅ 冲突检测：启用的 mod 之间没发现资源冲突。"
                : $"⚠ 冲突检测：发现 {groups.Count} 组「多个 mod 改同一份资源」的问题。");

            using var dialog = new ConflictReportForm(groups);
            dialog.ShowDialog(this);
            if (dialog.ChangedSomething)
            {
                Log("—— 冲突处理删过 mod，已重新扫描 ——");
                SetBusy(true);
                try
                {
                    await RescanSilentAsync();
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"❌ 冲突检测失败：{ex.Message}");
            Msg.Show(this, $"冲突检测失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>打开 Nexus key 配置小窗；保存成功后刷新配置和按钮文字。</summary>
    private void ShowNexusKeyDialog()
    {        using var dialog = new NexusKeyDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _config = AppConfig.Load();
            SyncNexusButton(Controls.Find("btnNexusKey", searchAllChildren: true).OfType<Button>().SingleOrDefault());
            Log(_config.NexusApiKey is null
                ? "已清除 Nexus 连接。"
                : $"Nexus 已连接为「{_config.NexusUserName ?? "（未验证用户）"}」。");
        }
    }

    private void SyncNexusButton(Button? button)
    {
        if (button is null)
            return;
        button.Text = _config.NexusApiKey is null ? "Nexus 密钥…" : $"Nexus: 已连接{_config.NexusUserName}";
    }

    /// <summary>
    /// 「自动更新」三分支：GitHub 更新键免 key 直连 → Nexus 更新键走 API key → 都没有给手动流程提示。
    /// </summary>
    private async Task AutoUpdateSelectedAsync()

    {
        if (_busy)
            return;

        var entry = SelectedEntry;
        if (entry is null)
        {
            Msg.Show(this, "先在列表里选一个 mod。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!entry.HasUpdate)
        {
            Msg.Show(this,
                "这个 mod 没检测到新版本。\n可以先点「检查更新」刷新一遍再试。",
                "没有可用更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var repo = GitHubModUpdateService.FindGitHubRepo(entry.Manifest?.UpdateKeys);
        var nexusId = repo is null ? NexusClient.FindNexusModId(entry.Manifest?.UpdateKeys) : null;
        if (repo is null && nexusId is null)
        {
            Msg.Show(this,
                "这个 mod 的更新键不支持自动下载（既不是 GitHub:作者/仓库，也不是有效的 Nexus:编号）。\n\n" +
                "点「更新页面」去官方下载页，浏览器下载 zip 后用「安装 zip…」导入即可。",
                "不支持自动更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，请先完全退出再更新。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _busy = true;
        SetBusy(true);
        try
        {
            if (repo is not null)
                await RunGitHubUpdateAsync(entry, repo);
            else
                await RunNexusUpdateAsync(entry, nexusId!.Value);
        }
        catch (Exception ex)
        {
            Log($"❌ 自动更新失败：{ex.Message}");
            Msg.Show(this, $"自动更新失败：{ex.Message}\n\n备选方案：点「更新页面」手动下载 zip，然后「安装 zip…」导入。",
                "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    /// <summary>GitHub 更新键分支：拉 release → 版本确认 → 下载 zip → 安装（免 API key）。</summary>
    private async Task RunGitHubUpdateAsync(ModEntry entry, string repo)
    {
        Log($"—— 自动更新 {entry.Name}（GitHub {repo}）——");

        var release = await _gitHub.GetLatestZipAsync(repo, entry.Name, Log);
        var releaseVersion = release.TagName.TrimStart('v', 'V');
        if (!ModVersionUtil.IsNewer(entry.VersionText, releaseVersion))
        {
            Log($"⚠ GitHub 最新 release 是 {releaseVersion}，不比你装的 {entry.VersionText} 新（可能 smapi.io 的更新检查另有含义）。未做任何改动。");
            return;
        }

        var confirm = Msg.Show(this,
            $"把「{entry.Name}」从 {entry.VersionText} 更新到 {releaseVersion} 吗？\n\n" +
            $"来源：GitHub {repo}（官方仓库，未经本工具修改）\n" +
            $"安装包：{release.AssetName}\n\n旧版本会自动备份到 Mods/.backup/ 下。",
            "确认更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (confirm != DialogResult.Yes)
        {
            Log("已取消。");
            return;
        }

        var workspace = Path.Combine(SmapiInstallerRunner.WorkspaceDirPath, "mod-updates");
        var zipPath = await _gitHub.DownloadAsync(release, workspace, Log);
        var report = await ModPackageInstaller.InstallFromZipAsync(zipPath, ModsDir, Log);
        if (report.Success)
            Log($"✅ {entry.Name}: {report.Primary.OldVersion ?? "首次安装"} → {report.Primary.NewVersion}");
        else
            Msg.Show(this, report.Results.Count > 0 ? report.Results[0].Message : report.Summary,
                "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);

        await RescanSilentAsync();
    }

    /// <summary>Nexus 更新键分支：查最新主文件 → 版本确认 → 下载安装（Premium）；403 时转浏览器手动兜底。</summary>
    private async Task RunNexusUpdateAsync(ModEntry entry, long modId)
    {
        var apiKey = _config.NexusApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Log("该 mod 从 Nexus 检查更新，但还没配置 API key。");
            using (var dialog = new NexusKeyDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    Log("已取消（没有保存 Nexus key）。");
                    return;
                }
            }
            _config = AppConfig.Load();
            SyncNexusButton(Controls.Find("btnNexusKey", searchAllChildren: true).OfType<Button>().SingleOrDefault());
            apiKey = _config.NexusApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log("Nexus key 没存上，转中止。");
                return;
            }
        }

        Log($"—— 自动更新 {entry.Name}（Nexus mod {modId}）——");
        var client = new NexusClient(apiKey);

        var file = await client.GetLatestMainFileAsync(modId);
        var nexusVersion = file.ModVersion ?? file.Version ?? "";
        if (string.IsNullOrWhiteSpace(nexusVersion) || !ModVersionUtil.IsNewer(entry.VersionText, nexusVersion))
        {
            Log($"⚠ Nexus 主文件版本是 {nexusVersion}，不比你装的 {entry.VersionText} 新。未做任何改动。");
            return;
        }

        var confirm = Msg.Show(this,
            $"把「{entry.Name}」从 {entry.VersionText} 更新到 {nexusVersion} 吗？\n\n" +
            $"来源：Nexus mod {modId}（作者官方文件，未经本工具修改）\n" +
            $"安装包：{file.FileName ?? file.Name}\n\n旧版本会自动备份到 Mods/.backup/ 下。",
            "确认更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (confirm != DialogResult.Yes)
        {
            Log("已取消。");
            return;
        }

        string zipPath;
        try
        {
            var workspace = Path.Combine(SmapiInstallerRunner.WorkspaceDirPath, "mod-updates");
            zipPath = await client.DownloadAsync(modId, workspace, Log, file: file);
        }
        catch (NexusApiException ex) when (ex.StatusCode == 403)
        {
            // 非 Premium：API 直下被 Nexus 拒绝 → 打开官方文件页让用户浏览器下载，回来「安装 zip…」两步兜底
            var manual = Msg.Show(this, ex.Message, "需要手动下载",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (manual == DialogResult.OK)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={file.FileId}",
                    UseShellExecute = true,
                });
                Log("官方文件页已打开。浏览器下载 zip 后，点「安装 zip…」选择它完成更新。");
            }
            else
            {
                Log("已取消（没有打开文件页）。");
            }
            return;
        }

        var report = await ModPackageInstaller.InstallFromZipAsync(zipPath, ModsDir, Log);
        if (report.Success)
            Log($"✅ {entry.Name}: {report.Primary.OldVersion ?? "首次安装"} → {report.Primary.NewVersion}");
        else
            Msg.Show(this, report.Results.Count > 0 ? report.Results[0].Message : report.Summary,
                "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);

        await RescanSilentAsync();
    }

    /*********
    ** 列表渲染
    *********/
    private static readonly Color ColorUpdate = Theme.Accent;
    private static readonly Color ColorProblem = Theme.Danger;
    private static readonly Color ColorWarning = Theme.Warn;
    private static readonly Color ColorDisabled = Theme.TextDim;
    private static readonly Color ColorNormal = Theme.Text;

    private void RefreshList()
    {
        _list.BeginUpdate();
        try
        {
            bool showDisabled = _showDisabled.Checked;
            var filter = _filterField.Box.Text.Trim();

            _list.Items.Clear();
            foreach (var mod in _mods
                         .OrderBy(m => PickTone(m).Rank)
                         .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!showDisabled && !mod.Enabled)
                    continue;
                if (filter.Length > 0 && !MatchesFilter(mod, filter))
                    continue;

                var tone = PickTone(mod).Tone;
                var item = new ListViewItem(mod.Name)
                {
                    Tag = mod,
                    ForeColor = tone,
                    ToolTipText = mod.Error,
                };
                item.SubItems.Add(mod.VersionText);
                item.SubItems.Add(mod.HasUpdate ? mod.UpdateVersion! : "");
                item.SubItems.Add(DescribeStatus(mod));
                item.SubItems.Add(mod.AuthorText);
                item.SubItems.Add(mod.UniqueId ?? "");
                item.SubItems.Add(mod.RelativeFolder);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        SyncActionButtons();
        RefreshSummary();
    }

    private static bool MatchesFilter(ModEntry mod, string filter) =>
        mod.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (mod.AuthorText?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (mod.UniqueId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || mod.FolderName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    /// <summary>配色 + 排序权重：可更新优先，其次异常，再正常，禁用最后。</summary>
    private static (Color Tone, int Rank) PickTone(ModEntry mod)
    {
        if (!mod.Enabled)
            return (ColorDisabled, 3);
        if (mod.Error is not null)
            return (ColorProblem, 1);
        if (mod.HasUpdate)
            return (ColorUpdate, 0);
        if (!string.IsNullOrEmpty(mod.MissingDependencies))
            return (ColorWarning, 1);
        if (mod.IsDuplicateId || mod.IsEntryDllMissing || IsBadCompatibility(mod))
            return (ColorProblem, 1);
        return (ColorNormal, 2);
    }

    /// <summary>smapi.io 兼容性状态里明确跟当前版本冲突的。</summary>
    private static bool IsBadCompatibility(ModEntry mod) =>
        mod.Update?.CompatibilityStatus is "Broken" or "Obsolete";

    /// <summary>状态列文本。</summary>
    private static string DescribeStatus(ModEntry mod)
    {
        var notes = new List<string>();

        if (mod.Error is not null)
            notes.Add(mod.Error);

        if (mod.HasUpdate)
            notes.Add($"有更新：{mod.UpdateVersion}（当前 {mod.VersionText}）");

        switch (mod.Update?.CompatibilityStatus)
        {
            case "Broken":
                notes.Add($"不兼容：( {FirstLine(mod.Update.CompatibilitySummary) ?? "官方确认当前游戏版本下有故障"} )");
                break;
            case "Obsolete":
                notes.Add("已废弃：" + (FirstLine(mod.Update.CompatibilitySummary) ?? "官方确认它已经没用了"));
                break;
            case "Abandoned":
                notes.Add("作者不再维护" + (mod.Update.LatestVersion is { } v ? $"（最新 {v}）" : ""));
                break;
            case "Unofficial":
                notes.Add("官方版已弃更，社区有非官方修复版（见更新页面）");
                break;
            case "Workaround":
                notes.Add(FirstLine(mod.Update.CompatibilitySummary) ?? "可以手动修复");
                break;
        }

        if (mod.Update?.Errors is { Length: > 0 } errors)
            notes.Add("更新查询失败：" + string.Join("；", errors.Take(2)));

        if (mod.IsDuplicateId)
            notes.Add("重复 MOD ID（另一个同名 mod 的副本也在，只会加载一个）");
        if (mod.MissingDependencies is { Length: > 0 } missing)
            notes.Add($"缺少依赖：{missing}");
        if (mod.IsEntryDllMissing)
            notes.Add("入口 DLL 文件不存在（装漏了或重名打包了）");

        // 正在载入的情况描述一下类型
        if (notes.Count == 0)
            notes.Add(mod.Type == ModEntryType.ContentPack ? "内容包" : "正常");

        return string.Join("；", notes);
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var first = text.Split('\n')[0].Trim();
        return first.Length > 120 ? first[..120] + "…" : first;
    }

    private void RefreshSummary()
    {
        int total = _mods.Count;
        int enabled = _mods.Count(m => m.Enabled && !m.IsBroken);
        int updates = _mods.Count(m => m.HasUpdate);
        int problems = _mods.Count(m => m.Error is not null || IsBadCompatibility(m));
        int disabled = _mods.Count(m => !m.Enabled && !m.IsBroken);
        _summary.Text = $"共 {total} 项";
        _pillOn.Text = $"在用 {enabled}";
        _pillUpdates.Text = $"可更新 {updates}";
        _pillBroken.Text = $"异常 {problems}";
        _pillDisabled.Text = $"已禁用 {disabled}";
        _pillUpdates.PillColor = updates > 0 ? Theme.Accent : Theme.TextMuted;
        _pillBroken.PillColor = problems > 0 ? Theme.Danger : Theme.TextMuted;
    }

    /*********
    ** 杂项操作
    *********/
    private void OpenSelectedFolder()
    {
        if (SelectedEntry is not { } entry)
            return;
        try
        {
            entry.OpenInExplorer();
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"打开文件夹失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TryOpenModsDir()
    {
        try
        {
            if (Directory.Exists(ModsDir))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{ModsDir}\"");
            else
                Msg.Show(this, $"目录不存在：{ModsDir}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"打开目录失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedPage()
    {
        if (SelectedEntry is not { } entry)
            return;

        var url = FindModPage(entry);
        if (url is null)
        {
            Msg.Show(this,
                "这个 mod 没有可识别的更新来源。\n\nmanifest.json 的 UpdateKeys 字段里也没有 NX/GitHub 等键，" +
                "可以打开它的文件夹找找 README。",
                "没有更新页面", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"打开浏览器失败：{ex.Message}\n\n{url}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>优先用接口给的地址，否则从 update key 猜（Nexus:123 → nexusmods 的 mod 页）。</summary>
    private static string? FindModPage(ModEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Update?.SuggestedUrl))
            return entry.Update!.SuggestedUrl;

        foreach (var key in entry.Manifest?.UpdateKeys ?? [])
        {
            var i = key.IndexOf(':');
            if (i <= 0)
                continue;
            var ns = key[..i].Trim();
            var id = key[(i + 1)..].Trim();

            switch (ns.ToLowerInvariant())
            {
                case "nexus":
                    if (uint.TryParse(id, out _))
                        return $"https://www.nexusmods.com/stardewvalley/mods/{id}";
                    break;
                case "github":
                    if (id.Contains('/'))
                        return $"https://github.com/{id}";
                    break;
                case "moddrop":
                    if (uint.TryParse(id, out _))
                        return $"https://www.moddrop.com/mods/{id}";
                    break;
                case "curseforge":
                    return $"https://www.curseforge.com/stardewvalley/mods/{id}";
                case "moddb":
                    return $"https://www.moddb.com/mods/{id}";
            }
        }
        return null;
    }

    private void CopySelectedId()
    {
        if (SelectedEntry is not { } entry)
            return;
        if (entry.UniqueId is not { } id)
        {
            Msg.Show(this, "这个条目没有 mod ID。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Clipboard.SetText(id);
            Log($"已复制 MOD ID：{id}");
        }
        catch
        {
            // 剪贴板偶尔被占用
        }
    }

    private void SyncActionButtons()
    {
        var entry = SelectedEntry;
        bool has = entry is not null && !_busy;
        _btnToggle.Enabled = has && entry!.Manifest is { };
        _btnOpenFolder.Enabled = has;
        _btnOpenPage.Enabled = has;
        _btnDelete.Enabled = has;
        _btnInstallZip.Enabled = !_busy;
        _btnAutoUpdate.Enabled = has && entry!.HasUpdate;
        _btnBackups.Enabled = !_busy;
        _btnConflicts.Enabled = !_busy;
        _btnToggle.Text = entry?.Enabled == true ? "禁用所选" : "启用所选";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnCheckUpdates.Enabled = !busy;
        _btnRescan.Enabled = !busy;
        _btnOpenModsDir.Enabled = !busy;
        _list.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        SyncActionButtons();
    }

    /*********
    ** 日志（和主窗口同款配色）
    *********/
    private void Log(string message)
    {
        _logPanel.Write(message);
    }
}
