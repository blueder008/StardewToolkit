using System.Diagnostics;
using System.IO.Compression;

namespace StardewToolkit.Services;

/// <summary>SMAPI 安装器的执行结果。</summary>
public sealed record SmapiInstallResult(
    bool Success,
    Version? InstalledVersion,
    string Output);

/// <summary>
/// 解包官方 SMAPI 安装包并静默调用其内部安装器。
///
/// 为什么不重定向输出：官方安装器内部的 Console.Clear()/Console.ReadKey()
/// 要求真实的控制台缓冲区；一旦 stdout 被重定向它就会抛“句柄无效”直接崩
/// （实测 SMAPI 4.5.2）。所以这里选择：
///     - 隐藏它的控制台窗口（WindowStyle=Hidden，控制台本体存在，API 正常）
///     - 不捕获输出，靠事后校验游戏目录判断成败
///     - 失败时保留解压目录，用户可以手动运行 install on Windows.bat 查看报错
/// </summary>
public sealed class SmapiInstallerRunner
{
    private static readonly string TempDir =
        Path.Combine(Path.GetTempPath(), "StardewToolkit");

    /// <summary>解压目录放在 LOCALAPPDATA 而不是 TEMP：官方 bat 会拒绝在 TEMP 里运行
    /// (它以为你是解压前忘了解压)，这样失败时用户手动跑 bat 也能通。</summary>
    private static readonly string ExtractRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StardewToolkit", "installer");

    /// <summary>安装包缓存目录（也用于下载）。</summary>
    public static string WorkspaceDirPath => TempDir;

    public string ZipPath { get; }

    public SmapiInstallerRunner(string zipPath)
    {
        ZipPath = zipPath;
    }

    /// <summary>运行安装或卸载。uninstall=true 时调用官方卸载流程。</summary>
    public async Task<SmapiInstallResult> RunAsync(
        bool uninstall, string gamePath, Action<string> log, CancellationToken ct = default)
    {
        var extractDir = Path.Combine(ExtractRoot, "run-" + Guid.NewGuid().ToString("N"));
        log("解压安装包到本地目录…");
        ZipFile.ExtractToDirectory(ZipPath, extractDir, true);

        try
        {
            // SMAPI 4.5+ 安装包根目录是 "SMAPI x.y.z installer/"，Windows 入口在
            // internal\windows\SMAPI.Installer.exe。兼容旧版的 install on Windows.exe。
            var installerExe =
                Directory.EnumerateFiles(extractDir, "SMAPI.Installer.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(p => p.Contains("\\windows\\", StringComparison.OrdinalIgnoreCase))
                ?? Directory.EnumerateFiles(extractDir, "install on Windows.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException("安装包里没找到 SMAPI 安装器（internal/windows/SMAPI.Installer.exe），安装包结构可能变了");

            log($"安装器入口：{Path.GetRelativePath(extractDir, installerExe)}");

            var psi = new ProcessStartInfo(installerExe)
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden, // 控制台存在但隐藏；不重定向输出（见类注释）
                WorkingDirectory = Path.GetDirectoryName(installerExe),
            };
            psi.ArgumentList.Add(uninstall ? "--uninstall" : "--install");
            psi.ArgumentList.Add("--game-path");
            psi.ArgumentList.Add(gamePath);
            psi.ArgumentList.Add("--no-prompt");

            log($"执行官方安装器：{string.Join(' ', psi.ArgumentList)}");

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("官方安装器进程启动失败");

            // 兜底超时：正常安装在一分钟内；10 分钟还没结束多半是卡住了，直接终止
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log("⚠ 官方安装器超过 10 分钟没有结束，已强制终止。");
                try { process.Kill(true); } catch { /* 已经退出就无所谓 */ }

                log($"保留解压目录供排查：{extractDir}");
                return new SmapiInstallResult(false, GameInspector.GetSmapiVersion(gamePath), "installer timed out");
            }

            bool success = uninstall
                ? !GameInspector.HasSmapiFiles(gamePath)
                : GameInspector.HasSmapiFiles(gamePath);

            var version = GameInspector.GetSmapiVersion(gamePath);
            log(success
                ? (uninstall ? "卸载完成。" : $"完成，当前 SMAPI 版本：{version?.ToString() ?? "未知"}")
                : "⚠ 官方安装器执行完毕，但目录校验失败（可能被杀软拦截或路径有误）");

            if (!success)
            {
                log($"要查看官方安装器的具体报错，可以手动运行：" +
                    Path.Combine(extractDir, "install on Windows.bat"));
                log($"（或它同目录的 internal\\windows\\SMAPI.Installer.exe，窗口会显示信息）");
            }

            return new SmapiInstallResult(success, version, "installer output not captured (hidden console)");
        }
        finally
        {
            if (GameInspector.HasSmapiFiles(gamePath) || !File.Exists(ZipPath))
            {
                // 成功或异常之外的情况清理掉；失败时保留（见 RunAsync 内的日志）
                try { Directory.Delete(extractDir, true); } catch { /* 下次再清 */ }
            }
        }
    }

    /// <summary>清理整个临时工作目录（应用退出时调用）。</summary>
    public static void PurgeWorkspace()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { }
        try { if (Directory.Exists(ExtractRoot)) Directory.Delete(ExtractRoot, recursive: true); } catch { }
    }
}
