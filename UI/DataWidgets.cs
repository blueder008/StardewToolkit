using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace StardewToolkit.UI;

/*********
** 列表
*********/

/// <summary>暗色自绘 ListView（Details 视图）：暗色表头、条纹行、选中高亮、可选状态圆点。</summary>
internal sealed class ThemedListView : ListView
{
    private int _hoverIndex = -1;
    private bool _headerHeightApplied;
    private Font? _headerFont;

    /// <summary>第一列前画一个状态圆点（颜色取 Item.ForeColor）。</summary>
    public bool ShowStatusDot { get; set; } = true;

    /// <summary>鼠标悬停高亮（超大列表建议关掉，避免频繁重绘）。</summary>
    public bool EnableHover { get; set; } = true;

    /// <summary>
    /// 第一列自绘勾选框。OwnerDraw 模式下系统复选框不会绘制，所以自己画。
    /// 打开后第一列文字会自动右移让位。
    /// </summary>
    public bool DrawCheckBoxes { get; set; }

    public int RowHeight { get; set; } = 30;
    public int HeaderHeight { get; set; } = 34;

    public ThemedListView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        MultiSelect = true;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        SmallImageList = new ImageList { ImageSize = new Size(1, RowHeight) };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkScrollbars(Handle);
        ApplyDarkListBackground();
        ApplyHeaderHeight();
    }

    /// <summary>
    /// 显式设置 ListView 的背景色 / 文字背景色（LVM_SETBKCOLOR / LVM_SETTEXTBKCOLOR）。
    /// 列没铺满可视宽度（窗口窄、出现横向滚动）时，最后一列右边露出的区域
    /// 用的就是这两个颜色——WinForms 不把 BackColor 映射过去，默认是白的。
    /// </summary>
    private void ApplyDarkListBackground()
    {
        try
        {
            int bk = ColorRef(Theme.Surface);
            SendMessage(Handle, LvmSetBkColor, IntPtr.Zero, (IntPtr)bk);
            SendMessage(Handle, LvmSetTextBkColor, IntPtr.Zero, (IntPtr)ColorRef(Theme.Surface));
        }
        catch
        {
            // 失败只是可能露出默认色，不影响功能
        }
    }

    /// <summary>WinForms 颜色 → COLORREF（0x00BBGGRR）。</summary>
    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        // 最后一列没铺满时右侧会露出系统默认的白底/灰底。
        // 客户区变化（出现滚动条、DPI 调整）不触发 Resize，这里补上通知。
        LastColumnStretchChanged?.Invoke();
    }

    /// <summary>宿主在 ClientSize 变化时收到的通知（用来重拉最后一列宽）。</summary>
    public event Action? LastColumnStretchChanged;

    /*********
    ** 表头拖拽联动
    *********/
    private const int WmNotify = 0x004E;
    private const int HdnEndTrackW = -327;      // HDN_FIRST - 27：用户拖分隔线结束
    private const int HdnDividerDblClickW = -326; // HDN_FIRST - 26：双击分隔线（自动调宽）

    /// <summary>防重入：宿主响应通知去改列宽时又会产生 HDN 通知，不能跟着再响应。</summary>
    private bool _suppressHeaderNotify;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // 用户拖表头分隔线结束（或双击分隔线自动调宽）后，
        // 最后一列右侧可能多出一段没有列覆盖的空档（表头露白、正文露底）。
        // 拖动过程中不抢（否则用户拖备注列时宽度被补位逻辑吃掉，永远拉不动），
        // 松手那一刻再让宿主补位。
        if (m.Msg == WmNotify && !_suppressHeaderNotify)
        {
            int code = Marshal.ReadInt32(m.LParam, 2 * IntPtr.Size);
            if (code is HdnEndTrackW or HdnDividerDblClickW && LastColumnStretchChanged is not null)
            {
                _suppressHeaderNotify = true;
                try
                {
                    LastColumnStretchChanged?.Invoke();
                }
                finally
                {
                    _suppressHeaderNotify = false;
                }
            }
        }
    }

    private void ApplyHeaderHeight()
    {
        if (_headerHeightApplied || !IsHandleCreated)
            return;
        try
        {
            IntPtr header = SendMessage(Handle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero)
                return;

            // 列头控件本身也要套暗色主题，否则列没铺满时右端露系统亮色底
            try { SetWindowTheme(header, "DarkMode_Explorer", null); } catch { /* 老系统忽略 */ }

            // 注意：HDI_HEIGHT 与 HDI_WIDTH 同为 0x0001，在 HDM_SETITEMW 上下文里
            // cxy 会被解释成「列宽」，所以绝不能用它设置表头高度（那会把第 0 列压扁）。
            // 表头高度只能靠字体撑：字号只做小幅提升（最多到 11pt）。
            // 不能按 HeaderHeight 等比放大 —— 系统会用该字号来测量列标题宽度，
            // 12pt 以上就会把「已装版本」这类四字表头挤成「已装版…」。
            float points = HeaderHeight <= 34f
                ? 9f
                : MathF.Min(11f, 9f + (HeaderHeight - 34f) * 0.05f);
            _headerFont = new Font(Theme.UiFontName, points, FontStyle.Bold);
            SendMessage(header, WmSetFont, _headerFont.ToHfont(), IntPtr.Zero);
            _headerHeightApplied = true;
        }
        catch
        {
            // 调整表头高度失败不影响使用
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!EnableHover)
            return;
        var item = GetItemAt(e.X, e.Y);
        int index = item?.Index ?? -1;
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var brush = new SolidBrush(Theme.BgSoft))
            g.FillRectangle(brush, e.Bounds);
        using (var line = new Pen(Theme.Border))
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        if (e.ColumnIndex > 0)
        {
            using var separator = new Pen(Color.FromArgb(20, 255, 255, 255));
            g.DrawLine(separator, e.Bounds.Left, e.Bounds.Top + 9, e.Bounds.Left, e.Bounds.Bottom - 9);
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var textRect = Rectangle.Inflate(e.Bounds, -12, 0);
        flags |= e.Header.TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };

        using var font = Theme.Ui(8.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, e.Header.Text, font, textRect, Theme.TextMuted, flags);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // 背景统一在第 0 列的 DrawSubItem 里画，这样不受事件触发顺序影响
        e.DrawDefault = false;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        var item = e.Item;
        var rowRect = item.Bounds;

        if (e.ColumnIndex == 0)
        {
            Color background = Theme.Surface;
            if (item.Selected)
                background = Theme.Blend(Theme.Surface, Theme.Accent, 0.18);
            else if (EnableHover && item.Index == _hoverIndex)
                background = Theme.SurfaceHover;
            else if (item.Index % 2 == 1)
                background = Color.FromArgb(17, 23, 30);

            using (var brush = new SolidBrush(background))
                g.FillRectangle(brush, rowRect);

            if (item.Selected)
            {
                using var accent = new SolidBrush(Theme.Accent);
                g.FillRectangle(accent, rowRect.Left, rowRect.Top + 3, 3, rowRect.Height - 6);
            }
        }

        var textRect = e.Bounds;
        textRect = new Rectangle(textRect.Left + 12, textRect.Top, Math.Max(0, textRect.Width - 16), textRect.Height);

        Color fore = item.ForeColor.IsEmpty ? Theme.Text : item.ForeColor;
        if (item.Selected)
            fore = Theme.Text;
        if (!Enabled)
            fore = Theme.TextDim;

        // 第一列自绘勾选框（OwnerDraw 下系统复选框不显示）
        if (e.ColumnIndex == 0 && DrawCheckBoxes)
        {
            var boxRect = new Rectangle(e.Bounds.Left + 12, e.Bounds.Top + (e.Bounds.Height - 16) / 2, 16, 16);
            if (item.Checked)
            {
                Theme.FillRounded(g, boxRect, 4, Theme.Accent);
                using var tick = new Pen(Theme.AccentText, 2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                };
                g.DrawLines(tick, new[]
                {
                    new Point(boxRect.Left + 4, boxRect.Top + 8),
                    new Point(boxRect.Left + 7, boxRect.Top + 11),
                    new Point(boxRect.Left + 12, boxRect.Top + 5),
                });
            }
            else
            {
                Theme.DrawRounded(g, boxRect, 4, Theme.SurfaceRaised, Theme.BorderStrong);
            }

            textRect = new Rectangle(e.Bounds.Left + 38, e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 42), e.Bounds.Height);
        }

        if (e.ColumnIndex == 0 && ShowStatusDot && !item.Selected && !DrawCheckBoxes)
        {
            int dotX = e.Bounds.Left + 7;
            int dotY = e.Bounds.Top + e.Bounds.Height / 2 - 3;
            using var dot = new SolidBrush(fore);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(dot, dotX, dotY, 6, 6);
            g.SmoothingMode = SmoothingMode.Default;
            textRect = new Rectangle(e.Bounds.Left + 19, e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 23), e.Bounds.Height);
        }

        using var font = e.ColumnIndex == 0 ? Theme.Ui(9f, FontStyle.Bold) : Theme.Ui(9f);
        TextRenderer.DrawText(g, e.SubItem.Text, font, textRect, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    /*********
    ** P/Invoke（表头加高 / 暗色背景）
    *********/
    private const int LvmGetHeader = 0x101F;
    private const int LvmSetBkColor = 0x1025;
    private const int LvmSetTextBkColor = 0x1026;
    private const int WmSetFont = 0x0030;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}

