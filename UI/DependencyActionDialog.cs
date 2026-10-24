namespace StardewToolkit.UI;

/// <summary>
/// 「缺少依赖」处理选择框：列出每个缺失依赖的处境（已装被禁 / 可下载），
/// 用户输入序号选择处理动作，直接确定则什么都不做。
/// </summary>
internal sealed class DependencyActionDialog : Form
{
    private readonly int _actionCount;

    /// <summary>用户选择的动作序号（0 基，对应传入的选项顺序）；-1 = 不处理。</summary>
    public int ChosenIndex => int.TryParse(_inputBox.Text.Trim(), out var n) && n >= 1 && n <= _actionCount ? n - 1 : -1;

    private readonly TextBox _inputBox;

    public DependencyActionDialog(string[] lines, int actionCount)
    {
        _actionCount = actionCount;

        Theme.StyleForm(this);
        Text = "缺少依赖";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 160 + lines.Length * 22);
        Padding = new Padding(24, 20, 24, 18);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Theme.Bg,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 正文
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 输入行
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 按钮

        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = Theme.Ui(9f),
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, lines),
        };
        root.Controls.Add(text, 0, 0);

        var inputRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 10, 0, 0),
        };
        inputRow.Controls.Add(new Label
        {
            Text = "输入序号：",
            AutoSize = true,
            ForeColor = Theme.Text,
            Font = Theme.Ui(9f),
            Margin = new Padding(0, 8, 6, 0),
        });
        _inputBox = new TextBox
        {
            Width = 60,
            Font = Theme.Ui(9.5f),
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
        };
        inputRow.Controls.Add(_inputBox);
        root.Controls.Add(inputRow, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        var ok = new ModernButton
        {
            Text = "执行",
            Variant = ButtonVariant.Primary,
            AutoSize = true,
            MinimumSize = new Size(96, 38),
            Margin = new Padding(8, 0, 0, 0),
        };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = new ModernButton
        {
            Text = "关闭",
            Variant = ButtonVariant.Secondary,
            AutoSize = true,
            MinimumSize = new Size(96, 38),
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) => _inputBox.Focus();
    }
}
