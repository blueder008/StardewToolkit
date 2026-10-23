using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace StardewToolkit.UI;

/// <summary>
/// 替代系统 MessageBox 的暗色对话框：同样的调用习惯（图标/按钮/默认按钮），但外观跟主界面一致。
/// </summary>
internal static class Msg
{
    public static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
        => Show(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
    {
        using var dialog = new AppDialogForm(text, caption, buttons, icon, defaultButton);
        return owner is Form form && !form.IsDisposed
            ? dialog.ShowDialog(form)
            : dialog.ShowDialog();
    }
}

internal sealed class AppDialogForm : Form
{
    private readonly string _message;
    private readonly string _caption;
    private readonly MessageBoxButtons _buttons;
    private readonly MessageBoxIcon _icon;
    private readonly MessageBoxDefaultButton _defaultButton;
    private Point _dragOffset;
    private bool _dragging;

    public AppDialogForm(string message, string caption, MessageBoxButtons buttons,
        MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
    {
        _message = message;
        _caption = caption;
        _buttons = buttons;
        _icon = icon;
        _defaultButton = defaultButton;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        DoubleBuffered = true;
        Padding = new Padding(1);

        BuildUi();
        ApplyRoundedCorners();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
        catch
        {
            // 老系统忽略
        }
    }

    private void BuildUi()
    {
        const int pad = 24;
        const int iconSize = 42;
        const int maxTextWidth = 430;

        var title = new Label
        {
            Text = string.IsNullOrWhiteSpace(_caption) ? "提示" : _caption,
            Font = Theme.Ui(10.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(pad, 18),
        };
        Controls.Add(title);

        var close = new ModernButton
        {
            Text = "✕",
            Variant = ButtonVariant.Ghost,
            CornerRadius = 6,
            Size = new Size(30, 30),
            Location = new Point(Width, 0), // 下面重新定位
            Font = Theme.Ui(9f),
        };
        close.Click += (_, _) => Close();
        Controls.Add(close);

        var badge = new DialogIconBadge(_icon) { Size = new Size(iconSize, iconSize) };
        Controls.Add(badge);

        var text = new Label
        {
            Text = _message.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            Font = Theme.Ui(9f),
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            MaximumSize = new Size(maxTextWidth, 0),
            Location = new Point(pad + iconSize + 16, 52),
        };
        Controls.Add(text);

        var textSize = text.PreferredSize;
        int width = Math.Max(430, Math.Min(600, textSize.Width + pad + iconSize + 16 + pad));
        int messageBottom = text.Top + textSize.Height;

        var buttonRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Height = 40,
            BackColor = Theme.Bg,
            Location = new Point(pad, messageBottom + 22),
        };
        var buttonSpecs = BuildButtonSpecs();
        foreach (var spec in buttonSpecs)
        {
            var button = new ModernButton
            {
                Text = spec.Text,
                Variant = spec.Primary ? ButtonVariant.Primary : ButtonVariant.Secondary,
                AutoSize = true,
                MinimumSize = new Size(92, 36),
                Margin = new Padding(8, 0, 0, 0),
            };
            var result = spec.Result;
            button.Click += (_, _) =>
            {
                DialogResult = result;
                Close();
            };
            buttonRow.Controls.Add(button);
            buttonRow.Width = Math.Max(buttonRow.Width, (int)buttonRow.PreferredSize.Width);
        }

        buttonRow.Width = Math.Max(1, buttonRow.PreferredSize.Width);
        buttonRow.Height = 44;
        buttonRow.Location = new Point(width - pad - buttonRow.Width, messageBottom + 20);
        Controls.Add(buttonRow);

        // 默认按钮 = 接受 Enter 的按钮
        int defaultIndex = _defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            _ => 0,
        };
        if (defaultIndex < buttonRow.Controls.Count)
            AcceptButton = (IButtonControl)buttonRow.Controls[defaultIndex];
        else if (buttonRow.Controls.Count > 0)
            AcceptButton = (IButtonControl)buttonRow.Controls[0];

        // Esc：取消类按钮优先
        var cancelButton = buttonRow.Controls.OfType<ModernButton>().LastOrDefault();
        if (cancelButton is not null && _buttons != MessageBoxButtons.OK)
            CancelButton = cancelButton;

        close.Location = new Point(width - 46, 12);

        ClientSize = new Size(width, messageBottom + 20 + 44 + pad);
        badge.Location = new Point(pad, 50);
        title.Location = new Point(pad, 16);
    }

    private sealed record ButtonSpec(string Text, DialogResult Result, bool Primary);

    private List<ButtonSpec> BuildButtonSpecs() => _buttons switch
    {
        MessageBoxButtons.OK =>
        [
            new("确定", DialogResult.OK, true),
        ],
        MessageBoxButtons.OKCancel =>
        [
            new("确定", DialogResult.OK, true),
            new("取消", DialogResult.Cancel, false),
        ],
        MessageBoxButtons.YesNo =>
        [
            new("是", DialogResult.Yes, true),
            new("否", DialogResult.No, false),
        ],
        MessageBoxButtons.YesNoCancel =>
        [
            new("是", DialogResult.Yes, true),
            new("否", DialogResult.No, false),
            new("取消", DialogResult.Cancel, false),
        ],
        _ =>
        [
            new("确定", DialogResult.OK, true),
        ],
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var pen = new Pen(Theme.BorderStrong);
        e.Graphics.DrawPath(pen, path);

        // 顶部一条绿色细光带
        using var accent = new Pen(Color.FromArgb(90, Theme.Accent));
        e.Graphics.DrawLine(accent, 14, 0, Width - 14, 0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && e.Y < 52)
        {
            _dragging = true;
            _dragOffset = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var screen = PointToScreen(e.Location);
            Location = new Point(screen.X - _dragOffset.X, screen.Y - _dragOffset.Y);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    private void ApplyRoundedCorners()
    {
        try
        {
            using var path = Theme.RoundedRect(new Rectangle(0, 0, Width, Height), 12);
            Region = new Region(path);
        }
        catch
        {
            // 忽略
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>对话框左侧的彩色图标徽章。</summary>
internal sealed class DialogIconBadge : Control
{
    private readonly MessageBoxIcon _icon;

    public DialogIconBadge(MessageBoxIcon icon)
    {
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color tint;
        string glyph;
        switch (_icon)
        {
            case MessageBoxIcon.Error:
                tint = Theme.Danger;
                glyph = "✕";
                break;
            case MessageBoxIcon.Warning:
                tint = Theme.Warn;
                glyph = "!";
                break;
            case MessageBoxIcon.Question:
                tint = Theme.Info;
                glyph = "?";
                break;
            case MessageBoxIcon.Information:
                tint = Theme.Info;
                glyph = "i";
                break;
            default:
                tint = Theme.TextMuted;
                glyph = "i";
                break;
        }

        var circle = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var brush = new SolidBrush(Theme.Blend(Theme.Bg, tint, 0.20)))
            g.FillEllipse(brush, circle);
        using (var pen = new Pen(Theme.Blend(Theme.Bg, tint, 0.55)))
            g.DrawEllipse(pen, circle);

        using var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, glyph, font, circle, tint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
