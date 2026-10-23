using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// Mod 管理器窗口：
/// 扫描 Mods 目录 → 解析 manifest → 展示启停/更新状态/兼容性 → 启停（移动文件夹）。
///
/// 更新检查走 smapi.io 的官方 web API（免 apiKey、批量查全部 update key 来源），
/// 启停用 SMAPI 官方约定（点开头的文件夹被忽略），语义与 Stardrop / SMAPI 保持一致。
/// </summary>
internal sealed class ModManagerView : UserControl
{
    /*********
    ** 状态
    *********/
    private readonly string _gamePath;
    private string ModsDir => Path.Combine(_gamePath, "Mods");

    /// <summary>打开本窗口前想聚焦的 mod 名（日志查看器联动跳转用）；扫描完成后顶到筛选框里。</summary>
    private string? _focusMod;

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
    private ModernButton _btnDownloads = null!;

    /// <summary>鼠标按下时落在某个勾选框上，抬起时同一格才认为是「点勾选框」。</summary>
    private int _pendingCheckIndex = -1;

    /// <summary>列表为空时显示的提示。</summary>
    private EmptyHint _emptyHint = null!;

    /// <summary>宿主用：当前管理哪个游戏目录。</summary>
    public string GamePath => _gamePath;

    /// <summary>最近一次扫描的统计（总数 / 已启用 / 可更新），供宿主初始化状态栏。</summary>
    public (int Total, int Enabled, int Updates) Stats { get; private set; }

    /// <summary>扫描/统计变化时通知宿主刷新状态栏（总数、已启用、可更新）。</summary>
    public event Action<int, int, int>? StatsChanged;

    public ModManagerView(string gamePath, string? focusMod = null)
    {
        _gamePath = gamePath;
        _focusMod = string.IsNullOrWhiteSpace(focusMod) ? null : focusMod.Trim();

        // UserControl：主题色手工设，窗口级外观交给宿主 Form
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        Dock = DockStyle.Fill;

        BuildUi();

        // UserControl 没有 Shown 事件，用宿主加载完成触发首次扫描
        Load += async (_, _) => await RescanAsync();
    }

