namespace StardewToolkit.UI;

/// <summary>
/// 编辑某个 mod 的备注。单行输入 + 说明，风格与其他对话框一致。
/// 备注以 mod 的 UniqueID 为键存进 AppConfig。
/// </summary>
internal sealed class NoteDialog : Form
{
    private readonly TextField _field;

    /// <summary>用户确认后的备注内容（空白表示清除）。</summary>
    public string NoteText => _field.Box.Text.Trim();

    public NoteDialog(string modName, string? currentNote)
    {
        Theme.StyleForm(this);
        Text = "编辑备注";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(520, 226);
        Padding = new Padding(24, 20, 24, 18);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Bg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 标题
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 说明
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // 输入框
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 按钮

        root.Controls.Add(new Label
        {
            Text = modName,
            Font = Theme.Ui(10.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = "给这个 mod 写点什么，方便以后想起来它是干什么用的。留空则清除备注。",
            Font = Theme.Ui(8.5f),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        }, 0, 1);

        _field = new TextField("例如：关掉自动收菜 / 主存档用 / 待排查")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 0, 0, 14),
        };
        _field.Box.Text = currentNote ?? "";
        _field.Box.MaxLength = 200;
        root.Controls.Add(_field, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
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
        Shown += (_, _) =>
        {
            _field.Box.Focus();
            _field.Box.SelectAll();
        };
    }
}
