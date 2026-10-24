using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// 星露谷 SMAPI 一键安装器主窗口：
/// Steam 探测 → GitHub 下载（SHA-256 校验）→ 官方安装器静默安装/更新/卸载。
/// </summary>
internal sealed class MainForm : Form
{
    /*********
    ** Nexus 备选下载渠道
    *********/
    /// <summary>Nexus 上的 SMAPI 官方页面（mod 2400，Pathoschild 本人维护的上传）：
    /// GitHub 直连和镜像全都下载失败时的备选渠道。</summary>
    private const string SmapiNexusPageUrl = "https://www.nexusmods.com/stardewvalley/mods/2400";
    private const long SmapiNexusModId = 2400;

    /*********
    ** 状态
    *********/
    private readonly AppConfig _config = AppConfig.Load();
    private readonly GitHubReleaseService _service = new();

    private SmapiRelease? _latestRelease;
    private readonly List<SteamGame> _games = new();
    private string? _currentTarget;
    private bool _busy;

    /*********
    ** 控件
    *********/
    private TableLayoutPanel _rootLayout = null!;
    private Label _smapiSummary = null!;
    private CardPanel _smapiBody = null!;
    private ModernButton _btnExpandSmapi = null!;
    private Panel _modHost = null!;
    private ModManagerView? _modView;

    private StatusList _statusList = null!;
    private ModernButton _btnInstall = null!;
    private ModernButton _btnManual = null!;
    private ModernButton _btnUninstall = null!;
    private ModernButton _btnLogs = null!;
    private Pill _netPill = null!;
    private readonly SemaphoreSlim _zipGate = new(1, 1); // 保证同一时间只有一个下载在写同一个文件

    private NxmDownloadForm? _nxmForm;

    /// <summary>启动时由浏览器拉起带进来的 nxm 链接（延迟到 Shown 后处理）。</summary>
    private readonly NxmLink? _pendingNxm;

    public MainForm(NxmLink? pendingNxm = null)
    {
        _pendingNxm = pendingNxm;
        Theme.StyleForm(this);
        Text = "星露谷工具箱 · SMAPI 与 Mod 管理（构建 " +
               System.IO.File.GetLastWriteTime(
                   System.AppContext.BaseDirectory + "StardewToolkit.dll").ToString("HH:mm") + "）";
        // 与预览稿一致的窗口尺寸：客户区 1262×753，够放 8 列表格
        MinimumSize = new Size(1040, 680);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        Shown += async (_, _) =>
        {
            NxmPipe.StartListening(
                raw =>
                {
                    if (NxmLink.Parse(raw) is { } parsed)
                        AcceptNxmLink(parsed);
                },
                onActivate: () =>
                {
                    // 第二个实例被双击时请求亮出主窗口
                    void BringUp()
                    {
                        if (WindowState == FormWindowState.Minimized)
                            WindowState = FormWindowState.Normal;
                        Activate();
                    }
                    if (InvokeRequired)
                        BeginInvoke(BringUp);
                    else
                        BringUp();
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
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(0),
        };
        // 0 顶栏 / 1 SMAPI 折叠条 / 2 SMAPI 展开体 / 3 Mod 管理区（含统计与下载）
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _rootLayout = root;
        root.Controls.Add(BuildTopBar(), 0, 0);
        root.Controls.Add(BuildSmapiStrip(), 0, 1);
        root.Controls.Add(BuildSmapiBody(), 0, 2);
        root.Controls.Add(BuildModArea(), 0, 3);

        Controls.Add(root);
    }

    /// <summary>顶栏：设置 / 日志 …… Nexus 连接状态。</summary>
    private Control BuildTopBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgSoft };

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.BgSoft,
            Padding = new Padding(12, 4, 0, 0),
        };
        var btnSettings = new ModernButton
        {
            Text = "设置",
            Glyph = "\uE713",
            GlyphSize = 10,
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 32,
            Font = Theme.Ui(9f),
            Margin = new Padding(0, 0, 4, 0),
        };
        btnSettings.Click += (_, _) => OpenSettings();

