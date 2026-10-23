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
            // 已有主实例在跑：把链接转发给它，然后这个影子进程退出
            if (pendingLinkRaw is not null)
                Services.NxmPipe.TrySend(pendingLinkRaw);
            else
                return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        var pendingLink = pendingLinkRaw is null ? null : Services.NxmLink.Parse(pendingLinkRaw);
        Application.Run(new UI.MainForm(pendingLink));

        Services.SmapiInstallerRunner.PurgeWorkspace();
    }
}
