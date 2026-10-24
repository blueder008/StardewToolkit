using System.Drawing.Drawing2D;

namespace StardewToolkit.UI;

/// <summary>网格视图里的一张卡片数据（ModEntry 的展示投影）。</summary>
public sealed class ModGridItem
{
    public required string Name { get; init; }
    public string? Note { get; init; }
    public long? NexusModId { get; init; }      // 有才能拉封面
    public bool Enabled { get; set; }
    public bool HasUpdate { get; init; }
    public bool HasProblem { get; init; }
    public string? ProblemText { get; init; }   // 悬停提示
    public string Version { get; init; } = "";
    public string? LatestVersion { get; init; }
}

/// <summary>
/// 自绘 mod 网格（ScrollableControl）：
///   - 封面来自 CoverCache，没有 → 名字首字占位块；
///   - 状态边框：绿 = 可更新，红 = 有问题，禁用 = 整卡降透明；
///   - 右下角复选框 = 启用/禁用（点击切换，触发 CheckedChanged）；
///   - 名称 / 备注显示在封面下方。
/// </summary>
internal sealed class ModGrid : ScrollableControl
{
    public sealed class Cell
    {
        public required ModGridItem Item { get; init; }
        public bool Checked;
        public Image? Cover;          // 异步加载完成后由外部回填并 Invalidate
        public bool CoverTried;       // 防止每帧重试加载
    }

    public event EventHandler<ModGridItem>? ItemCheckedChanged;

    private const int TileW = 176;
    private const int TileH = 176;
    private const int CoverH = 104;
    private const int GapX = 14;
    private const int GapY = 14;
    private const int PadX = 14;
    private const int PadY = 12;

    private readonly List<Cell> _cells = [];
    private readonly ToolTip _tip = new();
    private int _hover = -1;
    private int _pressedCheck = -1;

