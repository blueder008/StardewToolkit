using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// 星露谷 SMAPI 一键安装器主窗口：
/// Steam 探测 → GitHub 下载（SHA-256 校验）→ 官方安装器静默安装/更新/卸载。
/// </summary>
internal sealed class MainForm : Form
{
    /*********
    ** 状态
    *********/
    private readonly AppConfig _config = AppConfig.Load();
    private readonly GitHubReleaseService _service = new();

    private SmapiRelease? _latestRelease;
    private readonly List<SteamGame> _games = new();
    private bool _busy;

    /*********
    ** 控件
    *********/
    private ListBox _gameSelector = null!;
    private StatusList _statusList = null!;
    private Label _statusPath = null!;
    private CardPanel _statusCard = null!;
    private ModernButton _btnInstall = null!;
    private ModernButton _btnManual = null!;
    private ModernButton _btnUninstall = null!;
    private ModernButton _btnReload = null!;
    private ModernButton _btnMods = null!;
    private ModernButton _btnLogs = null!;
    private ModernButton _btnBackups = null!;
    private ModernButton _btnOpenGame = null!;
    private LogPanel _logPanel = null!;
    private Pill _netPill = null!;
    private readonly SemaphoreSlim _zipGate = new(1, 1); // 保证同一时间只有一个下载在写同一个文件

    private NxmDownloadForm? _nxmForm;

    /// <summary>启动时由浏览器拉起带进来的 nxm 链接（延迟到 Shown 后处理）。</summary>
    private readonly NxmLink? _pendingNxm;

    public MainForm(NxmLink? pendingNxm = null)
    {
        _pendingNxm = pendingNxm;
        Theme.StyleForm(this);
        Text = "星露谷工具箱 · SMAPI 与 Mod 管理";
        MinimumSize = new Size(940, 660);
        Size = new Size(1040, 780);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        Shown += async (_, _) =>
        {
            NxmPipe.StartListening(raw =>
            {
                if (NxmLink.Parse(raw) is { } parsed)
                    AcceptNxmLink(parsed);
            });
            if (_pendingNxm is { } link)
                AcceptNxmLink(link);
            await StartupAsync();
        };
    }

