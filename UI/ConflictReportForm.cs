using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// 冲突检测报告窗口：列出重复 ID、XNB 资源覆盖、Content Patcher 同 Target 三类
/// 「多个启用中的 mod 改同一份资源」的问题，左侧分组、右侧参与者、附处置建议。
/// </summary>
internal sealed class ConflictReportForm : Form
{
    /*********
    ** 状态
    *********/
    private readonly List<ConflictGroup> _groups;
    private List<ConflictGroup> _visibleGroups = [];

    /*********
    ** 控件
    *********/
    private TextField _filterField = null!;
    private ThemedListView _groupList = null!;
    private ThemedListView _entryList = null!;
    private Label _advice = null!;
    private CardPanel _adviceCard = null!;
    private ModernButton _btnOpenFolder = null!;
    private ModernButton _btnDelete = null!;
    private Pill _countPill = null!;

    /// <summary>本次会话里删过 mod（关窗后 Mod 管理器应重新扫描）。</summary>
    public bool ChangedSomething { get; private set; }

    public ConflictReportForm(List<ConflictGroup> groups)
    {
        _groups = groups;

        Theme.StyleForm(this);
        Text = "冲突检测报告";
        MinimumSize = new Size(960, 600);
        Size = new Size(1120, 700);
        StartPosition = FormStartPosition.CenterParent;

        var hero = BuildHero();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(18, 14, 18, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));   // 0: 筛选
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 1: 双列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));   // 2: 建议
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // 3: 操作

        root.Controls.Add(BuildFilterCard(), 0, 0);
        root.Controls.Add(BuildLists(), 0, 1);
        root.Controls.Add(BuildAdvice(), 0, 2);
        root.Controls.Add(BuildActions(), 0, 3);

        Controls.Add(root);
        Controls.Add(hero);

        RefreshGroups("");
    }

    /*********
    ** 布局
    *********/
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
            Text = "冲突检测",
            Font = Theme.Ui(16f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(88, 15),
        });
        hero.Controls.Add(new Label
        {
            Text = "重复 ID · XNB 覆盖 · Content Patcher 同 Target",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(90, 48),
        });

        _countPill = new Pill("0 组冲突", Theme.Warn);
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 300,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 33, 20, 0),
        };
        right.Controls.Add(_countPill);
        hero.Controls.Add(right);

        return hero;
    }

    private Control BuildFilterCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 9, 16, 9),
            Margin = new Padding(0, 0, 0, 8),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0),
        };
        _filterField = new TextField("筛选：ID / 资源路径 / Target")
        {
            Width = 320,
            Height = 36,
            Margin = new Padding(0, 4, 14, 0),
        };
        _filterField.Box.TextChanged += (_, _) => RefreshGroups(_filterField.Box.Text);
        left.Controls.Add(_filterField);
        left.Controls.Add(new Label
        {
            Text = "只统计启用中的 mod；双击右侧参与者可直接打开文件夹。",
            AutoSize = true,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextDim,
            Margin = new Padding(0, 13, 0, 0),
        });
        grid.Controls.Add(left, 0, 0);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(12, 10, 0, 0),
        };
        right.Controls.Add(new Pill("启用中", Theme.Success));
        grid.Controls.Add(right, 1, 0);

        card.Controls.Add(grid);
        return card;
    }

    private Control BuildLists()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));

        _groupList = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = false,
            ShowStatusDot = false,
            RowHeight = 30,
            Margin = new Padding(0),
        };
        _groupList.Columns.AddRange(
        [
            new ColumnHeader { Text = "冲突", Width = 300 },
            new ColumnHeader { Text = "参与", Width = 64, TextAlign = HorizontalAlignment.Right },
        ]);
        _groupList.SelectedIndexChanged += (_, _) => SyncEntries();
        var leftCard = MakeListCard("冲突组", _groupList);
        leftCard.Margin = new Padding(0, 0, 6, 10);
        grid.Controls.Add(leftCard, 0, 0);

        _entryList = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = false,
            ShowStatusDot = false,
            RowHeight = 30,
            Margin = new Padding(0),
        };
        _entryList.Columns.AddRange(
        [
            new ColumnHeader { Text = "Mod 名称", Width = 220 },
            new ColumnHeader { Text = "版本/详情", Width = 170 },
            new ColumnHeader { Text = "位置", Width = 260 },
        ]);
        _entryList.SelectedIndexChanged += (_, _) => SyncActionButtons();
        _entryList.DoubleClick += (_, _) => OpenSelectedFolder();
        var rightCard = MakeListCard("参与者", _entryList);
        rightCard.Margin = new Padding(6, 0, 0, 10);
        grid.Controls.Add(rightCard, 1, 0);

        return grid;
    }

    private static CardPanel MakeListCard(string title, ThemedListView list)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            Padding = new Padding(10, 4, 10, 10),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        header.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Ui(9f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Location = new Point(2, 9),
            BackColor = Color.Transparent,
        });
        inner.Controls.Add(header, 0, 0);
        inner.Controls.Add(list, 0, 1);
        card.Controls.Add(inner);
        return card;
    }

    private Control BuildAdvice()
    {
        _adviceCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            FillColor = Theme.Blend(Theme.Surface, Theme.Info, 0.08),
            StrokeColor = Theme.Blend(Theme.Border, Theme.Info, 0.30),
            Padding = new Padding(16, 10, 16, 10),
            Margin = new Padding(0, 0, 0, 8),
        };

        _advice = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = "选中左侧一组冲突看建议。",
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui(9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        _adviceCard.Controls.Add(_advice);
        return _adviceCard;
    }

    private Control BuildActions()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 8, 0, 0),
        };
        _btnOpenFolder = MakeButton("打开参与者文件夹", ButtonVariant.Secondary, 156);
        _btnOpenFolder.Click += (_, _) => OpenSelectedFolder();
        _btnDelete = MakeButton("删除所选 Mod…", ButtonVariant.Danger, 140);
        _btnDelete.Click += (_, _) => DeleteSelectedEntry();
        left.Controls.AddRange([_btnOpenFolder, _btnDelete]);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 8, 0, 0),
        };
        var btnClose = MakeButton("关闭", ButtonVariant.Ghost, 90);
        btnClose.Click += (_, _) => Close();
        right.Controls.Add(btnClose);

        grid.Controls.Add(left, 0, 0);
        grid.Controls.Add(right, 1, 0);
        return grid;
    }

    private static ModernButton MakeButton(string text, ButtonVariant variant, int width) => new()
    {
        Text = text,
        Variant = variant,
        AutoSize = true,
        MinimumSize = new Size(width, 36),
        Height = 36,
        Margin = new Padding(0, 0, 8, 0),
    };

    /*********
    ** 数据
    *********/
    private ConflictGroup? SelectedGroup =>
        _groupList.SelectedItems.Count > 0 ? _groupList.SelectedItems[0].Tag as ConflictGroup : null;

    private ModEntry? SelectedEntry =>
        _entryList.SelectedItems.Count > 0 ? _entryList.SelectedItems[0].Tag as ModEntry : null;

    /// <summary>应用筛选文字重建左侧组列表。</summary>
    private void RefreshGroups(string filter)
    {
        _visibleGroups = [
            .. _groups.Where(g =>
                filter.Length == 0
                || g.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || g.Entries.Any(e => e.Mod.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                || g.Entries.Any(e => (e.Mod.UniqueId ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)))
        ];

        _groupList.BeginUpdate();
        try
        {
            _groupList.Items.Clear();
            foreach (var group in _visibleGroups)
            {
                var lvi = new ListViewItem(group.Title) { Tag = group };
                lvi.SubItems.Add(group.Entries.Count.ToString());
                _groupList.Items.Add(lvi);
            }
        }
        finally
        {
            _groupList.EndUpdate();
        }

        _countPill.Text = _visibleGroups.Count == _groups.Count
            ? $"{_groups.Count} 组冲突"
            : $"{_visibleGroups.Count} / {_groups.Count} 组";
        _countPill.PillColor = _groups.Count == 0 ? Theme.Success : Theme.Warn;

        SyncEntries();
    }

    private void SyncEntries()
    {
        _entryList.BeginUpdate();
        try
        {
            _entryList.Items.Clear();

            var group = SelectedGroup;
            if (group is { })
            {
                foreach (var entry in group.Entries)
                {
                    var lvi = new ListViewItem(entry.Mod.Name) { Tag = entry.Mod };
                    lvi.SubItems.Add(entry.Detail.Length == 0 ? entry.Mod.VersionText : entry.Detail);
                    lvi.SubItems.Add(entry.Mod.RelativeFolder);
                    _entryList.Items.Add(lvi);
                }
                _advice.Text = $"💡 {group.Advice}";
                _advice.ForeColor = Theme.Text;
            }
            else
            {
                _advice.Text = _groups.Count == 0
                    ? "没发现冲突：启用的 mod 里没有重复 ID、没有争夺同一份数据。"
                    : "选中左侧一组冲突看参与者和处理建议。";
                _advice.ForeColor = _groups.Count == 0 ? Theme.Success : Theme.TextMuted;
            }
        }
        finally
        {
            _entryList.EndUpdate();
        }

        SyncActionButtons();
    }

    /// <summary>右侧选择变化后的按钮状态（开文件夹 / 删除都跟条目走）。</summary>
    private void SyncActionButtons()
    {
        bool has = SelectedEntry is not null;
        _btnOpenFolder.Enabled = has;
        _btnDelete.Enabled = has;
    }

    /// <summary>把选中的冲突 mod 整个移进回收站（不永久删；组里只剩一份时该组自动消失）。</summary>
    private void DeleteSelectedEntry()
    {
        if (SelectedEntry is not { } entry)
        {
            Msg.Show(this, "先在右侧选一个参与者。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，Mod 文件可能被占用。\n请先完全退出游戏再删除。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = Msg.Show(this,
            $"把「{entry.Name}」的整个文件夹移进回收站吗？需要时从回收站可还原。\n\n{entry.Folder}",
            "删除参与者", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        try
        {
            BackupManager.RecycleDirectory(entry.Folder);
            ChangedSomething = true;

            // 从报告里摘掉；组里只剩一份就不再算冲突
            foreach (var group in _groups)
                group.Entries.RemoveAll(e => string.Equals(e.Mod.Folder, entry.Folder, StringComparison.OrdinalIgnoreCase));
            for (int i = _groups.Count - 1; i >= 0; i--)
                if (_groups[i].Entries.Count < 2)
                    _groups.RemoveAt(i);

            RefreshGroups(_filterField.Box.Text);
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"删除失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSelectedFolder()
    {
        if (SelectedEntry is not { } entry)
            return;

        try
        {
            if (Directory.Exists(entry.Folder))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{entry.Folder}\"");
            else
                Msg.Show(this, $"目录已不存在：{entry.Folder}", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"打开文件夹失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
