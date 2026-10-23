using StardewToolkit.Services;

namespace StardewToolkit.UI;

/// <summary>
/// Nexus 配置小窗：personal API key（粘贴 → 验证 → 保存）+ nxm:// 浏览器联动注册。
/// 官方 key 页：账户设置里的「API 访问」标签。
/// </summary>
public sealed class NexusKeyDialog : Form
{
    private readonly TextField _keyField;
    private readonly Label _status;
    private readonly ModernButton _btnValidate;
    private readonly ModernButton _btnSave;
    private readonly Label _nxmStatus;
    private readonly ModernButton _btnNxmToggle;

    private string? _validatedName;
    private bool _validatedPremium;

    public NexusKeyDialog()
    {
        Theme.StyleForm(this);
        Text = "Nexus 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 548);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(20, 16, 20, 14),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));   // 0: 标题
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));    // 1: 间距
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 224));  // 2: API key
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));    // 3: 间距
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 146));  // 4: nxm
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 5: 弹性
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // 6: 底部按钮

        root.Controls.Add(BuildHeader(), 0, 0);

        var keyCard = BuildKeyCard(out _keyField, out _status, out _btnValidate);
        root.Controls.Add(keyCard, 0, 2);

        var nxmCard = BuildNxmCard(out _btnNxmToggle, out _nxmStatus);
        root.Controls.Add(nxmCard, 0, 4);

        root.Controls.Add(BuildFooter(out _btnSave), 0, 6);
        Controls.Add(root);

        AcceptButton = _btnSave;

        Shown += (_, _) => _keyField.Box.Focus();
        RefreshNxmStatus();
    }

    /*********
    ** 布局
    *********/
    private Control BuildHeader()
    {
        var head = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        head.Controls.Add(new LogoBadge
        {
            Letter = "N",
            Location = new Point(0, 6),
            Size = new Size(48, 48),
        });
        head.Controls.Add(new Label
        {
            Text = "Nexus 设置",
            Font = Theme.Ui(15f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(62, 2),
        });
        head.Controls.Add(new Label
        {
            Text = "personal API key · 浏览器 nxm:// 下载联动",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(64, 32),
        });
        return head;
    }

    private Control BuildKeyCard(out TextField keyField, out Label status, out ModernButton validate)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 12),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        inner.Controls.Add(new Label
        {
            Text = "① personal API key",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(10f, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 0);

        inner.Controls.Add(new Label
        {
            Text = "登录 nexusmods.com → 头像 → 账户设置 → 「API 访问」→ Personal Key 点 Display 复制。\n免费账号也能生成；Key 只保存在本机配置文件中，不会上传。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 1);

        var link = new LinkLabel
        {
            Text = "打开「API 访问」页面（自动选好标签）",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            LinkColor = Theme.Info,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.Purple,
            LinkBehavior = LinkBehavior.HoverUnderline,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        link.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://www.nexusmods.com/users/myaccount?tab=api+access",
                UseShellExecute = true,
            });
        inner.Controls.Add(link, 0, 2);

        keyField = new TextField("粘贴你的 personal API key…", password: true)
        {
            Dock = DockStyle.Fill,
            Height = 38,
            Margin = new Padding(0, 4, 0, 0),
        };
        inner.Controls.Add(keyField, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0),
        };
        validate = new ModernButton
        {
            Text = "验证连接",
            Variant = ButtonVariant.Primary,
            AutoSize = true,
            MinimumSize = new Size(112, 36),
            Height = 36,
            Margin = new Padding(0),
        };
        validate.Click += async (_, _) => await ValidateAsync();
        buttons.Controls.Add(validate);

        var saveHint = new Label
        {
            Text = "验证不是必须的，但能确认 key 有没有失效。",
            AutoSize = true,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextDim,
            Margin = new Padding(12, 11, 0, 0),
        };
        buttons.Controls.Add(saveHint);
        inner.Controls.Add(buttons, 0, 4);

        status = new Label
        {
            Text = "先粘贴 key 再点「验证连接」确认无误。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0),
        };
        inner.Controls.Add(status, 0, 5);

        card.Controls.Add(inner);
        return card;
    }

    private Control BuildNxmCard(out ModernButton toggle, out Label status)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 14, 18, 12),
        };

        var inner = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        inner.Controls.Add(new Label
        {
            Text = "② 浏览器联动（nxm://）",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(10f, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 0);

        inner.Controls.Add(new Label
        {
            Text = "把本工具注册为 nxm:// 链接处理器后，在 Nexus 文件页点「Mod Manager Download」会自动回传到这里下载安装。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 1);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        toggle = new ModernButton
        {
            Text = "设为本工具处理 nxm:// 链接",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(0, 36),
            Height = 36,
            Margin = new Padding(0, 6, 14, 0),
        };
        toggle.Click += (_, _) => ToggleNxm();
        row.Controls.Add(toggle, 0, 0);

        status = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        row.Controls.Add(status, 1, 0);
        inner.Controls.Add(row, 0, 2);

        card.Controls.Add(inner);
        return card;
    }

    private Control BuildFooter(out ModernButton save)
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

        grid.Controls.Add(new Label
        {
            Text = "Key 只保存在本机，可随时在这里更换。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = Theme.Ui(8f),
            ForeColor = Theme.TextDim,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        }, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(0, 4, 0, 0),
        };

        save = new ModernButton
        {
            Text = "保存",
            Variant = ButtonVariant.Primary,
            AutoSize = true,
            MinimumSize = new Size(104, 38),
            Height = 38,
            Margin = new Padding(10, 0, 0, 0),
        };
        save.Click += (_, _) => SaveKey();
        buttons.Controls.Add(save);

        var cancel = new ModernButton
        {
            Text = "取消",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            MinimumSize = new Size(92, 38),
            Height = 38,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(0),
        };
        buttons.Controls.Add(cancel);
        CancelButton = cancel;

        grid.Controls.Add(buttons, 1, 0);
        return grid;
    }

    /*********
    ** 验证 / 保存
    *********/
    private async Task ValidateAsync()
    {
        var key = _keyField.Box.Text.Trim();
        if (key.Length == 0)
        {
            _status.Text = "先粘贴 API key。";
            _status.ForeColor = Theme.Danger;
            return;
        }

        _btnValidate.Enabled = false;
        try
        {
            _status.Text = "验证中…";
            _status.ForeColor = Theme.TextMuted;
            var user = await new NexusClient(key).ValidateAsync();
            _validatedName = string.IsNullOrWhiteSpace(user.Name) ? $"用户 #{user.UserId}" : user.Name;
            _validatedPremium = user.IsPremium;
            _status.Text = $"✔ 连接成功：{_validatedName}（{(user.IsPremium ? "Premium，全自动下载可用" : "免费会员，支持自动检查 + 手动下载兜底")}）";
            _status.ForeColor = Theme.Success;
        }
        catch (NexusApiException ex)
        {
            _validatedName = null;
            _status.Text = ex.Message;
            _status.ForeColor = Theme.Danger;
        }
        catch (Exception ex)
        {
            _validatedName = null;
            _status.Text = $"网络错误：{ex.Message}";
            _status.ForeColor = Theme.Danger;
        }
        finally
        {
            _btnValidate.Enabled = true;
        }
    }

    private void SaveKey()
    {
        var key = _keyField.Box.Text.Trim();
        if (key.Length == 0)
        {
            _status.Text = "先粘贴 API key。";
            _status.ForeColor = Theme.Danger;
            return;
        }

        var config = AppConfig.Load();
        config.NexusApiKey = key;
        if (_validatedName is not null && config.NexusApiKey == key)
        {
            config.NexusUserName = _validatedName;
            if (!_validatedPremium)
                Msg.Show(this,
                    "你的 Nexus 账号不是 Premium。\n\n程序能自动**检查** Nexus 上的新版本；下载走两条路：\n" +
                    "・浏览器点「Mod Manager Download」自动回传安装（推荐，免费账号可用）\n" +
                    "・或程序打开官方文件页，你下载 zip 后用「安装 zip…」导入",
                    "已保存（免费会员模式）", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            config.NexusUserName = null;
        }
        config.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    /*********
    ** nxm 注册
    *********/
    private void ToggleNxm()
    {
        if (NxmProtocolRegistrar.IsRegisteredForThisApp())
        {
            NxmProtocolRegistrar.Unregister();
            Msg.Show(this,
                "已撤销注册。之后点「Mod Manager Download」浏览器会问你要用哪个程序打开（或交给其他管理器）。",
                "已撤销", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            if (Environment.ProcessPath is { Length: > 0 } exePath)
                NxmProtocolRegistrar.Register(exePath);
        }
        RefreshNxmStatus();
    }

    private void RefreshNxmStatus()
    {
        if (NxmProtocolRegistrar.IsRegisteredForThisApp())
        {
            _btnNxmToggle.Text = "撤销注册（不用本工具接管）";
            _nxmStatus.Text = "已注册 ✓ 浏览器下载自动回到本工具";
            _nxmStatus.ForeColor = Theme.Success;
            return;
        }

        var owner = NxmProtocolRegistrar.GetNxmClaimOwner();
        if (owner is not null)
        {
            _btnNxmToggle.Text = $"改由本工具接管（覆盖 {owner}）";
            _nxmStatus.Text = $"当前由「{owner}」的注册接管浏览器下载（比如 Stardrop）";
            _nxmStatus.ForeColor = Theme.Danger;
            return;
        }

        _btnNxmToggle.Text = "设为本工具处理 nxm:// 链接";
        _nxmStatus.Text = "未注册——点下载时浏览器会默认弹「选择应用」对话框";
        _nxmStatus.ForeColor = Theme.TextMuted;
    }
}
