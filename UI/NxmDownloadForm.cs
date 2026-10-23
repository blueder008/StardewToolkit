using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// nxm:// 下载队列窗口（非模态常驻）：浏览器点「Mod Manager Download」进来的链接排在这处理，
/// 一个一个下载 → 自动按 UniqueID 替换旧版（备份同 zip 安装流程）。
/// </summary>
internal sealed class NxmDownloadForm : Form
{
    private readonly Queue<NxmLink> _queue = new();
    private readonly Label _status;
    private readonly Pill _queuePill;
    private readonly ModernButton _btnClose;
    private readonly LogPanel _logPanel;
    private bool _pumping;

    public NxmDownloadForm()
    {
        Theme.StyleForm(this);
        Text = "Nexus 下载队列";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(540, 400);
        Size = new Size(640, 480);

        var hero = BuildHero();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(16, 12, 16, 12),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));   // 0: 状态
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 1: 日志
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));   // 2: 底部

        var statusCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 8, 16, 8),
            Margin = new Padding(0, 0, 0, 8),
        };
        var statusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _status = new Label
        {
            Text = "等待下载链接……（在 Nexus 网站文件页点「Mod Manager Download」就会传进来）",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(9f),
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
        };
        statusGrid.Controls.Add(_status, 0, 0);

        _queuePill = new Pill("空闲", Theme.TextMuted) { Margin = new Padding(12, 8, 0, 0) };
        statusGrid.Controls.Add(_queuePill, 1, 0);
        statusCard.Controls.Add(statusGrid);
        root.Controls.Add(statusCard, 0, 0);

        _logPanel = new LogPanel("下载日志") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        root.Controls.Add(_logPanel, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        footer.Controls.Add(new Label
        {
            Text = "窗口可以隐藏或最小化，队列会在后台继续。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(2, 0, 0, 0),
        }, 0, 0);

        _btnClose = new ModernButton
        {
            Text = "隐藏窗口",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(104, 36),
            Height = 36,
            Margin = new Padding(8, 4, 0, 4),
        };
        _btnClose.Click += (_, _) => Hide(); // 只藏起来，进程退出时统一关闭
        footer.Controls.Add(_btnClose, 1, 0);
        root.Controls.Add(footer, 0, 2);

        Controls.Add(root);
        Controls.Add(hero);
    }

    private HeroPanel BuildHero()
    {
        var hero = new HeroPanel { Dock = DockStyle.Top, Height = 84 };

        hero.Controls.Add(new LogoBadge
        {
            Location = new Point(18, 17),
            Size = new Size(48, 48),
        });
        hero.Controls.Add(new Label
        {
            Text = "Nexus 下载队列",
            Font = Theme.Ui(15f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(78, 12),
        });
        hero.Controls.Add(new Label
        {
            Text = "来自浏览器的 nxm:// 链接 · 自动下载并按 UniqueID 替换旧版",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(80, 42),
        });
        return hero;
    }

    /// <summary>主实例收到新链接时排队。必须在 UI 线程调用。</summary>
    public void Enqueue(NxmLink link)
    {
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();

        Log($"▶ 收到下载链接：mod {link.ModId} 文件 {link.FileId}（{link.Domain}）");
        _queue.Enqueue(link);
        _queuePill.Text = $"队列 {_queue.Count}";
        _queuePill.PillColor = Theme.Info;
        SetStatus(_pumping ? $"下载中（队列还有 {_queue.Count} 个待处理）…" : "准备下载…", Theme.Info);

        if (!_pumping)
            _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (_queue.Count > 0)
            {
                var link = _queue.Dequeue();
                _queuePill.Text = $"队列 {_queue.Count}";
                try
                {
                    await ProcessJobAsync(link);
                }
                catch (Exception ex)
                {
                    Log($"❌ 失败：{ex.Message}");
                    SetStatus("上一个下载失败了，继续队列里剩下的。", Theme.Danger);
                }
            }
            SetStatus("空闲——回 Nexus 网站点下一个「Mod Manager Download」即可。", Theme.TextMuted);
            _queuePill.Text = "空闲";
            _queuePill.PillColor = Theme.TextMuted;
            Log("— 队列处理完毕 —");
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task ProcessJobAsync(NxmLink link)
    {
        // 0. 域名安全闸：只认星露谷
        if (!link.Domain.Equals("stardewvalley", StringComparison.OrdinalIgnoreCase))
        {
            Msg.Show(this,
                $"这个链接是游戏「{link.Domain}」的，本工具只处理星露谷（stardewvalley）的下载。为你跳过。",
                "跳过下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var config = AppConfig.Load(); // 每单重读：管理器里刚保存的 key 立刻生效

        // 1. 游戏目录（下载安装必须有落点）
        if (string.IsNullOrWhiteSpace(config.LastGamePath))
        {
            Msg.Show(this,
                "还没选过游戏目录，下载下来的 mod 没地方安。请先在主窗口选一次游戏目录（选 SMAPI 安装版会自动记住）。",
                "需要先选择游戏目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 2. 游戏运行中不能替换 mod 文件
        if (GameInspector.IsGameRunning())
        {
            Msg.Show(this,
                "游戏或 SMAPI 正在运行，不能安装更新。请先完全退出游戏，再回 Nexus 网站重新点一次「Mod Manager Download」。",
                "游戏运行中", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var modsDir = Path.Combine(config.LastGamePath, "Mods");
        Log($"游戏目录：{config.LastGamePath}");

        // 3. 若配了 personal key，顺手拿文件信息（名字/版本更好认；拿不到也不影响下载）
        NexusModFile? info = null;
        if (config.NexusApiKey is { Length: > 0 } apiKey)
        {
            try
            {
                info = await new NexusClient(apiKey).GetFileInfoAsync(link.ModId, link.FileId);
                Log($"文件信息：{info.FileName ?? info.Name}（版本 {info.ModVersion ?? info.Version ?? "?"}）");
            }
            catch (NexusApiException ex)
            {
                Log($"（拿不到文件信息：{ex.Message}——不影响下载）");
            }
            catch (Exception ex)
            {
                Log($"（网络抖了一下，拿文件信息失败：{ex.Message}——不影响下载）");
            }
        }

        // 4. 按 Nexus mod id 找本地已装的 mod，能比就比一版，已经是新版就别白下一趟
        var mods = await ModScanner.ScanAsync(modsDir);
        var installed = mods.FirstOrDefault(m => m.Manifest?.UpdateKeys?
            .Any(k => k.Trim().Equals($"Nexus:{link.ModId}", StringComparison.OrdinalIgnoreCase)) == true);
        if (installed?.Manifest is { Version: { } currentVersion }
            && info?.ModVersion is { } newVersion
            && !ModVersionUtil.IsNewer(currentVersion, newVersion))
        {
            Log($"本地已是 {currentVersion}（Nexus 主文件 {newVersion}），不用更新，跳过下载。");
            return;
        }
        if (installed is not null)
            Log($"本地已装：{installed.Name} {installed.VersionText}（{installed.FolderName}）");

        // 5. 下载：带 key/expires 走官方免费也放行的授权路径；链接没带 key 就要靠 personal key + Premium
        var client = new NexusClient(config.NexusApiKey ?? "");
        var destDir = Path.Combine(SmapiInstallerRunner.WorkspaceDirPath, "nxm-downloads");
        string zipPath;
        if (!string.IsNullOrWhiteSpace(link.Key))
        {
            zipPath = await client.DownloadNxmAsync(link, destDir, Log);
        }
        else if (config.NexusApiKey is { Length: > 0 })
        {
            zipPath = await client.DownloadAsync(link.ModId, destDir, Log);
        }
        else
        {
            Msg.Show(this,
                "这个下载链接没带临时授权，而本工具也没配置 Nexus API key。\n\n去 Nexus 配一个 personal key（点主窗口 Mod 管理器右上「Nexus 密钥…」），或者在 Nexus 网站上重新点「Mod Manager Download」。",
                "没法下载", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 6. 安装（备份/替换逻辑同 zip 安装）
        var report = await ModPackageInstaller.InstallFromZipAsync(zipPath, modsDir, Log);
        if (report.Success)
        {
            if (report.Results.Count == 1)
            {
                var result = report.Primary;
                Log($"✅ 完成：{result.ModName} {result.OldVersion ?? "首次安装"} → {result.NewVersion}");
            }
            else
            {
                Log($"✅ 完成：{report.Summary}");
            }
            Log("（在 Mod 管理器里能看到它的最新状态）");
        }
        else
        {
            Msg.Show(this,
                report.Results.Count > 0 ? report.Results[0].Message : report.Summary,
                "下载完成但安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string text, Color tone)
    {
        _status.Text = text;
        _status.ForeColor = tone;
    }

    private void Log(string line)
    {
        foreach (var part in line.Replace("\r\n", "\n").Split('\n'))
            _logPanel.Write(part);
    }
}
