using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// 备份管理窗口：列出 Mods/.backup 下的全部快照（版本替换时自动写入的旧版存档），
/// 支持把某个旧版 mod 恢复回 Mods 目录（回滚 / 并存两种策略）、删除单份快照、清空全部备份。
/// </summary>
internal sealed class BackupManagerForm : Form
{
    /*********
    ** 状态
    *********/
    private readonly string _modsDir;
    private List<BackupSnapshot> _snapshots = [];
    private BackupSnapshot? _selectedSnapshot;
    private bool _busy;

    private ModernButton _btnRefresh = null!;
    private ModernButton _btnRestore = null!;
    private ModernButton _btnRestoreAll = null!;
    private ModernButton _btnOpenFolder = null!;
    private ModernButton _btnDeleteSnapshot = null!;
    private ModernButton _btnDeleteAll = null!;
    private Label _hint = null!;
    private Pill _pillSnapshots = null!;
    private Pill _pillSize = null!;
    private ThemedListView _snapshotList = null!;
    private ThemedListView _itemList = null!;
    private LogPanel _logPanel = null!;

    /// <summary>本次有没有恢复过任何东西；关闭后 Mod 管理器据此决定要不要重新扫描。</summary>
    public bool ChangedSomething { get; private set; }

    public BackupManagerForm(string modsDir)
    {
        _modsDir = modsDir;

        Theme.StyleForm(this);
        Text = "备份管理 — Mods/.backup";
        MinimumSize = new Size(980, 620);
        Size = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterParent;

        var hero = BuildHero();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(18, 14, 18, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));   // 0: 路径 + 统计
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 1: 双列表
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));   // 2: 操作
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));  // 3: 日志

        root.Controls.Add(BuildPathCard(), 0, 0);
        root.Controls.Add(BuildLists(), 0, 1);
        root.Controls.Add(BuildButtons(), 0, 2);
        _logPanel = new LogPanel("操作日志") { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        root.Controls.Add(_logPanel, 0, 3);

        Controls.Add(root);
        Controls.Add(hero);

        Shown += async (_, _) => await RefreshListAsync();
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
            Text = "备份管理",
            Font = Theme.Ui(16f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(88, 15),
        });
        hero.Controls.Add(new Label
        {
            Text = "Mods/.backup · 版本回滚 · 恢复或清理旧版 Mod",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(90, 48),
        });

        return hero;
    }

    private Control BuildPathCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 9, 16, 9),
            Margin = new Padding(0, 0, 0, 10),
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

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        left.Controls.Add(new Label
        {
            Text = $"备份位置：{BackupManager.BackupRoot(_modsDir)}",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Mono(8.5f),
            ForeColor = Theme.TextMuted,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 0);

        _hint = new Label
        {
            Text = "替换旧版本安装时自动产生；选中右侧条目后即可恢复。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextDim,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        left.Controls.Add(_hint, 0, 1);
        grid.Controls.Add(left, 0, 0);

        _pillSnapshots = new Pill("0 份快照", Theme.Info);
        _pillSize = new Pill("0 MB", Theme.Purple);
        var pills = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(12, 8, 0, 0),
        };
        pills.Controls.Add(_pillSnapshots);
        pills.Controls.Add(_pillSize);
        grid.Controls.Add(pills, 1, 0);

        card.Controls.Add(grid);
        return card;
    }

    /// <summary>左边快照列表 + 右边选中快照的内容清单。</summary>
    private Control BuildLists()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));

        _snapshotList = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = false,
            ShowStatusDot = false,
            RowHeight = 30,
            Margin = new Padding(0),
        };
        _snapshotList.Columns.AddRange(
        [
            new ColumnHeader { Text = "备份时间", Width = 160 },
            new ColumnHeader { Text = "条目", Width = 64, TextAlign = HorizontalAlignment.Right },
            new ColumnHeader { Text = "大小", Width = 84, TextAlign = HorizontalAlignment.Right },
        ]);
        _snapshotList.SelectedIndexChanged += (_, _) => SyncItems();
        StretchOnResize(_snapshotList);

        var leftCard = MakeListCard("快照", _snapshotList);
        leftCard.Margin = new Padding(0, 0, 6, 10);
        grid.Controls.Add(leftCard, 0, 0);

        _itemList = new ThemedListView
        {
            Dock = DockStyle.Fill,
            MultiSelect = true,
            ShowStatusDot = false,
            RowHeight = 30,
            Margin = new Padding(0),
        };
        _itemList.Columns.AddRange(
        [
            new ColumnHeader { Text = "Mod 名称", Width = 210 },
            new ColumnHeader { Text = "备份版本", Width = 92 },
            new ColumnHeader { Text = "文件夹名", Width = 190 },
            new ColumnHeader { Text = "大小", Width = 84, TextAlign = HorizontalAlignment.Right },
        ]);
        _itemList.SelectedIndexChanged += (_, _) => SyncActionButtons();
        _itemList.DoubleClick += async (_, _) => await RestoreSelectedAsync();
        StretchOnResize(_itemList);

        var rightCard = MakeListCard("内容清单", _itemList);
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

    /// <summary>最后一列吃掉剩余宽度（贴右不留白档）。</summary>
    private static void StretchOnResize(ThemedListView list)
    {
        void Stretch()
        {
            if (!list.IsHandleCreated || list.Columns.Count == 0)
                return;
            int others = 0;
            for (int i = 0; i < list.Columns.Count - 1; i++)
                others += list.Columns[i].Width;
            int stretch = list.ClientSize.Width - others;
            if (stretch >= 60)
                list.Columns[^1].Width = stretch;
        }
        list.Resize += (_, _) => Stretch();
        list.LastColumnStretchChanged += Stretch;
        list.HandleCreated += (_, _) => Stretch();
    }

    private Control BuildButtons()
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
        _btnRestore = MakeButton("恢复所选…", ButtonVariant.Primary, 134);
        _btnRestore.Click += async (_, _) => await RestoreSelectedAsync();
        _btnRestoreAll = MakeButton("恢复整份…", ButtonVariant.Secondary, 124);
        _btnRestoreAll.Click += async (_, _) => await RestoreSnapshotAsync();
        _btnDeleteSnapshot = MakeButton("删除这份…", ButtonVariant.Danger, 124);
        _btnDeleteSnapshot.Click += async (_, _) => await DeleteSelectedSnapshotAsync();
        _btnDeleteAll = MakeButton("清空全部…", ButtonVariant.Danger, 120);
        _btnDeleteAll.Click += async (_, _) => await DeleteAllAsync();
        left.Controls.AddRange([_btnRestore, _btnRestoreAll, _btnDeleteSnapshot, _btnDeleteAll]);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 8, 0, 0),
        };
        _btnRefresh = MakeButton("刷新", ButtonVariant.Ghost, 86);
        _btnRefresh.Click += async (_, _) => await RefreshListAsync();
        _btnOpenFolder = MakeButton("打开文件夹", ButtonVariant.Secondary, 112);
        _btnOpenFolder.Click += (_, _) => OpenSelectedFolder();
        right.Controls.Add(_btnRefresh);
        right.Controls.Add(_btnOpenFolder);

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
    private BackupSnapshot? SelectedSnapshot
    {
        get
        {
            if (_snapshotList.SelectedItems.Count == 0)
                return null;
            return _snapshotList.SelectedItems[0].Tag as BackupSnapshot;
        }
    }

    /// <summary>右侧列表选中的备份条目（所属快照必须也是当前选中的，防左列表切换后残留失效条目）。</summary>
    private (BackupSnapshot Snapshot, BackupItem Item)? SelectedItem
    {
        get
        {
            var snapshot = SelectedSnapshot;
            if (snapshot is null || _itemList.SelectedItems.Count == 0)
                return null;
            if (_itemList.SelectedItems[0].Tag is not BackupItem item)
                return null;
            return (snapshot, item);
        }
    }

    private async Task RefreshListAsync()
    {
        if (!Directory.Exists(_modsDir))
        {
            Log($"⚠ Mods 目录不存在：{_modsDir}");
            _hint.Text = "Mods 目录不存在，没有备份可看。";
            return;
        }

        SetBusy(true);
        try
        {
            _snapshots = await BackupManager.ListSnapshotsAsync(_modsDir);
            RefreshSnapshotList();

            if (_snapshots.Count == 0)
            {
                Log("还没有任何备份。用「安装 zip…」替换旧版 mod、或「自动更新」升级时，旧版会自动存进 Mods/.backup。");
                UpdateHint();
            }
            else
            {
                Log($"找到 {_snapshots.Count} 份快照，共 {_snapshots.Sum(s => s.SizeBytes) / 1024 / 1024.0:0.#} MB。");
            }
        }
        catch (Exception ex)
        {
            Log($"❌ 列举备份失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshSnapshotList()
    {
        _snapshotList.BeginUpdate();
        try
        {
            _snapshotList.Items.Clear();
            foreach (var snapshot in _snapshots.OrderByDescending(s => s.Time ?? DateTime.MinValue))
            {
                var lvi = new ListViewItem(snapshot.DisplayName)
                {
                    Tag = snapshot,
                };
                lvi.SubItems.Add(snapshot.Items.Count.ToString());
                lvi.SubItems.Add(snapshot.SizeText);
                _snapshotList.Items.Add(lvi);
            }
        }
        finally
        {
            _snapshotList.EndUpdate();
        }

        _pillSnapshots.Text = $"{_snapshots.Count} 份快照";
        long total = _snapshots.Sum(s => s.SizeBytes);
        _pillSize.Text = $"{total / 1024.0 / 1024.0:0.#} MB";

        SyncItems();
    }

    /// <summary>左列表换了快照 → 右侧填充这批内容清单。</summary>
    private void SyncItems()
    {
        _selectedSnapshot = null;
        _itemList.BeginUpdate();
        try
        {
            _itemList.Items.Clear();

            var snapshot = SelectedSnapshot;
            if (snapshot is { })
            {
                _selectedSnapshot = snapshot;
                foreach (var item in snapshot.Items.OrderBy(i => (i.ModName ?? i.Name), StringComparer.OrdinalIgnoreCase))
                {
                    var lvi = new ListViewItem(item.ModName ?? item.Name)
                    {
                        Tag = item,
                    };
                    lvi.SubItems.Add(item.ModVersion ?? "未知");
                    lvi.SubItems.Add(item.Name);
                    lvi.SubItems.Add(item.SizeText);
                    if (item.ModName is null)
                        lvi.ForeColor = Theme.TextDim;
                    _itemList.Items.Add(lvi);
                }
            }
        }
        finally
        {
            _itemList.EndUpdate();
        }

        UpdateHint();
        SyncActionButtons();
    }

    private void UpdateHint()
    {
        var snapshot = SelectedSnapshot;
        _hint.Text = snapshot is null
            ? "替换旧版本安装时自动产生；选中右侧条目后即可恢复。"
            : $"快照 {snapshot.RawName}：{snapshot.Items.Count} 个条目，{snapshot.SizeText}——选中右侧条目后「恢复所选…」就能撤回旧版";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _snapshotList.Enabled = !busy;
        _btnRefresh.Enabled = !busy;
        _btnRestoreAll.Enabled = !busy;
        _btnDeleteAll.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        SyncActionButtons();
    }

    private void SyncActionButtons()
    {
        bool snapshot = _selectedSnapshot is not null && !_busy;
        bool hasSelection = SelectedItem is not null;
        _btnRestore.Enabled = hasSelection && !_busy;
        _btnOpenFolder.Enabled = snapshot && !_busy;
        _btnDeleteSnapshot.Enabled = snapshot;
        _btnDeleteAll.Enabled = _snapshotList.Items.Count > 0;
    }

    /*********
    ** 恢复
    *********/
    private (BackupRestoreMode Mode, string Label)? AskRestoreMode(bool wholeSnapshot)
    {
        string subject = wholeSnapshot
            ? "这份快照里的 mod 恢复回 Mods 目录时，"
            : "这个备份的旧版恢复回 Mods 目录时，";
        var result = Msg.Show(this,
            $"{subject}如果和当前已装的版本冲突（同一个 mod），要怎么处理？\n\n" +
            "【是】替换已装版本（推荐：当前新版会自动再存一份进备份，随时可再换回）\n" +
            "【否】并存恢复（加编号后缀，SMAPI 里两个版本都存在，只会加载一个）\n" +
            "【取消】不恢复",
            "恢复方式", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        return result switch
        {
            DialogResult.Yes => (BackupRestoreMode.Replace, "替换已装版本"),
            DialogResult.No => (BackupRestoreMode.KeepBoth, "并存恢复"),
            _ => null,
        };
    }

    private async Task RestoreSelectedAsync()
    {
        var selection = SelectedItem;
        if (selection is null)
        {
            Msg.Show(this, "先在右上列表里选一个备份条目（左侧先选一份快照）。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var (snapshot, item) = selection.Value;

        // Mod 管理器的通用前置：游戏别开着
        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，Mod 文件可能被占用。\n请先完全退出游戏再恢复。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var mode = AskRestoreMode(wholeSnapshot: false);
        if (mode is not { } decision)
            return;

        SetBusy(true);
        try
        {
            Log($"—— 恢复 {item.Name}（{decision.Label}）——");
            var result = await BackupManager.RestoreItemAsync(_modsDir, item, decision.Mode, Log);
            if (result.Success)
            {
                ChangedSomething = true;
                Log($"✅ {result.Message}");

                // 条目被移出后刷新快照清单（时间戳目录里没东西了就只剩空壳，交给列表自然显示）
                _snapshots = await BackupManager.ListSnapshotsAsync(_modsDir);
                SelectSnapshot(snapshot.RawName);
            }
            else
            {
                Log($"❌ {result.Message}");
                Msg.Show(this, result.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RestoreSnapshotAsync()
    {
        var snapshot = SelectedSnapshot;
        if (snapshot is null)
        {
            Msg.Show(this, "先在左侧选一份快照。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (snapshot.Items.Count == 0)
        {
            Msg.Show(this, "这份快照是空的（里面没有 mod 条目），不用恢复。\n可以顺手把空快照删掉。", "空快照",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this, "游戏或 SMAPI 正在运行，Mod 文件可能被占用。\n请先完全退出游戏再恢复。", "游戏运行中",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var mode = AskRestoreMode(wholeSnapshot: true);
        if (mode is not { } decision)
            return;

        var confirm = Msg.Show(this,
            $"把快照 {snapshot.DisplayName} 里的 {snapshot.Items.Count} 个条目全部恢复（{decision.Label}）？",
            "确认恢复", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            Log($"—— 整份恢复 {snapshot.DisplayName}（{decision.Label}）——");
            var (restored, failed) = await BackupManager.RestoreSnapshotAsync(
                _modsDir, snapshot, decision.Mode, Log);

            ChangedSomething = true;
            Log(failed == 0
                ? $"✅ 整份恢复完成：{restored} 个条目全部恢复。"
                : $"整份恢复完成：{restored} 个成功，{failed} 个失败（详情见上）。");

            _snapshots = await BackupManager.ListSnapshotsAsync(_modsDir);
            SelectSnapshot(snapshot.RawName);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>恢复过一条之后刷新列表并重新选中原来的快照（保留用户上下文）。</summary>
    private void SelectSnapshot(string rawName)
    {
        RefreshSnapshotList();
        foreach (ListViewItem lvi in _snapshotList.Items)
        {
            if (lvi.Tag is BackupSnapshot s && s.RawName == rawName)
            {
                lvi.Selected = true;
                lvi.Focused = true;
                _snapshotList.EnsureVisible(lvi.Index);
                break;
            }
        }
    }

    /*********
    ** 删除
    *********/
    private async Task DeleteSelectedSnapshotAsync()
    {
        var snapshot = SelectedSnapshot;
        if (snapshot is null)
        {
            Msg.Show(this, "先在左侧选一份快照。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = Msg.Show(this,
            $"把这份快照移进回收站吗？里面的 {snapshot.Items.Count} 个备份条目（{snapshot.SizeText}）会整体进回收站，需要时可以从回收站还原：\n\n" +
            snapshot.Folder,
            "删除快照", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            BackupManager.DeleteSnapshot(_modsDir, snapshot);
            ChangedSomething = true;
            Log($"🗑 快照 {snapshot.RawName}（{snapshot.SizeText}）已移进回收站。");

            _snapshots = await BackupManager.ListSnapshotsAsync(_modsDir);
            RefreshSnapshotList();
        }
        catch (Exception ex)
        {
            Log($"❌ 删除失败：{ex.Message}");
            Msg.Show(this, $"删除失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DeleteAllAsync()
    {
        if (_snapshotList.Items.Count == 0)
            return;

        var confirm = Msg.Show(this,
            $"清空全部 {_snapshotList.Items.Count} 份快照？所有备份 mod 将整体移进回收站（每份都能在回收站单独还原）。\n\n" +
            "以后替换/更新 mod 时还会自动产生新的备份。",
            "确认清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
            return;

        SetBusy(true);
        try
        {
            int count = await Task.Run(() => BackupManager.DeleteAllBackups(_modsDir));
            ChangedSomething = true;
            Log($"🗑 已清空全部备份（{count} 份，已移进回收站）。");

            _snapshots = await BackupManager.ListSnapshotsAsync(_modsDir);
            RefreshSnapshotList();
        }
        catch (Exception ex)
        {
            Log($"❌ 清空失败：{ex.Message}");
            Msg.Show(this, $"清空失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenSelectedFolder()
    {
        var target = SelectedItem?.Item.SourceFolder ?? SelectedSnapshot?.Folder;
        if (target is null)
            return;

        try
        {
            if (Directory.Exists(target))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{target}\"");
            else
                Msg.Show(this, $"目录已不存在（可能恢复时被移走了）：{target}", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Msg.Show(this, $"打开文件夹失败：{ex.Message}", "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /*********
    ** 日志
    *********/
    private void Log(string message)
    {
        _logPanel.Write(message);
    }
}