    /*********
    ** nxm 链接
    *********/
    /// <summary>管道线程回调：切回 UI 线程交给下载队列窗口。</summary>
    private void AcceptNxmLink(NxmLink link)
    {
        void Enqueue()
        {
            _nxmForm ??= new NxmDownloadForm { Owner = this };
            _nxmForm.Enqueue(link);
        }

        if (InvokeRequired)
            BeginInvoke(Enqueue);
        else
            Enqueue();
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
            Padding = new Padding(18, 16, 18, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));  // 0: 目标游戏
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));   // 1: 间距
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));  // 2: 状态 / 快捷入口
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));   // 3: 间距
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 4: 主操作
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));   // 5: 间距
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 6: 日志
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 7: 页脚

        root.Controls.Add(BuildTargetCard(), 0, 0);
        root.Controls.Add(BuildInfoRow(), 0, 2);
        root.Controls.Add(BuildActionRow(), 0, 4);

        _logPanel = new LogPanel("运行日志") { Dock = DockStyle.Fill };
        root.Controls.Add(_logPanel, 0, 6);

        root.Controls.Add(new Label
        {
            Text = "提示：Steam 玩家装完需要把启动项填进 Steam 属性，安装成功后会弹窗给你可直接复制的字符串。",
            AutoSize = true,
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(2, 8, 0, 0),
        }, 0, 7);

        Controls.Add(root);
        Controls.Add(hero);
    }

    private HeroPanel BuildHero()
    {
        var hero = new HeroPanel { Dock = DockStyle.Top, Height = 96 };

        hero.Controls.Add(new LogoBadge
        {
            Location = new Point(20, 21),
            Size = new Size(54, 54),
        });
        hero.Controls.Add(new Label
        {
            Text = "星露谷工具箱",
            Font = Theme.Ui(17f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(90, 17),
        });
        hero.Controls.Add(new Label
        {
            Text = "SMAPI 安装 · Mod 管理 · 冲突诊断 · 日志分析",
            Font = Theme.Ui(9f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(92, 50),
        });

        _netPill = new Pill("正在检查网络", Theme.TextMuted);
        var version = typeof(MainForm).Assembly.GetName().Version;
        var versionPill = new Pill($"v{version?.ToString(3) ?? "0.9.0"}", Theme.Purple);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 330,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 36, 20, 0),
        };
        right.Controls.Add(_netPill);
        right.Controls.Add(versionPill);
        hero.Controls.Add(right);

        return hero;
    }

    private Control BuildTargetCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 10, 16, 12),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
        };
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "目标游戏",
            Font = Theme.Ui(10.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 6),
        }, 0, 0);

        var headerButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        var btnManualPick = new ModernButton
        {
            Text = "手动选择目录…",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 26,
            Font = Theme.Ui(8.5f),
        };
        btnManualPick.Click += (_, _) => PickManually();
        var btnDetect = new ModernButton
        {
            Text = "重新检测",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 26,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(0, 0, 6, 0),
        };
        btnDetect.Click += (_, _) => DetectGames();
        headerButtons.Controls.Add(btnManualPick);
        headerButtons.Controls.Add(btnDetect);
        header.Controls.Add(headerButtons, 1, 0);
        inner.Controls.Add(header, 0, 0);

        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 2, 0, 0),
        };
        _gameSelector = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 46,
            Font = Theme.Ui(9f),
        };
        _gameSelector.DrawItem += DrawGameItem;
        _gameSelector.SelectedIndexChanged += (_, _) => RefreshUi();
        host.Controls.Add(_gameSelector);
        inner.Controls.Add(host, 0, 1);

        card.Controls.Add(inner);
        return card;
    }

    /// <summary>候选目录自绘：路径 + 来源库 + SMAPI 状态徽标。</summary>
    private void DrawGameItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _games.Count)
            return;

        var g = e.Graphics;
        var game = _games[e.Index];
        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var rect = e.Bounds;

        Color background = selected
            ? Theme.Blend(Theme.SurfaceRaised, Theme.Accent, 0.16)
            : e.Index % 2 == 1 ? Color.FromArgb(29, 38, 50) : Theme.SurfaceRaised;
        using (var brush = new SolidBrush(background))
            g.FillRectangle(brush, rect);

        if (selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, rect.Left, rect.Top + 7, 3, rect.Height - 14);
        }
        else
        {
            using var line = new Pen(Color.FromArgb(20, 255, 255, 255));
            g.DrawLine(line, rect.Left + 12, rect.Bottom - 1, rect.Right - 12, rect.Bottom - 1);
        }

        bool hasSmapi = GameInspector.HasSmapiFiles(game.GamePath);
        int left = rect.Left + 14;
        int badgeWidth = 76;

        var textRect = new Rectangle(left, rect.Top + 6, Math.Max(40, rect.Right - left - badgeWidth - 22), 20);
        TextRenderer.DrawText(g, game.GamePath, Theme.Ui(9.5f, FontStyle.Bold), textRect,
            Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        var subRect = new Rectangle(left, rect.Top + 25, Math.Max(40, rect.Right - left - 16), 18);
        TextRenderer.DrawText(g, $"Steam 库 · {game.LibraryPath}", Theme.Ui(8f), subRect,
            Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        var tone = hasSmapi ? Theme.Success : Theme.TextMuted;
        var badge = new Rectangle(rect.Right - badgeWidth - 14, rect.Top + (rect.Height - 22) / 2, badgeWidth, 22);
        Theme.FillRounded(g, badge, 11, Theme.Blend(Theme.SurfaceRaised, tone, 0.16));
        TextRenderer.DrawText(g, hasSmapi ? "SMAPI 已装" : "未装 SMAPI", Theme.Ui(7.5f), badge, tone,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private Control BuildInfoRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        row.Controls.Add(BuildStatusCard(), 0, 0);
        row.Controls.Add(BuildQuickCard(), 1, 0);
        return row;
    }

    private Control BuildStatusCard()
    {
        _statusCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(16, 12, 16, 10),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
        };
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        inner.Controls.Add(new Label
        {
            Text = "安装状态",
            Font = Theme.Ui(10.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        }, 0, 0);

        _statusPath = new Label
        {
            Text = "尚未选择游戏目录",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 20,
            Font = Theme.Mono(8f),
            ForeColor = Theme.TextDim,
            AutoEllipsis = true,
            Margin = new Padding(0, 0, 0, 4),
            BackColor = Color.Transparent,
        };
        inner.Controls.Add(_statusPath, 0, 1);

        _statusList = new StatusList
        {
            Dock = DockStyle.Fill,
            RowHeight = 27,
            LabelWidth = 78,
            Margin = new Padding(0),
        };
        inner.Controls.Add(_statusList, 0, 2);

        _statusCard.Controls.Add(inner);
        return _statusCard;
    }

    private Control BuildQuickCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 14),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
        };
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        inner.Controls.Add(new Label
        {
            Text = "快捷入口",
            Font = Theme.Ui(10.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        }, 0, 0);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _btnMods = MakeQuickButton("Mod 管理", ButtonVariant.Primary, new Padding(0, 0, 6, 6));
        _btnMods.Click += (_, _) => OpenModManager();
        _btnLogs = MakeQuickButton("日志查看", ButtonVariant.Secondary, new Padding(6, 0, 0, 6));
        _btnLogs.Click += (_, _) => new LogViewerForm().Show(this);
        _btnBackups = MakeQuickButton("备份管理", ButtonVariant.Secondary, new Padding(0, 6, 6, 0));
        _btnBackups.Click += (_, _) => OpenBackupManager();
        _btnOpenGame = MakeQuickButton("打开游戏目录", ButtonVariant.Secondary, new Padding(6, 6, 0, 0));
        _btnOpenGame.Click += (_, _) => OpenGameFolder();

        grid.Controls.Add(_btnMods, 0, 0);
        grid.Controls.Add(_btnLogs, 1, 0);
        grid.Controls.Add(_btnBackups, 0, 1);
        grid.Controls.Add(_btnOpenGame, 1, 1);
        inner.Controls.Add(grid, 0, 1);

        card.Controls.Add(inner);
        return card;
    }

    private static ModernButton MakeQuickButton(string text, ButtonVariant variant, Padding margin) => new()
    {
        Text = text,
        Variant = variant,
        Dock = DockStyle.Fill,
        Margin = margin,
        Font = Theme.Ui(9f),
    };

    private Control BuildActionRow()
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 0),
        };

        _btnInstall = new ModernButton
        {
            Text = "一键安装 SMAPI",
            Variant = ButtonVariant.Primary,
            AutoSize = true,
            MinimumSize = new Size(230, 44),
            CornerRadius = 10,
            Font = Theme.Ui(10.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 10, 0),
        };
        _btnInstall.Click += async (_, _) => await ActionAsync(uninstall: false);

        _btnUninstall = new ModernButton
        {
            Text = "卸载 SMAPI",
            Variant = ButtonVariant.Danger,
            AutoSize = true,
            MinimumSize = new Size(112, 44),
            Margin = new Padding(0, 0, 10, 0),
        };
        _btnUninstall.Click += async (_, _) => await ActionAsync(uninstall: true);

        _btnReload = new ModernButton
        {
            Text = "检查更新",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(108, 44),
            Margin = new Padding(0, 0, 10, 0),
        };
        _btnReload.Click += async (_, _) => await ReloadLatestAsync();

        _btnManual = new ModernButton
        {
            Text = "手动安装…",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            MinimumSize = new Size(104, 44),
        };
        _btnManual.Click += async (_, _) => await ManualInstallClicked();

        row.Controls.AddRange([_btnInstall, _btnUninstall, _btnReload, _btnManual]);
        return row;
    }

    /*********
    ** 启动
    *********/
    private async Task StartupAsync()
    {
        var steamPath = SteamLocator.GetSteamPath();
        if (steamPath is null)
            Log("⚠ 没有检测到 Steam（未安装或没登录过）。可以点「重新检测」试试，或用手动选择目录。");
        else
            Log($"Steam 路径：{steamPath}");

        DetectGames();
        RefreshUi();

        await ReloadLatestAsync();
        RefreshUi();
    }

    /*********
    ** 游戏探测 选择目录
    *********/
    private void DetectGames()
    {
        var previous = _config.LastGamePath;

        _games.Clear();
        foreach (var g in SteamLocator.FindInstalls())
            _games.Add(g);

        // 上次手动选过的目录也纳入候选（Steam 探测覆盖不到的情况），保留在列表末尾
        if (previous is not null && Directory.Exists(previous)
            && GameInspector.HasGameFiles(previous)
            && _games.All(g => !SamePath(g.GamePath, previous)))
        {
            _games.Add(new SteamGame(previous, "手动选择"));
        }

        FillGameSelector();
        RefreshUi();
    }

    private void FillGameSelector()
    {
        _gameSelector.BeginUpdate();
        try
        {
            _gameSelector.Items.Clear();
            foreach (var g in _games)
                _gameSelector.Items.Add($"{g.GamePath}   [{g.LibraryPath}]");

            if (_games.Count == 0)
            {
                _gameSelector.SelectedIndex = -1;
                return;
            }

            // 默认优先：带 SMAPI 的目录，其次上次的，最后第一个 Steam 检测结果
            int select = _games.FindIndex(g => GameInspector.HasSmapiFiles(g.GamePath));
            if (select < 0 && _config.LastGamePath is not null)
                select = _games.FindIndex(g => SamePath(g.GamePath, _config.LastGamePath));
            if (select < 0)
                select = 0;

            _gameSelector.SelectedIndex = select;
        }
        finally
        {
            _gameSelector.EndUpdate();
        }
    }

    private void PickManually()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Stardew Valley 游戏目录（里面要有 Stardew Valley.dll）",
            ShowNewFolderButton = false,
        };
        if (Directory.Exists(_config.LastGamePath))
            dialog.SelectedPath = _config.LastGamePath;

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        if (!GameInspector.HasGameFiles(dialog.SelectedPath))
        {
            Msg.Show(this, "这个目录里没找到 Stardew Valley.dll，请选择游戏本体目录。", "不是游戏目录",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.LastGamePath = dialog.SelectedPath;
        _config.Save();
        DetectGames();
    }

    /// <summary>刷新候选一览和状态栏。</summary>
    private string? SelectedTarget =>
        _gameSelector.SelectedIndex >= 0 && _gameSelector.SelectedIndex < _games.Count
            ? _games[_gameSelector.SelectedIndex].GamePath
            : null;

    /*********
    ** 界面刷新
    *********/
    private void RefreshUi()
    {
        RefreshStatus();
        var target = SelectedTarget;

        _btnInstall.Enabled = !_busy && target is not null && GameInspector.HasGameFiles(target);
        _btnManual.Enabled = !_busy && target is not null && GameInspector.HasGameFiles(target);
        _btnUninstall.Enabled = !_busy && target is not null && GameInspector.HasSmapiFiles(target);
        _btnReload.Enabled = !_busy;
        _btnMods.Enabled = !_busy && target is not null;
        _btnBackups.Enabled = !_busy && target is not null;
        _btnOpenGame.Enabled = !_busy && target is not null;
        _btnInstall.Text = MainButtonText();
    }

    private string MainButtonText()
    {
        var target = SelectedTarget;
        if (target is null)
            return "一键安装 SMAPI";

        if (GameInspector.IsSmapiBroken(target))
            return "修复 SMAPI（覆盖安装）";

        var installed = GameInspector.GetSmapiVersion(target);
        if (installed is null)
            return _latestRelease is null ? "一键安装 SMAPI" : $"安装 SMAPI {_latestRelease.TagName}";

        if (_latestRelease is not null
            && Version.TryParse(_latestRelease.TagName, out var latest)
            && latest > installed)
        {
            return $"更新 SMAPI 到 {_latestRelease.TagName}";
        }

        return "重装 SMAPI";
    }

    private void RefreshStatus()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            _statusPath.Text = "尚未选择游戏目录";
            _statusList.SetRows(
            [
                new StatusList.Row("状态", "等待选择游戏目录", Theme.Warn),
                new StatusList.Row("提示", "点「重新检测」或「手动选择目录…」", Theme.TextMuted),
                new StatusList.Row("兼容", "Steam / GOG / 绿色版都可手动指定", Theme.TextMuted),
            ]);
            return;
        }

        var source = _games.FirstOrDefault(g => SamePath(g.GamePath, target))?.LibraryPath;
        _statusPath.Text = source is null ? target : $"{target}   ·   {source}";
        var rows = new List<StatusList.Row>();

        var gameVer = GameInspector.GetGameVersion(target);
        var smapiInstalled = GameInspector.HasSmapiFiles(target);
        var smapiVer = GameInspector.GetSmapiVersion(target);

        rows.Add(new StatusList.Row("游戏版本", gameVer?.ToString() ?? "未知",
            gameVer is null ? Theme.TextMuted : Theme.Text));
        rows.Add(new StatusList.Row("SMAPI",
            smapiInstalled ? $"{smapiVer?.ToString() ?? "版本未知"} 已安装" : "未安装",
            smapiInstalled ? Theme.Success : Theme.Warn));

        if (_latestRelease is null)
        {
            rows.Add(new StatusList.Row("最新版本", "尚未获取（网络不佳可重试）", Theme.TextMuted));
        }
        else if (!smapiInstalled)
        {
            rows.Add(new StatusList.Row("最新版本", _latestRelease.TagName, Theme.Info));
        }
        else if (smapiVer is not null && Version.TryParse(_latestRelease.TagName, out var latest) && latest > smapiVer)
        {
            rows.Add(new StatusList.Row("最新版本", $"{_latestRelease.TagName}  可更新", Theme.Warn));
        }
        else
        {
            rows.Add(new StatusList.Row("最新版本", $"{_latestRelease.TagName}  已是最新", Theme.Success));
        }

        var warnings = new List<string>();
        if (GameInspector.IsSmapiBroken(target))
            warnings.Add("SMAPI 文件残缺，主按钮覆盖安装即可修复");
        if (GameInspector.IsGameRunning())
            warnings.Add("游戏正在运行，请先退出再安装/更新");
        if (warnings.Count > 0)
            rows.Add(new StatusList.Row("注意", string.Join("；", warnings), Theme.Danger));

        _statusList.SetRows(rows);
    }

    /*********
    ** Mod 管理
    *********/
    /// <summary>打开 Mod 管理器窗口（模态，关掉回来自动刷新状态）。</summary>
    private void OpenModManager()
    {
        if (_busy)
            return;

        var target = SelectedTarget;
        if (target is null)
        {
            Msg.Show(this, "请先在上方选择一个游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var manager = new ModManagerForm(target);
        manager.ShowDialog(this);
        RefreshUi();
    }

    /// <summary>从主窗口直达备份管理（不需要先开 Mod 管理器）。</summary>
    private void OpenBackupManager()
    {
        if (_busy)
            return;

        var target = SelectedTarget;
        if (target is null)
        {
            Msg.Show(this, "请先在上方选择一个游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var modsDir = Path.Combine(target, "Mods");
        if (!Directory.Exists(modsDir))
        {
            Msg.Show(this,
                $"还没有 Mods 目录：{modsDir}\n\n先把 SMAPI 装好，再安装任意 Mod 后就会自动产生备份。",
                "没有 Mods 目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var backups = new BackupManagerForm(modsDir);
        backups.ShowDialog(this);
        RefreshUi();
    }

    private void OpenGameFolder()
    {
        var target = SelectedTarget;
        if (target is null || !Directory.Exists(target))
            return;

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch (Exception ex)
        {
            Log($"❌ 打开游戏目录失败：{ex.Message}");
        }
    }

    /*********
    ** 检查更新
    *********/
    private async Task ReloadLatestAsync()
    {
        try
        {
            _latestRelease = await _service.GetLatestAsync();
            Log($"最新 SMAPI 版本：{_latestRelease.TagName}（GitHub Releases）");
            _netPill.Text = "已连接 GitHub";
            _netPill.PillColor = Theme.Success;
        }
        catch (Exception ex)
        {
            _latestRelease = null;
            Log($"⚠ 检查 SMAPI 更新失败：{ex.Message}");
            _netPill.Text = "网络不可用";
            _netPill.PillColor = Theme.Warn;
        }
        RefreshUi();
    }

    /*********
    ** 下载 + 安装 + 卸载
    *********/
    private async Task ActionAsync(bool uninstall, string? manualZipPath = null)
    {
        if (_busy)
            return; // 防止双击/并发安装两路写同一个 zip 文件

        var target = SelectedTarget;
        if (target is null)
        {
            Msg.Show(this, "请先在上方选择一个游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，文件被占用时无法继续。\n请先完全退出游戏再试。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (uninstall)
        {
            var confirm = Msg.Show(this,
                $"要从这个目录卸载 SMAPI 吗？\n{target}\n\n卸载只移除 SMAPI 自己的文件，不影响你自己的 Mods 和存档。",
                "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
                return;
        }

        _busy = true;
        RefreshUi();
        try
        {
            Log($"—— 开始{(uninstall ? "卸载" : "安装")}：{target} ——");

            string zipPath;
            SmapiRelease? latest;
            if (manualZipPath is not null)
            {
                zipPath = manualZipPath;
                latest = _latestRelease;
                Log($"使用手动导入的安装包：{Path.GetFileName(manualZipPath)}");
            }
            else
            {
                latest = _latestRelease ?? await _service.GetLatestAsync();
                Log($"目标版本：SMAPI {latest.TagName}");
                zipPath = await GetInstallerZipAsync(latest);
            }

            var runner = new SmapiInstallerRunner(zipPath);
            var result = await runner.RunAsync(uninstall, target, Log);

            if (result.Success)
            {
                if (uninstall)
                {
                    Log("✅ SMAPI 已卸载。Steam 启动项里如果还填着 SMAPI 的路径，记得清空。");
                }
                else
                {
                    Log($"✅ SMAPI {(latest?.TagName ?? "（手动导入，版本未知）")} 安装成功。");
                    _config.LastGamePath = target;
                    _config.Save();
                    ShowSteamLaunchOptions(target, latest?.TagName ?? "未知版本");
                }
            }
            else
            {
                Log("❌ 官方安装器报告：结果校验失败。常见原因：杀毒软件拦截、路径无权限、文件被锁定。");
                ShowFailDigest(result.Output);
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
        }
        catch (Exception ex)
        {
            Log($"❌ {(uninstall ? "卸载" : "安装")}失败：{ex.Message}");
            ShowFailDigest(ex.ToString());
        }
        finally
        {
            _busy = false;
            RefreshUi();
        }
    }

    /// <summary>
    /// 手动安装：用户自己下载好官方安装包 zip，选进来，程序校验官方
    /// SHA-256 后直接安装。适合自动下载通道完全不通的环境。
    /// </summary>
    private async Task ManualInstallClicked()
    {
        if (_busy)
            return;

        var target = SelectedTarget;
        if (target is null)
        {
            Msg.Show(this, "请先在上方选择一个游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，请先完全退出游戏。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 拿当前最新 release 信息（用于哈希校验和版本号显示；拿不到也允许继续）
        SmapiRelease? release = _latestRelease;
        if (release is null)
        {
            try { release = await _service.GetLatestAsync(); }
            catch { release = null; }
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择 SMAPI 官方安装包 zip（从 smapi.io 或 GitHub Releases 下载）",
            Filter = "SMAPI 安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string manualZip = dialog.FileName;
        Log($"手动导入安装包：{manualZip}");

        if (release is not null && !string.IsNullOrWhiteSpace(release.Sha256))
        {
            Log("校验手动导入的安装包（对照官方 SHA-256）…");
            var hash = await GitHubReleaseService.ComputeSha256Async(manualZip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Msg.Show(this,
                    "校验失败：这个文件与官方 SMAPI " + release.TagName + " 不一致。\n\n" +
                    "文件哈希: " + hash + "\n官方哈希: " + release.Sha256 + "\n\n" +
                    "可能下载错了版本、文件损坏或被改过，请从官方渠道重新下载。",
                    "校验失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log("❌ 手动安装包校验失败，已拒绝安装。");
                return;
            }
            Log("✅ 手动导入的安装包校验通过。");
        }
        else
        {
            var force = Msg.Show(this,
                "暂时连不上 GitHub，无法校验官方哈希。\n" +
                "请确认这个 zip 是从官方渠道下载的（smapi.io / GitHub Releases）。\n" +
                "SMAPI 4.5+ 的安装器自带恶意文件检测，但仍建议联网后重新校验。\n\n继续安装？",
                "无法校验", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (force != DialogResult.Yes)
                return;
        }

        await ActionAsync(uninstall: false, manualZipPath: manualZip);
    }

    private async Task<string> GetInstallerZipAsync(SmapiRelease release)
    {
        await _zipGate.WaitAsync();
        try
        {
            var workspace = SmapiInstallerRunner.WorkspaceDirPath;
            var cached = Path.Combine(workspace, $"SMAPI-{release.TagName}-installer.zip");
            if (File.Exists(cached))
            {
                Log($"使用已下载的安装包：{Path.GetFileName(cached)}");
                return cached;
            }

            try
            {
                var zip = await _service.DownloadWithFallbackAsync(release, workspace, CreateDownloadProgress(), Log);
                Log("✅ 下载完成且校验通过。");
                return zip;
            }
            catch (Exception ex)
            {
                Log($"❌ 自动下载失败：{ex.Message}");
                Log($"手动下载地址：{release.DownloadUrl}");
                return await PickInstallerManuallyAsync(release);
            }
        }
        finally
        {
            _zipGate.Release();
        }
    }

    private IProgress<(long Done, long Total)> CreateDownloadProgress()
    {
        var lastPercent = -1;
        return new Progress<(long Done, long Total)>(p =>
        {
            if (p.Total <= 0)
                return;
            var pct = (int)(p.Done * 100 / p.Total);
            if (pct != lastPercent && pct % 10 == 0)
            {
                lastPercent = pct;
                Log($"下载中… {pct}%  ({p.Done / 1024.0 / 1024.0:F1} MB)");
            }
        });
    }

    /// <summary>
    /// 自动下载全部失败时的兜底：让用户在浏览器（可挂代理）里手动下载安装包，
    /// 之后程序做官方 SHA-256 校验再使用，校验不过直接拒绝。
    /// </summary>
    private async Task<string> PickInstallerManuallyAsync(SmapiRelease release)
    {
        var choice = Msg.Show(this,
            "自动下载失败（网络连不上 GitHub 的下载域名）。\n\n" +
            "可以：\n" +
            "  1) 用浏览器下载官方安装包，手动导入给我校验后安装\n" +
            "     下载地址（已写进日志，可直接复制）：\n" +
            $"     {release.DownloadUrl}\n" +
            "  2) 稍后重试自动下载\n\n" +
            "要现在手动选择安装包文件吗？",
            "下载失败", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (choice != DialogResult.Yes)
            throw new InvalidOperationException("自动下载失败，用户选择稍后再试。");

        using var dialog = new OpenFileDialog
        {
            Title = $"选择 SMAPI {release.TagName} 官方安装包",
            Filter = $"SMAPI 安装包|SMAPI-{release.TagName}-installer.zip|安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            throw new InvalidOperationException("未选择安装包文件，安装已取消。");

        Log("校验手动导入的安装包（对照官方 SHA-256）…");
        var hash = await GitHubReleaseService.ComputeSha256Async(dialog.FileName);
        if (!string.IsNullOrWhiteSpace(release.Sha256)
            && !string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "校验失败：选择的文件与官方 SMAPI " + release.TagName + " 不一致。\n" +
                "文件哈希: " + hash + "\n" +
                "官方哈希: " + release.Sha256 + "\n\n" +
                "可能下载错版本、文件损坏或被替换，请从下载地址重新下载后重试。");
        }
        Log("✅ 手动导入的安装包校验通过。");
        return dialog.FileName;
    }

    /*********
    ** Steam 启动项
    *********/
    private void ShowSteamLaunchOptions(string gamePath, string versionText)
    {
        var options = $"\"{Path.Combine(gamePath, "StardewModdingAPI.exe")}\" %command%";

        using var dialog = new Form
        {
            Text = "SMAPI 安装完成",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(700, 330),
            Padding = new Padding(24, 20, 24, 18),
        };
        Theme.StyleForm(dialog);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Theme.Bg };
        titleRow.Controls.Add(new Label
        {
            Text = $"SMAPI {versionText} 安装成功",
            Font = Theme.Ui(14f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 0),
        });
        titleRow.Controls.Add(new Pill("已完成", Theme.Success) { Margin = new Padding(0, 6, 0, 0) });
        panel.Controls.Add(titleRow, 0, 0);

        panel.Controls.Add(new Label
        {
            Text = "如果你用 Steam 玩，把下面这行贴到：游戏右键 → 属性 → 启动选项。",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Margin = new Padding(0, 8, 0, 12),
        }, 0, 1);

        var field = new TextField { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12) };
        field.Box.Text = options;
        field.Box.ReadOnly = true;
        field.Box.Font = Theme.Mono(9.5f);
        panel.Controls.Add(field, 0, 2);

        panel.Controls.Add(new Label
        {
            Text = "启动项生效后，从 Steam 启动也会通过 SMAPI 加载 Mod（成就、时长统计不受影响）。\n" +
                   "如果你直接双击 StardewModdingAPI.exe 玩，也可以不设置。",
            AutoSize = true,
            ForeColor = Theme.TextDim,
            Margin = new Padding(0, 0, 0, 10),
        }, 0, 3);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        var close = new ModernButton
        {
            Text = "关闭",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(96, 38),
            Margin = new Padding(10, 0, 0, 0),
        };
        close.Click += (_, _) => dialog.Close();
        var copy = new ModernButton
        {
            Text = "复制启动项",
            Variant = ButtonVariant.Primary,
            AutoSize = true,
            MinimumSize = new Size(128, 38),
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(options);
                Log("✅ 启动项已复制到剪贴板。");
            }
            catch { /* 剪贴板偶尔被占用 */ }
        };
        buttonRow.Controls.Add(close);
        buttonRow.Controls.Add(copy);
        panel.Controls.Add(buttonRow, 0, 5);

        dialog.Controls.Add(panel);
        dialog.ShowDialog(this);
    }

    /*********
    ** 日志
    *********/
    private void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }
        if (IsDisposed)
            return;
        _logPanel.Write(message);
    }

    /*********
    ** 其他
    *********/
    private void ShowFailDigest(string raw)
    {
        var lines = raw.Split('\n').TakeLast(20);
        var text = "可能是游戏文件被占用、路径无权限、下载校验失败或杀毒软件拦截。\n\n最近的输出：\n"
            + string.Join(Environment.NewLine, lines);
        if (text.Length > 6000)
            text = text[..6000] + "\n…";
        Msg.Show(this, text, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
