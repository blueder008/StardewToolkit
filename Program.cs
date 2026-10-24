namespace StardewToolkit;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 浏览器拉起带进来的 nxm:// 链接（.nxm 协议关联用）
        var pendingLinkRaw = args.FirstOrDefault(a => a?.Trim().Trim('"').StartsWith("nxm://", StringComparison.OrdinalIgnoreCase) == true);

        // 单实例
        bool createdNew;
        using var mutex = new Mutex(true, "StardewToolkit-singleton", out createdNew);
        if (!createdNew)
        {
            // 已有主实例在跑：把链接转发给它；没有链接就请求它把窗口亮出来，
            // 然后这个影子进程退出。绝不能什么都不做——双击没反应会像程序坏了。
            if (pendingLinkRaw is not null)
                Services.NxmPipe.TrySend(pendingLinkRaw);
            else
                Services.NxmPipe.TrySend(Services.NxmPipe.ActivateMessage);
            return;
        }

        // 受限环境自检：从沙箱/低权限终端启动的程序，Windows 不允许它拉起浏览器等正常程序，
        // 后面点「打开页面」就会全部失败。一打开就说清楚，别让用户到点链接时才撞墙。
        if (Services.ShellHelper.IsLowIntegrity())
        {
            System.Windows.Forms.MessageBox.Show(
                "本程序当前运行在受限（低权限）环境里，浏览器等外部程序将无法启动。\n\n" +
                "请关闭本窗口，直接双击 StardewToolkit.exe 重新打开\n" +
                "（不要从终端、脚本或其他沙箱工具里启动）。",
                "运行环境受限", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var pendingLink = pendingLinkRaw is null ? null : Services.NxmLink.Parse(pendingLinkRaw);
        Application.Run(new UI.MainForm(pendingLink));

        Services.SmapiInstallerRunner.PurgeWorkspace();
    }
}
