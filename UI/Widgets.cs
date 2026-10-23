using System.Drawing.Drawing2D;

namespace StardewToolkit.UI;

/*********
** 卡片 / 头图
*********/

/// <summary>带圆角和细描边的卡片容器。子容器请把 BackColor 设成 FillColor，圆角才不会被方角盖住。</summary>
internal class CardPanel : Panel
{
    public int CornerRadius { get; set; } = 12;
    public Color FillColor { get; set; } = Theme.Surface;
    public Color StrokeColor { get; set; } = Theme.Border;
    public int StrokeWidth { get; set; } = 1;

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Padding = new Padding(16, 14, 16, 14);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.DrawRounded(e.Graphics, rect, CornerRadius, FillColor, StrokeColor, StrokeWidth);
        base.OnPaint(e);
    }
}

/// <summary>顶部 hero 区：深色渐变 + 淡网格 + 绿色辉光，纯绘制不占额外控件。</summary>
internal sealed class HeroPanel : Panel
{
    public HeroPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = ClientRectangle;
        g.Clear(Theme.Bg);

        using (var brush = new LinearGradientBrush(
                   new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                   Color.FromArgb(24, 36, 32),
                   Theme.Bg,
                   22f))
        {
            g.FillRectangle(brush, rect);
        }

        // 低对比网格，科技感但不抢内容
        using (var pen = new Pen(Color.FromArgb(12, 255, 255, 255)))
        {
            const int step = 30;
            for (int x = step; x < Width; x += step)
                g.DrawLine(pen, x, 0, x, Height);
            for (int y = step; y < Height; y += step)
                g.DrawLine(pen, 0, y, Width, y);
        }

        // 右上角绿色辉光
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int i = 3; i >= 1; i--)
        {
            int size = 190 + i * 70;
            using var glow = new SolidBrush(Color.FromArgb(7 + (3 - i) * 3, Theme.Accent));
            g.FillEllipse(glow, Width - size / 2 - 70, -size / 2 + 24, size, size);
        }

        using (var line = new Pen(Theme.Border))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>应用 Logo：渐变圆角方块 + 字母。</summary>
internal sealed class LogoBadge : Control
{
    public int CornerRadius { get; set; } = 14;
    public string Letter { get; set; } = "S";

