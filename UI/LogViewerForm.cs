using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// SMAPI 日志查看器：
/// 读 %APPDATA%\StardewValley\ErrorLogs 下的 SMAPI 日志，按级别过滤/检索，
/// 错误红色高亮，选中条目展示完整多行信息（含堆栈），可直接复制去求助。
///
/// 默认隐藏 TRACE/DEBUG（那是给开发者看的海量输出），先看错误和警告。
/// </summary>
internal sealed class LogViewerForm : Form
{
    /*********
    ** 状态
    *********/
    private List<SmapiLogEntry> _allEntries = [];
    private SmapiLogMeta _meta = new(null, null, null, null, null);
    private List<(ListViewItem Item, SmapiLogEntry Entry)> _rows = [];  // 可见行（已按过滤裁掉）
    private string? _loadedPath;
    private string _summaryBase = "";      // 概要的主体（加载文件后写入），过滤结果会追加尾巴
    private bool _loadingAppLog;           // 当前下拉选中的是「本工具操作日志」还是 SMAPI 日志
    private List<LogEntry> _fileEntries = [];

    /*********
    ** 控件
    *********/
    private ModernComboBox _fileSelector = null!;
    private ModernButton _btnReloadFiles = null!;
    private ModernButton _btnOpenFile = null!;
    private ModernButton _btnOpenDir = null!;
    private TextField _searchField = null!;
    private ModernCheckBox _cbInfo = null!;
    private ModernCheckBox _cbWarn = null!;
    private ModernCheckBox _cbError = null!;
    private ModernCheckBox _cbDebug = null!;
    private ModernButton _btnNextError = null!;
    private ModernButton _btnCopy = null!;
    private ModernButton _btnLocateMod = null!;
    private Label _summary = null!;
    private ThemedListView _list = null!;
    private RichTextBox _detailBox = null!;
    private Pill _errorPill = null!;
    private Pill _warnPill = null!;