    /*********
    ** 布局
    *********/
    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(16, 8, 16, 10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 0: 搜索 / 刷新 / 显示已禁用
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 1: 操作按钮
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 2: 列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // 3: 统计

        // ---- 0: 搜索框（带放大镜）+ 刷新 + 显示已禁用 ----
        var filterRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 5, 0, 5),
            Margin = new Padding(0),
        };

        _filterField = new TextField("搜索 mod 名称 / 作者 / MOD ID")
        {
            Width = 340,
            Height = 36,
            Margin = new Padding(0, 0, 14, 0),
            LeadingGlyph = "\uE721",
            LeadingGlyphSize = 10,
        };
        _filterField.Box.TextChanged += (_, _) => RefreshList();
        filterRow.Controls.Add(_filterField);

        // 刷新按钮移到「Mod 列表」标题栏最右侧（见下方 listCard 的 header）
        _showDisabled = new ModernCheckBox
        {
            Text = "显示已禁用",
            Checked = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        _showDisabled.CheckedChanged += (_, _) => RefreshList();
        filterRow.Controls.Add(_showDisabled);
        root.Controls.Add(filterRow, 0, 0);

        // ---- 1: 操作按钮 ----
        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 3, 0, 5),
            Margin = new Padding(0),
        };

        _btnCheckUpdates = MakeButton("检查更新", ButtonVariant.Primary, 118, 0);
        _btnCheckUpdates.Font = Theme.Ui(9.5f, FontStyle.Bold);
        _btnCheckUpdates.Click += async (_, _) => await CheckUpdatesAsync();

        _btnAutoUpdate = MakeButton("自动更新", ButtonVariant.Secondary, 110);
        _btnAutoUpdate.Click += async (_, _) => await AutoUpdateSelectedAsync();

        _btnInstallZip = MakeButton("安装 zip…", ButtonVariant.Secondary, 112);
        _btnInstallZip.Click += async (_, _) => await InstallZipAsync();

        _btnConflicts = MakeButton("冲突检测", ButtonVariant.Secondary, 106);
        _btnConflicts.Click += async (_, _) => await RunConflictCheckAsync();

        _btnToggle = MakeButton("禁用所选", ButtonVariant.Secondary, 106);
        _btnToggle.Click += async (_, _) => await ToggleSelectedAsync();

        _btnBackups = MakeButton("备份管理…", ButtonVariant.Secondary, 112);
        _btnBackups.Click += async (_, _) => await ShowBackupManagerAsync();

        _btnDelete = MakeButton("删除…", ButtonVariant.Danger, 84);
        _btnDelete.Click += async (_, _) => await DeleteSelectedAsync();

        // 打开文件夹 / 更新页面移到右键菜单，但控件仍要存在（SyncActionButtons 会用到）
        _btnOpenFolder = MakeButton("打开文件夹", ButtonVariant.Ghost, 110);
        _btnOpenFolder.Click += (_, _) => OpenSelectedFolder();
        _btnOpenPage = MakeButton("更新页面", ButtonVariant.Ghost, 100);
        _btnOpenPage.Click += (_, _) => OpenSelectedPage();
        _btnOpenModsDir = MakeButton("打开 Mods 目录", ButtonVariant.Ghost, 130);
        _btnOpenModsDir.Click += (_, _) => TryOpenModsDir();

        actionRow.Controls.AddRange([
            _btnCheckUpdates, _btnAutoUpdate, _btnInstallZip,
            _btnConflicts, _btnToggle, _btnBackups, _btnDelete,
        ]);
        root.Controls.Add(actionRow, 0, 1);

        // ---- 2: 列表（勾选框 + 备注列） ----
        var listCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            Padding = new Padding(1),
            Margin = new Padding(0, 2, 0, 4),
        };
        _list = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = true,
            ShowStatusDot = false,
            DrawCheckBoxes = true,
            RowHeight = 34,
            HeaderHeight = 34,
        };
        _list.Columns.AddRange(
        [
            new ColumnHeader { Text = "  Mod 名称", Width = 210 },
            new ColumnHeader { Text = "已装版本", Width = 100 },
            new ColumnHeader { Text = "最新版本", Width = 100 },
            new ColumnHeader { Text = "状态", Width = 190 },
            new ColumnHeader { Text = "作者", Width = 108 },
            new ColumnHeader { Text = "MOD ID", Width = 160 },
            new ColumnHeader { Text = "位置", Width = 120 },
            new ColumnHeader { Text = "备注", Width = 120 },
        ]);
        _list.DoubleClick += (_, _) => OpenSelectedFolder();
        _list.SelectedIndexChanged += (_, _) => SyncActionButtons();
        _list.MouseDown += OnListMouseDown;
        _list.MouseUp += OnListMouseUp;
        _list.Resize += (_, _) => StretchLastColumn();
        _list.HandleCreated += (_, _) => StretchLastColumn();
        BuildContextMenu();

        // ---- 列表标题栏：「Mod 列表」标题 + 最右侧的刷新按钮 ----
        var listHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Theme.Surface,
        };
        listHeader.Controls.Add(new Label
        {
            Text = "Mod 列表",
            AutoSize = true,
            Font = Theme.Ui(9f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Location = new Point(16, 11),
            BackColor = Color.Transparent,
        });
        _btnRescan = new ModernButton
        {
            Glyph = "\uE72C",          // 刷新
            GlyphSize = 11,
            Text = "",
            Variant = ButtonVariant.Ghost,
            AutoSize = false,
            Size = new Size(30, 26),
            Margin = new Padding(0),
        };
        _btnRescan.Click += async (_, _) => await RescanAsync();
        _btnRescan.Location = new Point(0, 7);   // 下面按宽度贴右
        listHeader.Controls.Add(_btnRescan);
        listHeader.Resize += (_, _) =>
            _btnRescan.Location = new Point(listHeader.Width - _btnRescan.Width - 12, 7);

        // 空状态提示叠在列表上面（列表没内容时它才显示）
        _emptyHint = new EmptyHint
        {
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = Theme.Surface,   // 显式底色，避免透明背景在 CardPanel 上画不出来
        };

        listCard.Controls.Add(_list);
        listCard.Controls.Add(_emptyHint);
        listCard.Controls.Add(listHeader);
        root.Controls.Add(listCard, 0, 2);

        // ---- 3: 统计行（左侧数量，右侧下载入口，同一行） ----
        // 结构：根 Panel 里 pills(Fill) + 按钮容器(Dock.Right)。
        // 不用 TableLayoutPanel 的按钮列——Ghost 按钮在 AutoSize 列里会被布局吞掉。
        var summaryRow = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(2, 0, 22, 0),
            Height = 32,
        };

        var btnHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 10, 0),   // Padding 才影响 Dock 布局；Margin 不影响
        };
        _btnDownloads = new ModernButton
        {
            Text = "下载",
            Glyph = "\uE896",
            GlyphSize = 10,
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 26,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(0),
        };
        _btnDownloads.Click += (_, _) => OpenDownloads?.Invoke();
        btnHost.Controls.Add(_btnDownloads);
        summaryRow.Controls.Add(btnHost);

        var pills = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Margin = new Padding(0),
        };
        _summary = new Label
        {
            Text = "扫描中…",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(0, 6, 14, 0),
        };
        _pillOn = new Pill("已启用 0", Theme.Accent) { Margin = new Padding(0, 2, 6, 0) };
        _pillUpdates = new Pill("可更新 0", Theme.Warn) { Margin = new Padding(0, 2, 6, 0) };
        _pillBroken = new Pill("异常 0", Theme.Danger) { Margin = new Padding(0, 2, 6, 0) };
        _pillDisabled = new Pill("已禁用 0", Theme.TextMuted) { Margin = new Padding(0, 2, 6, 0) };
        pills.Controls.AddRange([_summary, _pillOn, _pillUpdates, _pillBroken, _pillDisabled]);
        summaryRow.Controls.Add(pills);

        root.Controls.Add(summaryRow, 0, 3);

        Controls.Add(root);
    }

    /// <summary>日志查看器跳转：预填筛选框并选中对应 mod。</summary>
    public void ApplyFocusMod(string modName)
    {
        _focusMod = modName;
        _focusApplied = false;
        _filterField.Box.Text = modName;
        // RefreshList 由 TextChanged 触发；再补一次选中
        if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
            _list.Items[0].Focused = true;
        }
    }

    /// <summary>末列吃掉剩余宽度，避免列表右侧出现系统默认的白色空档。</summary>
    private void StretchLastColumn()
    {
        if (_list is null || _list.Columns.Count == 0 || !_list.IsHandleCreated)
            return;
        int others = 0;
        for (int i = 0; i < _list.Columns.Count - 1; i++)
            others += _list.Columns[i].Width;
        _list.Columns[^1].Width = Math.Max(140, _list.ClientSize.Width - others);
    }

    /// <summary>点第一列左侧的勾选框区域 = 切换该 mod 的启用状态。</summary>
    private void OnListMouseDown(object? sender, MouseEventArgs e)
        => _pendingCheckIndex = HitCheckBox(e.Location);

    private void OnListMouseUp(object? sender, MouseEventArgs e)
    {
        int index = HitCheckBox(e.Location);
        if (index >= 0 && index == _pendingCheckIndex)
            _ = ToggleCheckAtAsync(index);
        _pendingCheckIndex = -1;
    }

    private int HitCheckBox(Point p)
    {
        var hit = _list.GetItemAt(p.X, p.Y);
        if (hit is null)
            return -1;
        // 勾选框绘制在第 0 列左起 12..28
        int left = 1;
        return p.X >= left + 8 && p.X <= left + 34 ? hit.Index : -1;
    }

    private async Task ToggleCheckAtAsync(int index)
    {
        if (index < 0 || index >= _list.Items.Count)
            return;
        _list.Items[index].Selected = true;
        await ToggleSelectedAsync();
    }

    /// <summary>统一规格的操作按钮：固定最小宽度，AutoSize 让文字不被裁切。</summary>
    private static ModernButton MakeButton(string text, ButtonVariant variant, int width, int leftGap = 8) => new()
    {
        Text = text,
        Variant = variant,
        AutoSize = true,
        MinimumSize = new Size(width, 36),
        Height = 36,
        Margin = new Padding(leftGap, 0, 8, 0),
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
        menu.Items.Add("编辑备注…", null, (_, _) => EditSelectedNote());
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
            _mods.Clear();
            RefreshList();      // 让空状态提示和统计归零
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
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
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
        dialog.ShowDialog(FindForm());
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
            dialog.ShowDialog(FindForm());
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
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
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
        string zipPath;
        try
        {
            zipPath = await _gitHub.DownloadAsync(release, workspace, Log);
        }
        catch (Exception ex)
        {
            // GitHub 直连下载失败 → 换镜像前缀重试（SMAPI 安装器同款兜底链），
            // 镜像也全挂才放弃，提示走「更新页面」手动下载。
            Log($"⚠ GitHub 直连下载失败：{ex.Message}，尝试镜像…");
            zipPath = await DownloadFromMirrorsAsync(release, workspace)
                      ?? throw new InvalidOperationException(
                          "GitHub 与全部镜像都下载失败，请点「更新页面」手动下载 zip，再「安装 zip…」导入。");
        }

        var report = await ModPackageInstaller.InstallFromZipAsync(zipPath, ModsDir, Log);
        if (report.Success)
            Log($"✅ {entry.Name}: {report.Primary.OldVersion ?? "首次安装"} → {report.Primary.NewVersion}");
        else
            Msg.Show(this, report.Results.Count > 0 ? report.Results[0].Message : report.Summary,
                "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);

        await RescanSilentAsync();
    }

    /// <summary>
    /// GitHub mod 包的镜像兜底下载：直连失败后按 gh-proxy / ghfast / ghproxy 镜像逐个重试。
    /// 全部失败返回 null（mod 包没有官方哈希可校验，只信任 GitHub 域名与这几家镜像）。
    /// </summary>
    private static readonly string[] GitHubMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.net/",
    ];

    private async Task<string?> DownloadFromMirrorsAsync(GitHubModRelease release, string destDir)
    {
        foreach (var prefix in GitHubMirrorPrefixes)
        {
            try
            {
                return await _gitHub.DownloadAsync(release, destDir, Log, ct: default, mirrorPrefix: prefix);
            }
            catch (Exception ex)
            {
                Log($"  ✗ 镜像 {new Uri(prefix).Host} 失败：{ex.Message}");
            }
        }
        return null;
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
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
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
                ShellHelper.OpenUrl($"https://www.nexusmods.com/stardewvalley/mods/{modId}?tab=files&file_id={file.FileId}");
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
                    Checked = mod.Enabled,      // 自绘勾选框读这个状态
                };
                item.SubItems.Add(mod.VersionText);                                   // 已装版本
                item.SubItems.Add(mod.HasUpdate ? mod.UpdateVersion! : "");           // 最新版本
                item.SubItems.Add(DescribeStatus(mod));                               // 状态
                item.SubItems.Add(mod.AuthorText);                                    // 作者
                item.SubItems.Add(mod.UniqueId ?? "");                                // MOD ID
                item.SubItems.Add(mod.RelativeFolder);                                // 位置
                item.SubItems.Add(_config.GetNote(mod.UniqueId) ?? "");               // 备注
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateEmptyHint();
        SyncActionButtons();
        RefreshSummary();
    }

    /// <summary>列表为空时在卡片中央给一句说明，而不是留一片黑。</summary>
    private void UpdateEmptyHint()
    {
        if (_emptyHint is null)
            return;

        bool empty = _list.Items.Count == 0;

        // 列表和提示框是重叠的：空状态时把列表让开，避免它盖住提示文字。
        _list.Visible = !empty;
        _emptyHint.Visible = empty;

        if (!empty)
            return;

        _emptyHint.Hint =
            !Directory.Exists(ModsDir)
                ? $"还没有 Mods 目录。\n\n先装好 SMAPI，再安装任意 Mod 后这里就会出现。\n\n{ModsDir}"
                : _mods.Count == 0
                    ? "这个 Mods 目录里没有找到任何 mod。\n\n用「安装 zip…」导入，或把 mod 文件夹放进 Mods 目录后点右上角刷新。"
                    : "没有符合当前筛选条件的 mod。\n\n试试清空搜索框，或勾上「显示已禁用」。";

        _emptyHint.Invalidate();
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

        // 通知宿主刷新底部状态栏
        Stats = (total, enabled, updates);
        StatsChanged?.Invoke(total, enabled, updates);
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

        if (!ShellHelper.OpenUrl(url))
            Msg.Show(this, $"打开浏览器失败。\n\n{url}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    /// <summary>编辑选中 mod 的备注。备注按 UniqueID 存进 AppConfig。</summary>
    private void EditSelectedNote()
    {
        if (SelectedEntry is not { } entry)
            return;
        if (entry.UniqueId is not { } id)
        {
            Msg.Show(this, "这个条目没有 mod ID，无法挂备注。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new NoteDialog(entry.Name, _config.GetNote(id));
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;

        _config.SetNote(id, dialog.NoteText);
        Log(string.IsNullOrWhiteSpace(dialog.NoteText)
            ? $"已清除备注：{entry.Name}"
            : $"已保存备注：{entry.Name} — {dialog.NoteText}");

        // 只刷这一行的备注列，不重扫
        RefreshList();
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
    ** 日志
    *********/
    /// <summary>操作日志事件：主窗口把它显示在折叠条上，界面里不再常驻日志面板。</summary>
    public event Action<string>? Logged;

    /// <summary>宿主提供的「打开下载队列」动作（下载按钮在统计行右侧）。</summary>
    public Action? OpenDownloads { get; set; }

    private void Log(string message) => Logged?.Invoke(message);
}