    public LogoBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                 | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(52, 52);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1, Width - 3, Height - 3);
        using (var path = Theme.RoundedRect(rect, CornerRadius))
        {
            using var brush = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                Color.FromArgb(110, 231, 168),
                Color.FromArgb(16, 185, 129),
                45f);
            g.FillPath(brush, path);
            using var inner = new Pen(Color.FromArgb(60, 255, 255, 255));
            g.DrawPath(inner, path);
        }

        using var font = new Font("Segoe UI", Math.Max(12f, Width * 0.5f), FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, Letter, font, rect, Theme.AccentText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>小圆角标签（版本号、连接状态等）。</summary>
internal sealed class Pill : Control
{
    private Color _pillColor = Theme.Accent;

    public Color PillColor
    {
        get => _pillColor;
        set { _pillColor = value; Invalidate(); }
    }

    public Pill(string text, Color? color = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Text = text;
        if (color is { } c)
            _pillColor = c;
        Font = Theme.Ui(8.5f);
        BackColor = Color.Transparent;
        Height = 24;
        UpdateWidth();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        UpdateWidth();
        Invalidate();
    }

    private void UpdateWidth()
    {
        Width = Math.Max(28, TextRenderer.MeasureText(Text, Font).Width + 22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRounded(g, rect, Height / 2, Theme.Blend(Theme.Bg, _pillColor, 0.18));
        using (var pen = new Pen(Theme.Blend(Theme.Bg, _pillColor, 0.45)))
        using (var path = Theme.RoundedRect(rect, Height / 2))
            g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, rect, _pillColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/*********
** 按钮
*********/
internal enum ButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger,
}

/// <summary>自绘扁平按钮：圆角、hover/pressed 状态、主次层级。</summary>
internal sealed class ModernButton : Button
{
    private bool _hover;
    private bool _pressed;

    public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;
    public int CornerRadius { get; set; } = 8;
    public string? Glyph { get; set; }
    public int GlyphSize { get; set; } = 10;

    public ModernButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Font = Theme.Ui(9f);
        Height = 34;
        Cursor = Cursors.Hand;
        TabStop = true;
        ForeColor = Theme.Text;
        BackColor = Theme.SurfaceRaised;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font);
        int glyph = string.IsNullOrEmpty(Glyph) ? 0 : 20;
        int width = text.Width + glyph + 30;
        int height = Math.Max(34, text.Height + 16);
        return new Size(Math.Max(MinimumSize.Width, width), Math.Max(MinimumSize.Height, height));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    private Color Backdrop()
    {
        for (Control? parent = Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is CardPanel card)
                return card.FillColor;
            if (parent is Form)
                break;
            if (parent.BackColor.A == 255)
                return parent.BackColor;
        }
        return Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color fill, stroke, text;
        if (!Enabled)
        {
            fill = Variant == ButtonVariant.Ghost ? Backdrop() : Theme.Blend(Theme.Bg, Theme.SurfaceRaised, 0.55);
            stroke = Theme.Border;
            text = Theme.TextDim;
        }
        else
        {
            switch (Variant)
            {
                case ButtonVariant.Primary:
                    fill = _pressed
                        ? Theme.Blend(Theme.Accent, Color.White, 0.22)
                        : _hover ? Theme.Blend(Theme.Accent, Color.White, 0.12) : Theme.Accent;
                    stroke = Color.Transparent;
                    text = Theme.AccentText;
                    break;

                case ButtonVariant.Danger:
                    fill = _pressed || _hover ? Theme.Blend(Theme.Bg, Theme.Danger, 0.22) : Theme.SurfaceRaised;
                    stroke = _hover ? Theme.Danger : Theme.Blend(Theme.Border, Theme.Danger, 0.35);
                    text = Theme.Danger;
                    break;

                case ButtonVariant.Ghost:
                    fill = _pressed ? Theme.SurfaceHover : _hover ? Theme.SurfaceRaised : Backdrop();
                    stroke = Color.Transparent;
                    text = _hover ? Theme.Text : Theme.TextMuted;
                    break;

                default:
                    fill = _pressed || _hover ? Theme.SurfaceHover : Theme.SurfaceRaised;
                    stroke = _hover ? Theme.BorderStrong : Theme.Border;
                    text = Theme.Text;
                    break;
            }
        }

        var rect = new Rectangle(0, 0, Width, Height);
        if (fill.A > 0)
            Theme.FillRounded(g, rect, CornerRadius, fill);
        if (stroke.A > 0)
        {
            using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
            using var pen = new Pen(stroke);
            g.DrawPath(pen, path);
        }

        // 图标 + 文字整体居中
        string glyphText = string.IsNullOrEmpty(Glyph) ? "" : Glyph!;
        int glyphWidth = 0;
        if (glyphText.Length > 0)
        {
            using var iconFont = Theme.IconFont(GlyphSize);
            var glyphSize = TextRenderer.MeasureText(glyphText, iconFont);
            glyphWidth = glyphSize.Width + 8;
            int glyphX = (Width - (TextRenderer.MeasureText(Text, Font).Width + glyphWidth)) / 2;
            TextRenderer.DrawText(g, glyphText, iconFont,
                new Rectangle(glyphX, 0, glyphSize.Width + 4, Height), text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.Left);
        }

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(glyphWidth, 0, Width - glyphWidth, Height), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (Focused && ShowFocusCues)
        {
            var focus = new Rectangle(3, 3, Width - 7, Height - 7);
            using var path = Theme.RoundedRect(focus, Math.Max(2, CornerRadius - 2));
            using var pen = new Pen(Theme.Alpha(text, 120)) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, path);
        }
    }
}

/*********
** 输入 / 勾选
*********/

/// <summary>带圆角外框的输入框，内部是原生 TextBox（保留输入法/右键菜单/密码框行为）。</summary>
internal sealed class TextField : Panel
{
    private bool _focused;
    private string? _leadingGlyph;

    public TextBox Box { get; }

    /// <summary>左侧图标字形（如搜索放大镜）。为空则不占位。</summary>
    public string? LeadingGlyph
    {
        get => _leadingGlyph;
        set
        {
            _leadingGlyph = value;
            // 对象初始化器在构造之后才赋值，这里重新摆一次内部 TextBox
            LayoutBox();
            Invalidate();
        }
    }

    public int LeadingGlyphSize { get; set; } = 10;