    public ModGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
        AutoScroll = true;
    }

    /// <summary>重建网格数据。coverLoader：异步封面加载回调（null 则全部占位块）。</summary>
    public void SetItems(IEnumerable<ModGridItem> items, Action<ModGridItem, Cell>? coverLoader = null)
    {
        _cells.Clear();
        foreach (var it in items)
        {
            var cell = new Cell { Item = it, Checked = it.Enabled };
            _cells.Add(cell);
            coverLoader?.Invoke(it, cell);
        }
        UpdateScroll();
        Invalidate();
    }

    /// <summary>外部（异步封面加载完成）回填一张卡片封面并重绘那一格。</summary>
    public void SetCover(ModGridItem item, Image? cover)
    {
        var cell = _cells.FirstOrDefault(c => ReferenceEquals(c.Item, item));
        if (cell is null || cell.CoverTried)
            return;
        cell.Cover = cover;
        cell.CoverTried = true;
        Invalidate();
    }

    /// <summary>复选框统一读回（宿主同步「启用/禁用」逻辑用）。</summary>
    public IReadOnlyList<(ModGridItem Item, bool Checked)> GetCheckedItems() =>
        _cells.Select(c => (c.Item, c.Checked)).ToList();

    private int Columns => Math.Max(1, (ClientSize.Width - PadX * 2 + GapX) / (TileW + GapX));
    private int Rows => (int)Math.Ceiling(_cells.Count / (double)Columns);

    private void UpdateScroll()
    {
        int needed = Rows * (TileH + GapY) + PadY * 2;
        AutoScrollMinSize = new Size(0, Math.Max(0, needed));
    }

    private Rectangle TileRect(int index)
    {
        int col = index % Columns;
        int row = index / Columns;
        int scrollY = -AutoScrollPosition.Y;
        return new Rectangle(
            PadX + col * (TileW + GapX),
            PadY + row * (TileH + GapY) + scrollY,
            TileW,
            TileH);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (int i = 0; i < _cells.Count; i++)
        {
            var rect = TileRect(i);
            if (rect.Bottom < -20 || rect.Top > Height + 20)
                continue;
            DrawTile(g, rect, _cells[i], i == _hover);
        }
    }

    private void DrawTile(Graphics g, Rectangle rect, Cell cell, bool hover)
    {
        var item = cell.Item;
        var cardRect = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

        // 状态色
        Color accent = item.HasProblem ? Theme.Danger : item.HasUpdate ? Theme.Accent : Color.Transparent;

        // 卡片底 + 描边
        Theme.DrawRounded(g, cardRect, 10, Theme.BgSoft, hover ? Theme.BorderStrong : Theme.Border);

        // 状态色边框（画在描边内侧一圈）
        if (accent.A > 0)
        {
            using var pen = new Pen(Theme.Alpha(accent, 190), 2f);
            using var path = Theme.RoundedRect(Rectangle.Inflate(cardRect, -1, -1), 10);
            g.DrawPath(pen, path);
        }

        // 封面区
        var cover = new Rectangle(rect.X + 8, rect.Y + 8, rect.Width - 17, CoverH);
        if (cell.Cover is not null)
        {
            using var path = Theme.RoundedRect(cover, 7);
            var old = g.Clip;
            g.SetClip(path);
            // 裁切填充：封面横向图，按比例缩放到覆盖后居中裁掉上下
            float scale = Math.Max(cover.Width / (float)cell.Cover.Width, cover.Height / (float)cell.Cover.Height);
            int w = (int)(cell.Cover.Width * scale + 0.5f);
            int h = (int)(cell.Cover.Height * scale + 0.5f);
            var dst = new Rectangle(cover.X - (w - cover.Width) / 2, cover.Y - (h - cover.Height) / 2, w, h);
            g.DrawImage(cell.Cover, dst);
            g.Clip = old;
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }
        else
        {
            // 占位块：名字首字
            Theme.FillRounded(g, cover, 7, Theme.SurfaceRaised);
            using (var pen = new Pen(Theme.Border))
            using (var path = Theme.RoundedRect(cover, 7))
                g.DrawPath(pen, path);

            string initial = item.Name.Length > 0 ? item.Name[..1].ToUpperInvariant() : "?";
            using var big = new Font(Theme.UiFontName, 26f, FontStyle.Bold);
            TextRenderer.DrawText(g, initial, big, cover, Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // 名称（封面下方，最多两行）
        var nameRect = new Rectangle(rect.X + 10, cover.Bottom + 5, rect.Width - 20, 30);
        TextRenderer.DrawText(g, item.Name, Theme.Ui(8.5f, FontStyle.Bold), nameRect, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

        // 备注（有才显示，一行）
        if (!string.IsNullOrWhiteSpace(item.Note))
        {
            var noteRect = new Rectangle(rect.X + 10, nameRect.Bottom + 1, rect.Width - 20, 15);
            TextRenderer.DrawText(g, item.Note, Theme.Ui(7.5f), noteRect, Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        // 右下角复选框（启用 = 勾选）
        var box = new Rectangle(rect.Right - 26, rect.Bottom - 26, 16, 16);
        if (cell.Checked)
        {
            Theme.FillRounded(g, box, 4, Theme.Accent);
            using var tick = new Pen(Theme.AccentText, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLines(tick, new[]
            {
                new Point(box.Left + 4, box.Top + 8),
                new Point(box.Left + 7, box.Top + 11),
                new Point(box.Left + 12, box.Top + 5),
            });
        }
        else
        {
            Theme.DrawRounded(g, box, 4,
                hover ? Theme.SurfaceHover : Theme.SurfaceRaised, Theme.BorderStrong);
        }

        // 禁用整卡压暗（画完内容后盖一层半透明底色）
        if (!item.Enabled)
        {
            using var dim = new SolidBrush(Color.FromArgb(110, Theme.Bg));
            using var path = Theme.RoundedRect(cardRect, 10);
            g.FillPath(dim, path);
        }
    }

    /*********
    ** 交互
    *********/
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            var item = hit >= 0 ? _cells[hit].Item : null;
            _tip.SetToolTip(this, item?.ProblemText ?? "");
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1)
        {
            _hover = -1;
            _tip.SetToolTip(this, "");
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressedCheck = HitCheckBox(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int hit = HitCheckBox(e.Location);
        if (hit >= 0 && hit == _pressedCheck)
        {
            var cell = _cells[hit];
            cell.Checked = !cell.Checked;
            cell.Item.Enabled = cell.Checked;
            ItemCheckedChanged?.Invoke(this, cell.Item);
        }
        _pressedCheck = -1;
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _cells.Count; i++)
            if (TileRect(i).Contains(p))
                return i;
        return -1;
    }

    private int HitCheckBox(Point p)
    {
        int i = HitTest(p);
        if (i < 0)
            return -1;
        var rect = TileRect(i);
        var box = new Rectangle(rect.Right - 34, rect.Bottom - 34, 34, 34);
        return box.Contains(p) ? i : -1;
    }
}