/*********
** 下拉框
*********/
internal sealed class ModernComboBox : ComboBox
{
    public ModernComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9f);
        ItemHeight = 26;
        DropDownHeight = 280;
        IntegralHeight = false;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using (var brush = new SolidBrush(selected ? Theme.Blend(Theme.SurfaceRaised, Theme.Accent, 0.22) : Theme.SurfaceRaised))
            e.Graphics.FillRectangle(brush, e.Bounds);

        Color fore = selected ? Theme.Text : Theme.TextMuted;
        var text = GetItemText(Items[e.Index]);
        TextRenderer.DrawText(e.Graphics, text, Font,
            Rectangle.Inflate(e.Bounds, -8, 0), fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }
}

/*********
** 只读文本卡片（详情 / 日志）
*********/
internal sealed class RichTextCard : CardPanel
{
    private readonly Label _title;
    private readonly FlowLayoutPanel _actions;

    public RichTextBox Box { get; }

    public RichTextCard(string title, bool wordWrap = true)
    {
        Padding = new Padding(0);
        FillColor = Theme.Surface;
        StrokeColor = Theme.Border;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Theme.Surface,
        };
        _title = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Ui(9f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Location = new Point(16, 11),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(_title);

        _actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 7, 10, 0),
        };
        header.Controls.Add(_actions);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(10, 0, 10, 10),
        };
        Box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = wordWrap,
            DetectUrls = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.LogBg,
            ForeColor = Theme.Text,
            Font = Theme.Mono(9f),
        };
        body.Controls.Add(Box);

        Controls.Add(body);
        Controls.Add(header);
    }

    public ModernButton AddAction(string text, Action onClick, ButtonVariant variant = ButtonVariant.Ghost)
    {
        var button = new ModernButton
        {
            Text = text,
            Variant = variant,
            AutoSize = true,
            MinimumSize = new Size(0, 26),
            Margin = new Padding(6, 0, 0, 0),
            Font = Theme.Ui(8.5f),
        };
        button.Click += (_, _) => onClick();
        _actions.Controls.Add(button);
        return button;
    }

    public void SetTitle(string title) => _title.Text = title;
}

