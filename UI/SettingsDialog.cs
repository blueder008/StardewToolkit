using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// 设置窗口：游戏目录 + Nexus 密钥。
/// 游戏位置的自动检测/手动指定都移到这里（主界面不再显示路径）。
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppConfig _config;
    private readonly Action _pickManually;
    private readonly TextField _pathField;
    private readonly ModernButton _btnNexus;

    public SettingsDialog(AppConfig config, string? currentTarget, Action pickManually)
    {
        _config = config;
        _pickManually = pickManually;

        Theme.StyleForm(this);
        Text = "设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(660, 330);
        Padding = new Padding(24, 20, 24, 18);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Bg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 游戏目录
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // Nexus
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 说明
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 按钮

        // ---- 游戏目录 ----
        var gameCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 14),
            Margin = new Padding(0, 0, 0, 12),
        };
        var gameInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
        };
        gameInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gameInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        gameInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        gameInner.Controls.Add(new Label
        {
            Text = "游戏目录",
            Font = Theme.Ui(10f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        }, 0, 0);

        _pathField = new TextField("尚未选择游戏目录")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 0, 0, 10),
        };
        _pathField.Box.Text = currentTarget ?? "";
        _pathField.Box.ReadOnly = true;
        gameInner.Controls.Add(_pathField, 0, 1);

        var pathButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        var btnDetect = new ModernButton
        {
            Text = "自动检测",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(104, 34),
            Margin = new Padding(0, 0, 8, 0),
        };
        btnDetect.Click += (_, _) =>
        {
            var found = SteamLocator.FindInstalls();
            if (found.Count == 0)
            {
                Msg.Show(this, "没检测到 Steam 库里的星露谷。可以用「手动选择目录…」指定。",
                    "没找到游戏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var first = found[0];
            _pathField.Box.Text = first.GamePath;
            _config.LastGamePath = first.GamePath;
        };

        var btnPick = new ModernButton
        {
            Text = "手动选择目录…",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            MinimumSize = new Size(126, 34),
            Margin = new Padding(0),
        };
        btnPick.Click += (_, _) =>
        {
            _pickManually();
            if (!string.IsNullOrWhiteSpace(_config.LastGamePath))
                _pathField.Box.Text = _config.LastGamePath;
        };

        pathButtons.Controls.Add(btnDetect);
        pathButtons.Controls.Add(btnPick);
        gameInner.Controls.Add(pathButtons, 0, 2);

        gameCard.Controls.Add(gameInner);
        root.Controls.Add(gameCard, 0, 0);

        // ---- Nexus 密钥 ----
        var nexusCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 14),
            Margin = new Padding(0, 0, 0, 12),
        };
        var nexusInner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
        };
        nexusInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nexusInner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var nexusText = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        nexusText.Controls.Add(new Label
        {
            Text = "Nexus 账号",
            Font = Theme.Ui(10f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);

        var nexusState = _config.NexusApiKey is null
            ? "未配置 · Premium 账号可全自动下载 mod"
            : $"已连接{_config.NexusUserName}";
        nexusText.Controls.Add(new Label
        {
            Text = nexusState,
            Font = Theme.Ui(8.5f),
            ForeColor = _config.NexusApiKey is null ? Theme.TextMuted : Theme.Accent,
            AutoSize = true,
            Margin = new Padding(0),
        }, 0, 1);
        nexusInner.Controls.Add(nexusText, 0, 0);

        _btnNexus = new ModernButton
        {
            Text = "配置密钥…",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(112, 36),
            Margin = new Padding(12, 0, 0, 0),
        };
        _btnNexus.Click += (_, _) =>
        {
            using var dialog = new NexusKeyDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _config.NexusApiKey = dialog.ApiKey;
                _config.NexusUserName = dialog.UserName;
                _config.Save();
                _btnNexus.Text = "已配置 ✓";
            }
        };
        nexusInner.Controls.Add(_btnNexus, 1, 0);

        nexusCard.Controls.Add(nexusInner);
        root.Controls.Add(nexusCard, 0, 1);

        root.Controls.Add(new Label
        {
            Text = "提示：游戏目录和 Nexus 密钥都存在本机配置文件里，不会上传到任何地方。",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Margin = new Padding(2, 4, 0, 0),
        }, 0, 2);

        // ---- 底部按钮 ----
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 10, 0, 0),
        };
        var save = new ModernButton
        {
            Text = "保存",
            Variant = ButtonVariant.Primary,
            AutoSize = true,
            MinimumSize = new Size(96, 38),
            Margin = new Padding(8, 0, 0, 0),
        };
        save.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = new ModernButton
        {
            Text = "取消",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(96, 38),
            Margin = new Padding(0),
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 3);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