    public LogViewerForm()
    {
        Theme.StyleForm(this);
        Text = "SMAPI 日志查看器";
        MinimumSize = new Size(1020, 680);
        Size = new Size(1180, 820);
        StartPosition = FormStartPosition.CenterParent;

        BuildUi();
        Shown += async (_, _) => await LoadFileListAsync();
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
            Padding = new Padding(18, 14, 18, 10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));  // 0: 文件 / 过滤
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // 1: 概要
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 2: 列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));  // 3: 详情
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // 4: 页脚

        root.Controls.Add(BuildFileCard(), 0, 0);

        _summary = new Label
        {
            Text = "未加载日志。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui(8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Padding = new Padding(2, 0, 0, 4),
            Margin = new Padding(0),
        };
        root.Controls.Add(_summary, 0, 1);

        var listCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            Padding = new Padding(1),
            Margin = new Padding(0, 0, 0, 10),
        };
        _list = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = false,
            ShowStatusDot = true,
            RowHeight = 28,
        };
        _list.Columns.AddRange(
        [
            new ColumnHeader { Text = "时间", Width = 76 },
            new ColumnHeader { Text = "级别", Width = 82 },
            new ColumnHeader { Text = "来源", Width = 210 },
            new ColumnHeader { Text = "内容", Width = 700 },
        ]);
        _list.SelectedIndexChanged += (_, _) => ShowDetail();
        listCard.Controls.Add(_list);
        root.Controls.Add(listCard, 0, 2);

        var detailCard = new RichTextCard("详情") { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 10) };
        _detailBox = detailCard.Box;
        _btnCopy = detailCard.AddAction("复制这条", CopyDetail);
        _btnLocateMod = detailCard.AddAction("在 Mod 管理器中定位…", LocateInModManager);
        _btnLocateMod.Enabled = false;
        root.Controls.Add(detailCard, 0, 3);

        root.Controls.Add(new Label
        {
            Text = "提示：装了 mod 打不开游戏时，先看这里红色的行。「复制这条」把完整信息（含堆栈）复制出去求助最方便。",
            AutoSize = true,
            ForeColor = Theme.TextDim,
            Font = Theme.Ui(8.5f),
            Margin = new Padding(2, 7, 0, 0),
        }, 0, 4);

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
            Text = "日志查看器",
            Font = Theme.Ui(16f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(88, 15),
        });
        hero.Controls.Add(new Label
        {
            Text = "SMAPI 日志 · 按级别过滤 · 定位报错 Mod",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(90, 48),
        });

        _errorPill = new Pill("0 条错误", Theme.Danger);
        _warnPill = new Pill("0 条警告", Theme.Warn);
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 300,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 33, 20, 0),
        };
        right.Controls.Add(_errorPill);
        right.Controls.Add(_warnPill);
        hero.Controls.Add(right);

        return hero;
    }

    private Control BuildFileCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 8),
            Margin = new Padding(0, 0, 0, 8),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var fileRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _fileSelector = new ModernComboBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 5, 10, 5),
        };
        _fileSelector.SelectedIndexChanged += async (_, _) => await LoadSelectedFileAsync();
        fileRow.Controls.Add(_fileSelector, 0, 0);

        _btnReloadFiles = MakeButton("刷新列表", ButtonVariant.Ghost, 96);
        _btnReloadFiles.Click += async (_, _) => { await LoadFileListAsync(keepFilter: true); };
        fileRow.Controls.Add(_btnReloadFiles, 1, 0);

        _btnOpenFile = MakeButton("打开原文件", ButtonVariant.Secondary, 108);
        _btnOpenFile.Click += (_, _) => OpenLogExternally(file: true);
        fileRow.Controls.Add(_btnOpenFile, 2, 0);

        _btnOpenDir = MakeButton("打开目录", ButtonVariant.Secondary, 96);
        _btnOpenDir.Click += (_, _) => OpenLogExternally(file: false);
        fileRow.Controls.Add(_btnOpenDir, 3, 0);

        var filterRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _searchField = new TextField("按内容搜索（来源、错误信息…）")
        {
            Dock = DockStyle.Fill,
            Height = 34,
            Margin = new Padding(0, 5, 14, 5),
        };
        _searchField.Box.TextChanged += (_, _) => { RebuildVisible(); };
        filterRow.Controls.Add(_searchField, 0, 0);

        var levels = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 11, 0, 0),
        };
        levels.Controls.Add(MakeLevelToggle("INFO", true, out _cbInfo));
        levels.Controls.Add(MakeLevelToggle("ALERT/WARN", true, out _cbWarn));
        levels.Controls.Add(MakeLevelToggle("ERROR", true, out _cbError));
        levels.Controls.Add(MakeLevelToggle("DEBUG/TRACE", false, out _cbDebug));
        filterRow.Controls.Add(levels, 1, 0);

        _btnNextError = MakeButton("跳到下个错误", ButtonVariant.Primary, 116);
        _btnNextError.Margin = new Padding(8, 5, 0, 5);
        _btnNextError.Click += (_, _) => JumpToNextError();
        filterRow.Controls.Add(_btnNextError, 2, 0);

        inner.Controls.Add(fileRow, 0, 0);
        inner.Controls.Add(filterRow, 0, 1);
        card.Controls.Add(inner);
        return card;
    }

    private static ModernButton MakeButton(string text, ButtonVariant variant, int width)
        => new()
        {
            Text = text,
            Variant = variant,
            AutoSize = true,
            MinimumSize = new Size(width, 34),
            Height = 34,
            Margin = new Padding(8, 4, 0, 4),
        };

    private ModernCheckBox MakeLevelToggle(string label, bool @default, out ModernCheckBox box)
    {
        box = new ModernCheckBox
        {
            Text = label,
            Checked = @default,
            Margin = new Padding(0, 0, 16, 0),
        };
        box.CheckedChanged += (_, _) => RebuildVisible();
        return box;
    }

    /*********
    ** 加载
    *********/
    /// <summary>重新列出日志目录里的文件。<paramref name="keepFilter"/> 选中 ALARM 保持当前选择（手动点「刷新列表」时用）。</summary>
    /// <summary>
    /// 下拉里合并两类日志：
    ///   [工具] app-yyyyMMdd.log  —— 本工具自己的操作流水（SMAPI 安装/更新、mod 安装等）
    ///   SMAPI-latest.txt …      —— 游戏的 SMAPI 日志
    /// 工具日志永远排最前（这是用户最常看的）。
    /// </summary>
    private async Task LoadFileListAsync(bool keepFilter = false)
    {
        var smapiFiles = SmapiLogParser.GetLogFiles();
        var appFiles = AppLog.GetFiles();
        _fileEntries = [.. appFiles.Select(f => new LogEntry(f, true)),
                        .. smapiFiles.Select(f => new LogEntry(f, false))];

        var selectedPath = keepFilter ? _loadedPath : null;

        _fileSelector.BeginUpdate();
        try
        {
            _fileSelector.Items.Clear();
            foreach (var e in _fileEntries)
            {
                var tag = e.IsAppLog ? "[工具] " : "";
                _fileSelector.Items.Add($"{tag}{e.File.Name}（{e.File.LastWriteTime:MM-dd HH:mm}，{Math.Max(1, e.File.Length / 1024.0):F0} KB）");
            }
        }
        finally
        {
            _fileSelector.EndUpdate();
        }

        if (_fileEntries.Count == 0)
        {
            _summary.Text = "还没有任何日志。\n" +
                            "· 本工具的操作日志在装/卸 SMAPI、安装 mod 后产生\n" +
                            "· SMAPI 日志在用 SMAPI 启动一次游戏后产生\n" +
                            GameInspector.ErrorLogDirectory;
            return;
        }

        // 默认选中：优先记住上次看的；否则最新一条工具日志；否则 SMAPI 最新
        int select = 0;
        if (_loadedPath is not null)
        {
            var kept = _fileEntries.FindIndex(e => SamePath(e.File.FullName, selectedPath ?? _loadedPath));
            if (kept >= 0)
                select = kept;
        }
        else if (appFiles.Count == 0)
        {
            // 没有 SMAPI-crash 优先逻辑要照顾（工具日志不存在时才用原逻辑）
            var crash = smapiFiles.FindIndex(f => string.Equals(f.Name, "SMAPI-crash.txt", StringComparison.OrdinalIgnoreCase));
            if (crash >= 0)
                select = appFiles.Count + crash;
        }

        _fileSelector.SelectedIndex = select; // 触发 LoadSelectedFileAsync
        if (_fileSelector.SelectedIndex < 0)
            await SelectFirstFileAsync();
    }

    /// <summary>下拉里的一项：文件 + 是不是本工具日志。</summary>
    private sealed record LogEntry(FileInfo File, bool IsAppLog);

    private async Task SelectFirstFileAsync()
    {
        if (_fileSelector.Items.Count > 0)
        {
            _fileSelector.SelectedIndex = 0;
            await LoadSelectedFileAsync();
        }
    }

    private async Task LoadSelectedFileAsync()
    {
        int index = _fileSelector.SelectedIndex;
        if (index < 0 || index >= _fileEntries.Count)
            return;

        var entry = _fileEntries[index];
        SetBusy(true);
        try
        {
            if (entry.IsAppLog)
                await LoadAppLogAsync(entry.File);
            else
                await LoadSmapiLogAsync(entry.File);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>加载本工具的操作日志：直接按行读，级别从消息前缀推断。</summary>
    private async Task LoadAppLogAsync(FileInfo file)
    {
        _loadingAppLog = true;
        _summary.Text = $"读取中… {file.Name}";
        try
        {
            var text = await File.ReadAllTextAsync(file.FullName);
            var entries = new List<SmapiLogEntry>();
            int lineNo = 0;
            foreach (var raw in text.Split('\n'))
            {
                lineNo++;
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                // [HH:mm:ss] 消息
                string time = "", message = line;
                if (line.StartsWith('['))
                {
                    int close = line.IndexOf("] ");
                    if (close > 0)
                    {
                        time = line[1..close];
                        message = line[(close + 2)..];
                    }
                }

                string level = message.StartsWith("❌") || message.StartsWith("🗑")
                    ? "ERROR"
                    : message.StartsWith("⚠") || message.StartsWith("🔴")
                        ? "WARN"
                        : message.StartsWith("✅")
                            ? "INFO"
                            : "INFO";

                entries.Add(new SmapiLogEntry(lineNo, time, level, "工具", message));
            }

            // 倒序展示（最新的在最上，工具日志通常只有几十行，SMAPI 那种滚动定位没必要）
            entries.Reverse();
            _allEntries = entries;
            _meta = new SmapiLogMeta(null, null, null, null, null);
            _loadedPath = file.FullName;

            // 工具日志没有 mod 定位语义
            _btnLocateMod.Enabled = false;

            int errors = entries.Count(e => e.IsError);
            _errorPill.Text = $"{errors} 条错误";
            _warnPill.Text = $"{entries.Count(e => e.Level == "WARN")} 条警告";

            _summaryBase = $"本工具操作日志 · 共 {entries.Count} 条" +
                (errors > 0 ? $" · {errors} 条错误" : "");
            _summary.ForeColor = errors > 0 ? Theme.Danger : Theme.TextMuted;
            _summary.Text = _summaryBase;
            RebuildVisible();
        }
        catch (Exception ex)
        {
            _summary.ForeColor = Theme.Danger;
            _summary.Text = $"读取日志失败：{ex.Message}";
        }
    }

    /// <summary>加载 SMAPI 日志（原有逻辑抽到这里）。</summary>
    private async Task LoadSmapiLogAsync(FileInfo file)
    {
        _loadingAppLog = false;
        try
        {
            _summary.Text = $"读取中… {file.Name}";
            (_meta, _allEntries) = await SmapiLogParser.ParseFileAsync(file.FullName);
            _loadedPath = file.FullName;

            RebuildVisible(jumpToError: true);

            var parts = new List<string>();
            if (_meta.SmapiVersion is { } smapi)
                parts.Add("SMAPI {smapi}");
            if (_meta.GameVersion is { } game)
                parts.Add("游戏 {game}");
            if (_meta.ModCount is { } mods)
                parts.Add("{mods} 个 mod");
            if (_meta.LogStartedAt is { } started)
                parts.Add("记录于 {started.Trim()}");

            int errors = _allEntries.Count(e => e.IsError);
            int warns = _allEntries.Count(e => e.Level is "ALERT" or "WARN");
            var badSources = _allEntries.Where(e => e.IsError)
                .Select(e => e.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            _errorPill.Text = $"{errors} 条错误";
            _warnPill.Text = $"{warns} 条警告";

            var summary = string.Join("  |  ", parts);
            if (summary.Length > 0)
                summary += Environment.NewLine;
            summary += $"共 {_allEntries.Count} 条消息（已隐藏诊断噪音）：{errors} 条错误、{warns} 条警告/提醒";
            if (badSources.Count > 0)
                summary += $"\n出错来源：{string.Join("、", badSources)}";

            if (string.Equals(file.Name, "SMAPI-crash.txt", StringComparison.OrdinalIgnoreCase))
                summary = "🔴 这是上次崩溃时留下的现场（SMAPI-crash.txt）！\n" + summary;

            _summary.ForeColor = errors > 0 ? Theme.Danger : Theme.TextMuted;
            _summaryBase = summary;
            _summary.Text = summary;
        }
        catch (Exception ex)
        {
            _allEntries = [];
            _meta = new SmapiLogMeta(null, null, null, null, null);
            RebuildVisible();
            _summary.ForeColor = Theme.Danger;
            _summary.Text = $"读取日志失败：{ex.Message}";
            _errorPill.Text = "读取失败";
        }
    }

    private void SetBusy(bool busy)
    {
        UseWaitCursor = busy;
        _btnReloadFiles.Enabled = !busy;
        _fileSelector.Enabled = !busy;
    }

    /*********
    ** 过滤与列表
    *********/
    /// <summary>按级别复选框 + 搜索词重建可见列表（普通 ListView 全量重建；默认级别下可见行数很小）。</summary>
    private void RebuildVisible(bool jumpToError = false)
    {
        bool showInfo = _cbInfo.Checked;
        bool showWarn = _cbWarn.Checked;
        bool showError = _cbError.Checked;
        bool showDebug = _cbDebug.Checked;

        bool LevelVisible(string level) => level switch
        {
            "INFO" => showInfo,
            "ALERT" or "WARN" => showWarn,
            "ERROR" or "CRITICAL" => showError,
            "DEBUG" or "TRACE" => showDebug,
            _ => false,
        };

        var filter = _searchField.Box.Text.Trim();
        const int maxRows = 20000; // 勾上 DEBUG/TRACE 大日志可能爆多，截断保护

        _rows = [];
        int hidden = 0;
        for (int i = 0; i < _allEntries.Count; i++)
        {
            var entry = _allEntries[i];
            if (!LevelVisible(entry.Level))
                continue;
            if (filter.Length > 0
                && !entry.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !entry.Time.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            if (_rows.Count >= maxRows)
            {
                hidden++;
                continue;
            }
            _rows.Add((MakeItem(entry), entry));
        }

        _list.BeginUpdate();
        try
        {
            _list.SelectedItems.Clear();
            _list.Items.Clear();
            var tinyItems = new ListViewItem[_rows.Count];
            for (int i = 0; i < _rows.Count; i++)
                tinyItems[i] = _rows[i].Item;
            _list.Items.AddRange(tinyItems);
        }
        finally
        {
            _list.EndUpdate();
        }

        if (hidden > 0)
            _summary.Text = _summaryBase + Environment.NewLine + $"⚠ 消息太多，只显示了前 {maxRows} 行。缩小搜索范围或关掉 DEBUG/TRACE 再看。";
        else
            _summary.Text = _summaryBase;

        if (jumpToError && _rows.Count > 0)
        {
            // 默认滚到第一条错误，别让用户自己一行行找
            int firstError = _rows.FindIndex(r => r.Entry.IsError);
            JumpTo(firstError >= 0 ? firstError : 0);
        }
    }

    private ListViewItem MakeItem(SmapiLogEntry entry)
    {
        var item = new ListViewItem(entry.Time) { UseItemStyleForSubItems = true };
        item.SubItems.Add(entry.Level);
        item.SubItems.Add(entry.Source);
        item.SubItems.Add(entry.FirstLine);

        item.ForeColor = entry.Level switch
        {
            "ERROR" or "CRITICAL" => Theme.Danger,
            "WARN" => Theme.Warn,
            "ALERT" => Theme.Info,
            "DEBUG" or "TRACE" => Theme.TextDim,
            _ => Theme.Text,
        };

        return item;
    }

    /*********
    ** 交互
    *********/
    private SmapiLogEntry? SelectedEntry
    {
        get
        {
            if (_list.SelectedItems.Count == 0)
                return null;
            int i = _list.SelectedIndices[0];
            return i >= 0 && i < _rows.Count ? _rows[i].Entry : null;
        }
    }

    private void ShowDetail()
    {
        var entry = SelectedEntry;
        if (entry is null)
            return;

        _detailBox.Text = $"[{entry.Time} {entry.Level} {entry.Source}]  (原文件第 {entry.LineNumber} 行)\n{entry.Message}";
        _detailBox.SelectionStart = 0;
        _detailBox.SelectionLength = 0;

        // 只有 mod 在报错（来源不是 SMAPI/Game）且日志里能解析出 Mods 目录时才有得定位
        _btnLocateMod.Enabled = entry.IsError && entry.LogSourceIsMod && _meta.ModsFolder is not null;
    }

    private void LocateInModManager()
    {
        var entry = SelectedEntry;
        if (entry is null || _meta.ModsFolder is null)
            return;

        // 日志里记的是 Mods 目录本身；Mod 管理器吃的是游戏根目录
        var gamePath = Path.GetDirectoryName(_meta.ModsFolder.TrimEnd('\\'));
        if (gamePath is null || !Directory.Exists(Path.Combine(gamePath, "Mods")))
        {
            Msg.Show(this,
                $"日志里记录的 Mods 目录是：{_meta.ModsFolder}\n但这个目录现在不存在了（可能是游戏挪过位置）。请改从主窗口进入 Mod 管理器。",
                "无法定位", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 来源可能带分屏标记（screen_1 Content Patcher），去掉只剩 mod 名
        var modName = System.Text.RegularExpressions.Regex
            .Replace(entry.Source, @"^screen_\d+\s+", "").Trim();

        // 新骨架：Mod 管理区内嵌在主窗口。把要定位的 mod 名传给主窗，
        // 让它重建视图并预填筛选框，然后本窗口收起。
        if (Owner is MainForm main)
        {
            main.FocusMod(modName, gamePath);
            Hide();
        }
        else
        {
            using var main2 = new MainForm();
            main2.Show();
            main2.FocusMod(modName, gamePath);
        }
    }

    private void JumpToNextError()
    {
        if (_rows.Count == 0)
            return;

        int start = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] + 1 : 0;
        for (int step = 0; step < _rows.Count; step++)
        {
            int i = (start + step) % _rows.Count;
            if (_rows[i].Entry.IsError)
            {
                JumpTo(i);
                return;
            }
        }

        Msg.Show(this, "可见范围内没有错误信息（可以勾上更多级别再找找）。", "提示",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void JumpTo(int index)
    {
        if (index < 0 || index >= _rows.Count)
            return;

        _list.SelectedIndices.Clear();
        _rows[index].Item.Selected = true;
        _list.EnsureVisible(index);
        _list.FocusedItem = _rows[index].Item;
        ShowDetail();
    }

    private void CopyDetail()
    {
        try
        {
            var entry = SelectedEntry;
            var text = entry is null ? _detailBox.Text : $"[{entry.Time} {entry.Level} {entry.Source}] {entry.Message}";
            if (string.IsNullOrWhiteSpace(text))
                return;
            Clipboard.SetText(text);
            _summary.Text = "已复制选中条目到剪贴板。" + Environment.NewLine + _summary.Text;
        }
        catch
        {
            // 剪贴板偶尔被占用
        }
    }

    private void OpenLogExternally(bool file)
    {
        try
        {
            if (file && _loadedPath is { } path && File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else
                System.Diagnostics.Process.Start("explorer.exe", $"\"{GameInspector.ErrorLogDirectory}\"");
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"打开失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