/*********
** 日志卡片
*********/
internal sealed class LogPanel : CardPanel
{
    private readonly Label _title;
    private readonly ModernButton _btnCopy;
    private readonly ModernButton _btnClear;

    public RichTextBox Box { get; }

    public LogPanel(string title = "运行日志", int headerHeight = 40)
    {
        Padding = new Padding(0);
        FillColor = Theme.Surface;
        StrokeColor = Theme.Border;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = headerHeight,
            BackColor = Theme.Surface,
        };

        _title = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.Ui(9f, FontStyle.Bold),
            ForeColor = Theme.Text,
            Location = new Point(16, 12),
            BackColor = Color.Transparent,
        };
        header.Controls.Add(_title);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 150,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 7, 10, 0),
        };
        _btnClear = new ModernButton
        {
            Text = "清空",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 26,
            Margin = new Padding(4, 0, 0, 0),
            Font = Theme.Ui(8.5f),
        };
        _btnClear.Click += (_, _) => Box.Clear();
        _btnCopy = new ModernButton
        {
            Text = "复制",
            Variant = ButtonVariant.Ghost,
            AutoSize = true,
            Height = 26,
            Margin = new Padding(4, 0, 0, 0),
            Font = Theme.Ui(8.5f),
        };
        _btnCopy.Click += (_, _) => CopyAll();
        actions.Controls.Add(_btnClear);
        actions.Controls.Add(_btnCopy);
        header.Controls.Add(actions);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Padding = new Padding(10, 0, 10, 10),
        };
        Box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            DetectUrls = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.LogBg,
            ForeColor = Theme.Text,
            Font = Theme.Mono(9f),
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        body.Controls.Add(Box);

        Controls.Add(body);
        Controls.Add(header);
    }

    /// <summary>写一行日志：时间戳弱化，消息按 ✅/❌/⚠ 自动上色。</summary>
    public void Write(string message)
    {
        if (Box.IsDisposed)
            return;

        Color tone;
        if (message.StartsWith("❌") || message.StartsWith("🗑"))
            tone = Theme.Danger;
        else if (message.StartsWith("✅"))
            tone = Theme.Success;
        else if (message.StartsWith("⚠") || message.StartsWith("🔴"))
            tone = Theme.Warn;
        else if (message.StartsWith("——") || message.StartsWith("—"))
            tone = Theme.TextMuted;
        else if (message.StartsWith("💡") || message.StartsWith("·"))
            tone = Theme.Info;
        else
            tone = Theme.Text;

        Box.SelectionStart = Box.TextLength;
        Box.SelectionLength = 0;
        Box.SelectionColor = Theme.TextDim;
        Box.AppendText($"[{DateTime.Now:HH:mm:ss}] ");

        Box.SelectionStart = Box.TextLength;
        Box.SelectionColor = tone;
        Box.AppendText(message + Environment.NewLine);

        Box.SelectionStart = Box.TextLength;
        Box.SelectionLength = 0;
        Box.ScrollToCaret();
    }

    public void SetTitle(string title) => _title.Text = title;

    private void CopyAll()
    {
        try
        {
            if (Box.TextLength > 0)
                Clipboard.SetText(Box.Text);
        }
        catch
        {
            // 剪贴板偶尔被占用
        }
    }
}