    public TextField(string placeholder = "", bool password = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
        Height = 36;
        Cursor = Cursors.IBeam;

        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            Font = Theme.Ui(9.5f),
            PlaceholderText = placeholder,
            UseSystemPasswordChar = password,
        };
        Box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        Box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Box.TextChanged += (_, _) => Invalidate();
        Controls.Add(Box);
        Click += (_, _) => Box.Focus();
    }

    private int LeadingWidth => string.IsNullOrEmpty(_leadingGlyph) ? 0 : 28;

    private void LayoutBox()
    {
        if (Box is null)
            return;
        int x = 12 + LeadingWidth;
        int y = Math.Max(0, (Height - Box.PreferredHeight) / 2);
        Box.SetBounds(x, y, Math.Max(20, Width - x - 12), Box.PreferredHeight);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutBox();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.DrawRounded(g, rect, 8, Theme.SurfaceRaised,
            _focused ? Theme.Accent : Theme.Border);

        base.OnPaint(e);

        // 左侧图标画在 base.OnPaint 之后，否则会被子控件重绘盖掉
        if (!string.IsNullOrEmpty(_leadingGlyph))
        {
            using var iconFont = Theme.IconFont(LeadingGlyphSize);
            TextRenderer.DrawText(g, _leadingGlyph, iconFont,
                new Rectangle(12, 0, 16, Height),
                _focused ? Theme.TextMuted : Theme.TextDim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.Left);
        }
    }
}

/// <summary>自绘勾选框：暗底 + 绿色选中态。</summary>
internal sealed class ModernCheckBox : CheckBox
{
    public ModernCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        AutoSize = true;
        Font = Theme.Ui(9f);
        ForeColor = Theme.TextMuted;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Height = 24;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        // 勾选框占 1..17，文字从 24 起，右侧再留 6px 余量。
        // 按控件实际字体测量，避免高 DPI 下文字被挤掉。
        var text = TextRenderer.MeasureText(Text, Font);
        return new Size(24 + text.Width + 6, Math.Max(24, text.Height + 8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var box = new Rectangle(1, (Height - 16) / 2, 16, 16);
        if (Checked)
        {
            Theme.FillRounded(g, box, 4, Theme.Accent);
            using var pen = new Pen(Theme.AccentText, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLines(pen, new[]
            {
                new Point(box.Left + 4, box.Top + 8),
                new Point(box.Left + 7, box.Top + 11),
                new Point(box.Left + 12, box.Top + 5),
            });
        }
        else
        {
            Theme.DrawRounded(g, box, 4, Theme.SurfaceRaised, Theme.BorderStrong);
        }

        var textColor = Enabled ? (Checked ? Theme.Text : Theme.TextMuted) : Theme.TextDim;
        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(24, 0, Math.Max(0, Width - 24), Height), textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.Left);
    }
}

/*********
** 状态列表
*********/

/// <summary>键值状态行：左侧标签，右侧带色点的值。比一大段只读文本干净得多。</summary>
internal sealed class StatusList : Control
{
    public readonly record struct Row(string Label, string Value, Color Tone);

    private readonly List<Row> _rows = [];

    public int RowHeight { get; set; } = 30;
    public int LabelWidth { get; set; } = 88;

    public StatusList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.Ui(9f);
        Height = RowHeight;
    }

    public void SetRows(IEnumerable<Row> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
        int wanted = Math.Max(RowHeight, _rows.Count * RowHeight);
        if (Height != wanted)
            Height = wanted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var labelFont = Theme.Ui(8.5f);
        using var valueFont = Theme.Ui(9f);

        int y = 0;
        foreach (var row in _rows)
        {
            var rect = new Rectangle(0, y, Width, RowHeight);

            TextRenderer.DrawText(g, row.Label, labelFont,
                new Rectangle(rect.Left, rect.Top, LabelWidth, rect.Height), Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            int dotX = LabelWidth + 2;
            int dotY = rect.Top + rect.Height / 2 - 3;
            using (var dot = new SolidBrush(row.Tone))
                g.FillEllipse(dot, dotX, dotY, 6, 6);

            TextRenderer.DrawText(g, row.Value, valueFont,
                new Rectangle(dotX + 14, rect.Top, Math.Max(0, Width - dotX - 14), rect.Height), row.Tone,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            if (y > 0)
            {
                using var line = new Pen(Color.FromArgb(18, 255, 255, 255));
                g.DrawLine(line, 0, rect.Top, Width, rect.Top);
            }
            y += RowHeight;
        }
    }
}

/*********
** 空状态
*********/
internal sealed class EmptyHint : Control
{
    public EmptyHint()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                 | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Font = Theme.Ui(9f);
    }

    public string Hint { get; set; } = "";

    protected override void OnPaint(PaintEventArgs e)
    {
        TextRenderer.DrawText(e.Graphics, Hint, Font, ClientRectangle,
            Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}
