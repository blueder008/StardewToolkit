using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace StardewToolkit.UI;

/// <summary>
/// 全局视觉规范：暗色科技风配色、字体、圆角绘制、窗口外观工具。
/// 所有窗口只从这里取颜色/字体，保证风格统一。
/// </summary>
internal static class Theme
{
    /*********
    ** 调色板
    *********/
    public static readonly Color Bg = Color.FromArgb(11, 15, 20);            // 窗口底色
    public static readonly Color BgSoft = Color.FromArgb(15, 20, 27);
    public static readonly Color Surface = Color.FromArgb(20, 27, 35);       // 卡片
    public static readonly Color SurfaceRaised = Color.FromArgb(26, 34, 45); // 输入框 / 按钮
    public static readonly Color SurfaceHover = Color.FromArgb(34, 45, 58);
    public static readonly Color LogBg = Color.FromArgb(7, 10, 14);
    public static readonly Color Border = Color.FromArgb(38, 48, 62);
    public static readonly Color BorderStrong = Color.FromArgb(57, 72, 90);

    public static readonly Color Text = Color.FromArgb(233, 239, 246);
    public static readonly Color TextMuted = Color.FromArgb(147, 162, 178);
    public static readonly Color TextDim = Color.FromArgb(101, 116, 133);

    public static readonly Color Accent = Color.FromArgb(74, 222, 128);      // 星露谷绿
    public static readonly Color AccentDeep = Color.FromArgb(16, 185, 129);
    public static readonly Color AccentSoft = Color.FromArgb(23, 64, 45);
    public static readonly Color AccentText = Color.FromArgb(5, 24, 15);

    public static readonly Color Info = Color.FromArgb(96, 165, 250);
    public static readonly Color Warn = Color.FromArgb(250, 190, 70);
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);
    public static readonly Color Success = Color.FromArgb(74, 222, 128);
    public static readonly Color Purple = Color.FromArgb(167, 139, 250);

    /*********
    ** 字体
    *********/
    public static readonly string UiFontName = PickFont("Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI");
    public static readonly string MonoFontName = PickFont("Cascadia Mono", "JetBrains Mono", "Consolas", "Courier New");
    public static readonly string IconFontName = PickFont("Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol", "Segoe UI");

    public static Font Ui(float size = 9f, FontStyle style = FontStyle.Regular) => new(UiFontName, size, style);
    public static Font Mono(float size = 9f, FontStyle style = FontStyle.Regular) => new(MonoFontName, size, style);
    public static Font Title(float size = 11f) => new(UiFontName, size, FontStyle.Bold);
    public static Font IconFont(float size = 10f) => new(IconFontName, size, FontStyle.Regular);

    private static string PickFont(params string[] candidates)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in candidates)
                if (names.Contains(name))
                    return name;
        }
        catch
        {
            // 字体枚举失败时退到系统默认，不影响功能
        }
        return candidates[^1];
    }

    /*********
    ** 颜色工具
    *********/
    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            (int)Math.Round(from.A + (to.A - from.A) * amount),
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    public static Color Alpha(Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color);

    /*********
    ** 圆角绘制
    *********/
    public static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Rectangle rect, int radius, Color fill)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        g.SmoothingMode = mode;
    }

    public static void DrawRounded(Graphics g, Rectangle rect, int radius, Color fill, Color border, float borderWidth = 1f)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(rect, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (border.A > 0)
        {
            using var pen = new Pen(border, borderWidth);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = mode;
    }

    /*********
    ** 窗口
    *********/
    public static void StyleForm(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = Text;
        form.Font = Ui(9f);
        form.Icon ??= AppIcon;
        form.HandleCreated += (_, _) => ApplyWindowChrome(form.Handle);
        if (form.IsHandleCreated)
            ApplyWindowChrome(form.Handle);
    }

    private static void ApplyWindowChrome(IntPtr handle)
    {
        // Win10 1809+ / Win11：让系统标题栏也跟着变暗
        int enabled = 1;
        try
        {
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int)); // 旧版本号
            int border = ColorTranslator.ToWin32(Border);                     // Win11 边框描边
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch
        {
            // 老系统不支持就算了，保持系统默认外观
        }
    }

    /// <summary>让 ListView / TreeView 的滚动条用暗色（Win10 1903+ 有效）。</summary>
    public static void UseDarkScrollbars(IntPtr handle)
    {
        try
        {
            SetWindowTheme(handle, "DarkMode_Explorer", null);
        }
        catch
        {
            // 忽略：系统太老时保持默认
        }
    }

    /// <summary>把右键菜单也换成暗色（原生菜单默认是浅色）。</summary>
    public static void StyleMenu(ContextMenuStrip menu)
    {
        menu.BackColor = SurfaceRaised;
        menu.ForeColor = Text;
        menu.Font = Ui(9f);
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        menu.ShowImageMargin = false;
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => SurfaceRaised;
        public override Color MenuItemSelected => SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => SurfaceHover;
        public override Color MenuItemBorder => Accent;
        public override Color MenuBorder => BorderStrong;
        public override Color ImageMarginGradientBegin => SurfaceRaised;
        public override Color ImageMarginGradientMiddle => SurfaceRaised;
        public override Color ImageMarginGradientEnd => SurfaceRaised;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    /*********
    ** 应用图标（运行时生成，避免依赖外部资源文件）
    *********/
    public static readonly Icon AppIcon = CreateAppIcon();

    private static Icon CreateAppIcon()
    {
        try
        {
            using var bitmap = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundedRect(new Rectangle(2, 2, 60, 60), 16);
                using var brush = new LinearGradientBrush(
                    new Rectangle(0, 0, 64, 64),
                    Color.FromArgb(110, 231, 168),
                    Color.FromArgb(16, 185, 129),
                    45f);
                g.FillPath(brush, path);
                using var font = new Font("Segoe UI", 33f, FontStyle.Bold, GraphicsUnit.Pixel);
                TextRenderer.DrawText(g, "S", font, new Rectangle(0, -2, 64, 66),
                    Color.FromArgb(5, 24, 15),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            IntPtr hIcon = bitmap.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    /*********
    ** P/Invoke
    *********/
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