        _btnLogs = new ModernButton
        {
            Text = "日志",
            Glyph = "\uE9D9",
            GlyphSize = 10,
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 32,
            Font = Theme.Ui(9f),
            Margin = new Padding(0),
        };
        _btnLogs.Click += (_, _) => new LogViewerForm().Show(this);

        left.Controls.Add(btnSettings);
        left.Controls.Add(_btnLogs);
        bar.Controls.Add(left);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.BgSoft,
            Padding = new Padding(0, 8, 14, 0),
        };
        _netPill = new Pill("正在检查网络", Theme.TextMuted);
        right.Controls.Add(_netPill);
        bar.Controls.Add(right);

        return bar;
    }

    /// <summary>SMAPI 折叠条：一行说清状态，右侧按钮展开完整区域。</summary>
    private Control BuildSmapiStrip()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            Margin = new Padding(16, 8, 16, 4),
            Padding = new Padding(0),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        inner.Controls.Add(new LogoBadge
        {
            Letter = "S",
            Size = new Size(26, 26),
            CornerRadius = 8,
            Margin = new Padding(14, 7, 0, 0),
        }, 0, 0);

        _smapiSummary = new Label
        {
            Text = "正在检测 SMAPI…",
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Ui(9.5f),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(10, 0, 0, 0),
        };
        inner.Controls.Add(_smapiSummary, 1, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 8, 12, 0),
            Margin = new Padding(0),
        };
        _btnExpandSmapi = new ModernButton
        {
            Text = "展开",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 28,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(0),
        };
        _btnExpandSmapi.Click += (_, _) => ToggleSmapiPanel();
        actions.Controls.Add(_btnExpandSmapi);
        inner.Controls.Add(actions, 2, 0);

        card.Controls.Add(inner);
        return card;
    }

    /// <summary>SMAPI 展开体：游戏目录 + 版本状态 + 安装/卸载按钮。</summary>
    private Control BuildSmapiBody()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            Margin = new Padding(16, 0, 16, 6),
            Padding = new Padding(18, 12, 18, 12),
            Visible = false,
        };
        _smapiBody = card;

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));

        _statusList = new StatusList
        {
            Dock = DockStyle.Fill,
            RowHeight = 28,
            LabelWidth = 84,
            Margin = new Padding(0),
        };
        inner.Controls.Add(_statusList, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(8, 0, 0, 0),
            Margin = new Padding(0),
        };
        _btnInstall = new ModernButton
        {
            Text = "安装 SMAPI",
            Variant = ButtonVariant.Primary,
            AutoSize = false,
            Size = new Size(152, 34),
            Font = Theme.Ui(9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 6),
        };
        _btnInstall.Click += async (_, _) => await ActionAsync(uninstall: false);

        _btnUninstall = new ModernButton
        {
            Text = "卸载 SMAPI",
            Variant = ButtonVariant.Danger,
            AutoSize = false,
            Size = new Size(152, 34),
            Margin = new Padding(0, 0, 0, 6),
        };
        _btnUninstall.Click += async (_, _) => await ActionAsync(uninstall: true);

        _btnManual = new ModernButton
        {
            Text = "手动安装…",
            Variant = ButtonVariant.Ghost,
            AutoSize = false,
            Size = new Size(152, 34),
            Margin = new Padding(0),
        };
        _btnManual.Click += async (_, _) => await ManualInstallClicked();

        buttons.Controls.Add(_btnInstall);
        buttons.Controls.Add(_btnUninstall);
        buttons.Controls.Add(_btnManual);
        inner.Controls.Add(buttons, 1, 0);

        card.Controls.Add(inner);
        return card;
    }

    /// <summary>主内容：Mod 管理区（用户选定的单窗口骨架）。</summary>
    private Control BuildModArea()
    {
        _modHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        return _modHost;
    }

    /*********
    ** 启动
    *********/
    private async Task StartupAsync()
    {
        DetectGames();
        RefreshUi();

        // 首次使用 / 没装 SMAPI：自动展开 SMAPI 区，把安装入口直接摆出来，
        // 否则用户看到的是一个空列表，不知道该先干什么。
        var target = SelectedTarget;
        if (target is not null && !GameInspector.HasSmapiFiles(target))
            ExpandSmapi(true);

        EnsureModView();

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

    /// <summary>
    /// 从候选里挑当前目标：优先带 SMAPI 的目录，其次上次用过的，最后第一个。
    /// 新骨架里没有游戏选择列表（位置在设置里改），这里只负责确定「当前目标」。
    /// </summary>
    private void FillGameSelector()
    {
        if (_games.Count == 0)
        {
            _currentTarget = null;
            return;
        }

        int select = _games.FindIndex(g => GameInspector.HasSmapiFiles(g.GamePath));
        if (select < 0 && _config.LastGamePath is not null)
            select = _games.FindIndex(g => SamePath(g.GamePath, _config.LastGamePath));
        if (select < 0)
            select = 0;

        _currentTarget = _games[select].GamePath;
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

    /// <summary>当前选中的游戏目录（设置里改，主界面只读）。</summary>
    private string? SelectedTarget => _currentTarget;

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
        _btnInstall.Text = MainButtonText();
    }

    /// <summary>
    /// 主按钮文案，只保留两种形态：
    ///   - 没装过（含 SMAPI 文件残缺）→ 安装 SMAPI
    ///   - 已装过（无论是有新版可更新，还是已是最新重装）→ 更新 SMAPI
    /// </summary>
    private string MainButtonText()
    {
        var target = SelectedTarget;
        if (target is null)
            return "安装 SMAPI";

        // 没装、或文件残缺 → 都是「装」这一个动作
        if (!GameInspector.HasSmapiFiles(target) || GameInspector.IsSmapiBroken(target))
            return "安装 SMAPI";

        // 已装 → 统一显示「更新 SMAPI」（官方安装器幂等，重装即更新）
        return "更新 SMAPI";
    }

    private void RefreshStatus()
    {
        var target = SelectedTarget;
        if (target is null)
        {
            _smapiSummary.Text = "尚未选择游戏目录 · 请到「设置」里指定";
            _smapiSummary.ForeColor = Theme.Warn;
            _statusList.SetRows(
            [
                new StatusList.Row("状态", "等待选择游戏目录", Theme.Warn),
                new StatusList.Row("提示", "点顶部「设置」选择游戏目录", Theme.TextMuted),
                new StatusList.Row("兼容", "Steam / GOG / 绿色版都可手动指定", Theme.TextMuted),
            ]);
            return;
        }

        var source = _games.FirstOrDefault(g => SamePath(g.GamePath, target))?.LibraryPath;
        var rows = new List<StatusList.Row>();
        rows.Add(new StatusList.Row("游戏目录", target, Theme.TextMuted));

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
        UpdateSmapiSummary(target);
    }

    /// <summary>折叠条上那一行摘要：把最关键的信息压成一句话。</summary>
    private void UpdateSmapiSummary(string? target)
    {
        if (_smapiSummary is null)
            return;

        if (target is null)
        {
            _smapiSummary.Text = "尚未选择游戏目录 · 请到「设置」里指定";
            _smapiSummary.ForeColor = Theme.Warn;
            return;
        }

        var installed = GameInspector.HasSmapiFiles(target);
        var version = GameInspector.GetSmapiVersion(target);
        var broken = GameInspector.IsSmapiBroken(target);

        if (broken)
        {
            _smapiSummary.Text = "SMAPI 文件残缺 · 展开可覆盖修复";
            _smapiSummary.ForeColor = Theme.Danger;
            return;
        }

        if (!installed)
        {
            var latest = _latestRelease?.TagName;
            _smapiSummary.Text = latest is null
                ? "未安装 SMAPI · 展开可安装"
                : $"未安装 SMAPI · 最新 {latest}";
            _smapiSummary.ForeColor = Theme.Warn;
            return;
        }

        var current = version?.ToString() ?? "版本未知";
        if (_latestRelease is not null
            && Version.TryParse(_latestRelease.TagName, out var latestVer)
            && version is not null
            && latestVer > version)
        {
            _smapiSummary.Text = $"SMAPI {current} · 可更新到 {_latestRelease.TagName}";
            _smapiSummary.ForeColor = Theme.Warn;
        }
        else
        {
            _smapiSummary.Text = $"SMAPI {current} · 已安装 · 已是最新";
            _smapiSummary.ForeColor = Theme.Text;
        }
    }

    /*********
    ** Mod 管理
    *********/
    /// <summary>Mod 管理已经内嵌在主窗口里；这里保留「重建视图」能力（游戏目录变化后用）。</summary>
    private void ReloadModView()
    {
        if (_modView is not null)
        {
            _modHost.Controls.Remove(_modView);
            _modView.Dispose();
            _modView = null;
        }
        EnsureModView();
    }

    /// <summary>在资源管理器里打开游戏目录。</summary>
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
                SmapiRelease? tryLatest = null;
                try
                {
                    tryLatest = _latestRelease ?? await _service.GetLatestAsync();
                    Log($"目标版本：SMAPI {tryLatest.TagName}");
                    zipPath = await GetInstallerZipAsync(tryLatest)
                              ?? throw new OperationCanceledException("下载已中止。");
                    latest = tryLatest;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // GitHub 彻底连不上（连最新版本信息都拿不到）→ 走 Nexus 官方页面兜底：
                    // 有 key 时直接从 Nexus 自动下载；没有 key 就打开 Nexus 页面引导浏览器下载。
                    var nexusZip = await DownloadViaNexusFallbackAsync(Log);
                    if (nexusZip is null)
                        throw new OperationCanceledException("下载已中止。");

                    zipPath = nexusZip;
                    latest = null; // GitHub 信息拿不到，版本号装完后从游戏目录读取
                }
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

                // GitHub 全部下载源（直连 + 镜像）都失败 → 转走 Nexus 官方页面（mod 2400）兜底
                var nexusZip = await DownloadViaNexusFallbackAsync(Log);
                if (nexusZip is not null)
                    return nexusZip;

                // Nexus 兜底也没成（用户取消或同样失败）→ 保留原来的手动选文件流程
                return await PickInstallerManuallyAsync(release);
            }
        }
        finally
        {
            _zipGate.Release();
        }
    }

    /*********
    ** Nexus 备选下载渠道（GitHub 全挂时用）
    *********/
    /// <summary>
    /// GitHub 下载全部失败后的 Nexus 兜底（官方页面 {SmapiNexusPageUrl}）：
    ///   1. 已保存 Nexus API key → 自动拿 SMAPI 的最新主文件尝试下载：
    ///      Premium 会员能直接下成功；免费账号 Nexus 会拒绝（403），自动转第 2 步。
    ///   2. 没 key 或 403 → 自动打开 Nexus 的 SMAPI 文件页，用户在浏览器里下载 zip
    ///      后选回来。zip 本身仍过官方 SHA-256 校验（哈希来自 GitHub release 信息，
    ///      GitHub 也连不上时按官方渠道提醒确认后放行）。
    /// 用户中途取消返回 null。
    /// </summary>
    private async Task<string?> DownloadViaNexusFallbackAsync(Action<string> log)
    {
        log("GitHub 下载不通，改走 Nexus 官方页面（stardewvalley/mods/2400）…");

        var apiKey = _config.NexusApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var client = new NexusClient(apiKey);
            try
            {
                var file = await client.GetLatestMainFileAsync(SmapiNexusModId);
                log($"Nexus 上的 SMAPI 主文件：{file.FileName ?? file.Name}（版本 {file.ModVersion ?? file.Version ?? "?"}）");

                var workspace = SmapiInstallerRunner.WorkspaceDirPath;
                var zipPath = await client.DownloadAsync(SmapiNexusModId, workspace, log, file: file);

                // 有 GitHub 的官方哈希就照常校验（Nexus 上传的就是同一个文件，应当一致）
                if (_latestRelease is { } rel && !string.IsNullOrWhiteSpace(rel.Sha256))
                {
                    log("校验 Nexus 下载的安装包（对照官方 SHA-256）…");
                    var hash = await GitHubReleaseService.ComputeSha256Async(zipPath);
                    if (!string.Equals(hash, rel.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(zipPath);
                        log("❌ Nexus 下载的安装包哈希校验失败，已删除。转浏览器手动下载流程。");
                        return await OpenNexusPageAndPickAsync(log);
                    }
                    log("✅ 安装包校验通过。");
                }

                log("✅ 已从 Nexus 下载 SMAPI 安装包。");
                return zipPath;
            }
            catch (NexusApiException ex) when (ex.StatusCode is 401 or 403)
            {
                // 免费账号 API 直下会被拒（403），key 失效是 401 —— 都不中断流程，转浏览器引导
                log($"Nexus API 下载被拒（{ex.StatusCode}）：{ex.Message}");
                log("转浏览器手动下载流程。");
            }
            catch (Exception ex)
            {
                log($"⚠ Nexus API 下载失败：{ex.Message}\n转浏览器手动下载流程。");
            }
        }
        else
        {
            log("未配置 Nexus API key，走浏览器手动下载流程（免费账号也能用）。");
        }

        return await OpenNexusPageAndPickAsync(log);
    }

    /// <summary>打开 Nexus 的 SMAPI 文件页，等用户浏览器下载 zip 后选回来安装。取消返回 null。</summary>
    private async Task<string?> OpenNexusPageAndPickAsync(Action<string> log)
    {
        log($"打开 Nexus 文件页：{SmapiNexusPageUrl}");

        var pageUrl = SmapiNexusPageUrl + "?tab=files";
        var opened = await ShellHelper.OpenUrlAsync(pageUrl, log);
        if (opened)
        {
            log("Nexus 文件页已打开。");
        }
        else
        {
            // 浏览器拉不起来也不要卡死流程：链接进剪贴板，用户可以自己粘贴
            var copied = ShellHelper.TryCopyToClipboard(pageUrl);
            log($"⚠ 浏览器没能自动打开：{ShellHelper.LastError}" +
                (copied ? "（链接已复制到剪贴板，可粘贴到浏览器）" : ""));
        }

        var choice = Msg.Show(this,
            "GitHub 下载全部失败，已切换到 Nexus 官方页面。\n\n" +
            (opened
                ? "已为你打开浏览器：\n"
                : "浏览器没能自动打开，链接已复制到剪贴板，粘贴到浏览器即可：\n") +
            $"    {SmapiNexusPageUrl}\n\n" +
            "在 Nexus 页面点「MOD FILES → Manual Download」（免费账号也可下载），\n" +
            "下载完成后回到这里选择 zip 文件，程序会校验后自动安装。\n\n" +
            "选好了安装包文件吗？",
            "Nexus 备选下载", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        if (choice != DialogResult.Yes)
        {
            log("用户没有选择 Nexus 下载的安装包。");
            return null;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择从 Nexus 下载的 SMAPI 安装包 zip",
            Filter = "SMAPI 安装包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            log("未选择安装包文件。");
            return null;
        }

        var zip = dialog.FileName;
        log($"Nexus 手动导入安装包：{zip}");

        // 有 GitHub 的官方哈希就照常校验（Nexus 上传的就是同一个文件，应当一致）
        if (_latestRelease is { } release && !string.IsNullOrWhiteSpace(release.Sha256))
        {
            log("校验安装包（对照官方 SHA-256）…");
            var hash = await GitHubReleaseService.ComputeSha256Async(zip);
            if (!string.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "校验失败：选择的文件与官方 SMAPI " + release.TagName + " 不一致。\n" +
                    "文件哈希: " + hash + "\n" +
                    "官方哈希: " + release.Sha256 + "\n\n" +
                    "请确认下载的是 Nexus 上的 SMAPI 安装器 zip（而不是别的文件）后重试。");
            log("✅ 安装包校验通过。");
        }
        else
        {
            // GitHub 连版本信息都拿不到，没有官方哈希可对照 —— 明确告知后按官方渠道放行
            var force = Msg.Show(this,
                "暂时连不上 GitHub，无法对照官方 SHA-256 校验这个文件。\n" +
                "Nexus 页面下载的 SMAPI 由作者本人维护，一般是安全的；\n" +
                "SMAPI 4.5+ 的安装器也自带恶意文件检测。\n\n继续安装？",
                "无法校验", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (force != DialogResult.Yes)
            {
                log("用户取消安装（未通过校验确认）。");
                return null;
            }
        }

        return zip;
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
        // 全部落盘：日志查看器的来源下拉里可以选「本工具操作日志」回看完整历史
        Services.AppLog.Write(message);

        if (InvokeRequired)
        {
            BeginInvoke(() => Log(message));
            return;
        }
        if (IsDisposed)
            return;

        // 折叠条显示最近一条操作反馈（完整历史去「日志 → 本工具操作日志」看）
        if (_smapiSummary is not null)
        {
            _smapiSummary.Text = message;
            _smapiSummary.ForeColor = message.StartsWith("❌") || message.StartsWith("🗑")
                ? Theme.Danger
                : message.StartsWith("✅")
                    ? Theme.Success
                    : message.StartsWith("⚠") || message.StartsWith("🔴")
                        ? Theme.Warn
                        : Theme.TextMuted;
        }
    }

    /*********
    ** 新骨架的交互
    *********/
    /// <summary>展开 / 收起 SMAPI 区域。</summary>
    private void ToggleSmapiPanel() => ExpandSmapi(_rootLayout.RowStyles[2].Height <= 0);

    /// <summary>展开或收起 SMAPI 区域。</summary>
    private void ExpandSmapi(bool expand)
    {
        if (_rootLayout is null || _smapiBody is null)
            return;
        _rootLayout.RowStyles[2].Height = expand ? 150 : 0;
        _smapiBody.Visible = expand;
        _btnExpandSmapi.Text = expand ? "收起" : "展开";
        _rootLayout.PerformLayout();
    }

    /// <summary>打开设置窗口；返回后重读配置并刷新。</summary>
    private void OpenSettings()
    {
        using var dialog = new SettingsDialog(_config, _currentTarget, PickManually);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _config.Save();
        DetectGames();
        RefreshUi();
        EnsureModView();
    }

    /// <summary>把 Mod 管理区挂到主窗口里（首次进入或游戏目录变化时重建）。</summary>
    private void EnsureModView()
    {
        var target = SelectedTarget;
        if (target is null || _modHost is null)
            return;

        if (_modView is not null && SamePath(_modView.GamePath, target))
            return;

        if (_modView is not null)
        {
            _modHost.Controls.Remove(_modView);
            _modView.Dispose();
        }

        _modView = new ModManagerView(target) { Dock = DockStyle.Fill };
        _modHost.Controls.Add(_modView);
        _modView.Logged += Log;
        _modView.OpenDownloads = ShowDownloadQueue;
    }

    /// <summary>
    /// 日志查看器跳转过来：定位到指定游戏目录的 Mod 管理区，并预填筛选框。
    /// </summary>
    public void FocusMod(string modName, string gamePath)
    {
        Activate();
        EnsureModView();

        if (_modView is not null && !SamePath(_modView.GamePath, gamePath))
        {
            // 目标游戏和当前的不同：切换目标再重建视图
            _config.LastGamePath = gamePath;
            _config.Save();
            DetectGames();
            ReloadModView();
        }

        if (_modView is not null)
            _modView.ApplyFocusMod(modName);
    }

    /// <summary>打开下载队列窗口。</summary>
    private void ShowDownloadQueue()
    {
        _nxmForm ??= new NxmDownloadForm();
        if (_nxmForm.IsDisposed)
            _nxmForm = new NxmDownloadForm();

        if (!_nxmForm.Visible)
            _nxmForm.Show(this);
        else
            _nxmForm.Activate();
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
