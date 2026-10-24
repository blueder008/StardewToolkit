using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StardewToolkit.Services;

/// <summary>
/// 打开外部网址的统一入口，带「真的打开了吗」的验证和多种兜底方式。
///
/// 为什么需要这么麻烦：本工具经常和浏览器处在不同权限级别（浏览器被以管理员身份
/// 启动过就会留在提升权限状态），这时：
///   - 普通权限的进程 ShellExecute 打开网址，浏览器会弹「现有实例以提升的权限运行」；
///   - 交给 explorer.exe 中转又可能被 UIPI 静默拦掉，表现就是「点了没反应」。
/// 所以这里按顺序尝试多种方式，每次启动后检查浏览器进程是否真的起来了；
/// 全部失败时把最后的真实错误（比如浏览器的锁文件报错）交给调用方展示。
/// </summary>
public static class ShellHelper
{
    /// <summary>最后一次打开失败的原因（给界面展示用）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 用默认浏览器打开网址。真的打开了返回 true；
    /// 全部方式都失败返回 false，原因在 <see cref="LastError"/>。
    /// </summary>
    public static async Task<bool> OpenUrlAsync(string url, Action<string>? log = null)
    {
        LastError = null;

        var browser = DefaultBrowser();
        var alreadyRunning = browser is not null && IsRunning(browser.Value.ProcessName);

        // 1) 标准 ShellExecute：绝大多数正常环境走这一条
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            // 解析不出默认浏览器时没法验证进程，只能按成功处理（否则会误报失败）
            if (browser is null || alreadyRunning || await WaitForBrowserAsync(browser, TimeSpan.FromSeconds(4)))
                return true;
            log?.Invoke("标准方式没有拉起浏览器，改用直接启动…");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log?.Invoke($"标准方式失败：{ex.Message}");
        }

        // 2) 直接启动默认浏览器可执行文件（绕过 shell 关联，并且能拿到它的报错）
        if (browser is { } info)
        {
            try
            {
                var (ok, error) = await LaunchBrowserExeAsync(info, url);
                if (ok)
                    return true;
                LastError = error;
                log?.Invoke($"直接启动浏览器失败：{error}");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log?.Invoke($"直接启动浏览器异常：{ex.Message}");
            }
        }

        // 3) explorer.exe 中转（普通权限的 shell 代发）
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = false });
            if (await WaitForBrowserAsync(browser, TimeSpan.FromSeconds(3)))
                return true;
        }
        catch (Exception ex)
        {
            LastError ??= ex.Message;
        }

        // 4) rundll32 走 FileProtocolHandler
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"url.dll,FileProtocolHandler \"{url}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (await WaitForBrowserAsync(browser, TimeSpan.FromSeconds(3)))
                return true;
        }
        catch (Exception ex)
        {
            LastError ??= ex.Message;
        }

        LastError ??= "浏览器没有响应（可能被以管理员身份运行的实例占住，或浏览器本身异常）。";
        if (IsLowIntegrity())
            LastError += "\n\n另外：本程序当前运行在受限（低权限/沙箱）环境里，这种环境通常不允许启动浏览器。" +
                         "请关闭后直接双击 StardewToolkit.exe 运行（不要从终端/IPC 启动）。";
        return false;
    }

    /// <summary>打开失败时的兜底：把链接复制到剪贴板，让用户自己粘贴。返回是否复制成功。</summary>
    public static bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 本进程是否运行在低完整性（沙箱/受限令牌）环境里。这种环境写不了浏览器配置目录，
    /// 浏览器会直接起不来（典型报错：Lock file can not be created! code 5）。
    /// 公开给启动自检用：与其让用户在点链接时撞墙，不如一打开就说清楚。
    /// </summary>
    public static bool IsLowIntegrity()
    {
        try
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out token))
                return false;
            try
            {
                int need;
                GetTokenInformation(token, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out need);
                if (need <= 0)
                    return false;

                var buffer = Marshal.AllocHGlobal(need);
                try
                {
                    if (!GetTokenInformation(token, 25, buffer, need, out _))
                        return false;

                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = Marshal.ReadByte(sid, 1);
                    var last = Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
                    return last < 0x2000; // 低于 Medium
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returned);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /*********
    ** 内部
    *********/
    private static (string ExePath, string ProcessName, string ArgsTemplate)? DefaultBrowser()
    {
        try
        {
            var userChoice = (string?)Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice",
                "ProgId", null);
            if (string.IsNullOrWhiteSpace(userChoice))
                return null;

            var command = (string?)Registry.GetValue(
                $@"HKEY_CLASSES_ROOT\{userChoice}\shell\open\command", "", null);
            if (string.IsNullOrWhiteSpace(command))
                return null;

            // 形如： "C:\...\msedge.exe" --single-argument %1
            command = command.Trim();
            string exe, args;
            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                if (end < 0)
                    return null;
                exe = command[1..end];
                args = command[(end + 1)..].Trim();
            }
            else
            {
                var space = command.IndexOf(' ');
                exe = space < 0 ? command : command[..space];
                args = space < 0 ? "" : command[(space + 1)..].Trim();
            }

            return (exe, Path.GetFileNameWithoutExtension(exe), args);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动后等浏览器进程出现（浏览器本来没开时，这是「真的打开了」的可靠信号）。</summary>
    private static async Task<bool> WaitForBrowserAsync(
        (string ExePath, string ProcessName, string ArgsTemplate)? browser, TimeSpan timeout)
    {
        if (browser is not { } info)
            return false; // 解析不出默认浏览器，没法验证，交给后续方式判断

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning(info.ProcessName))
                return true;
            await Task.Delay(250);
        }
        return false;
    }

    /// <summary>
    /// 直接运行默认浏览器的 exe 并带上网址。浏览器如果因为配置目录/权限问题起不来，
    /// 通常会很快退出并往 stderr 写原因，这里把它捞出来作为失败原因。
    /// </summary>
    private static async Task<(bool Ok, string? Error)> LaunchBrowserExeAsync(
        (string ExePath, string ProcessName, string ArgsTemplate) browser, string url)
    {
        var quotedUrl = "\"" + url + "\"";
        var args = string.IsNullOrWhiteSpace(browser.ArgsTemplate)
            ? quotedUrl
            : browser.ArgsTemplate.Contains("%1", StringComparison.Ordinal)
                ? browser.ArgsTemplate.Replace("%1", quotedUrl)
                : browser.ArgsTemplate + " " + quotedUrl;

        using var process = Process.Start(new ProcessStartInfo(browser.ExePath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        });

        if (process is null)
            return (false, "浏览器进程没有启动");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 3 秒还没退出 = 浏览器已经在跑，视为成功
            return (true, null);
        }

        if (process.ExitCode == 0)
            return (true, null);

        var stderr = "";
        try
        {
            stderr = await process.StandardError.ReadToEndAsync();
        }
        catch
        {
            // 忽略
        }

        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .TakeLast(3)
            .ToArray();

        return (false, lines.Length > 0
            ? $"浏览器启动后退出（退出码 {process.ExitCode}）：{string.Join(" / ", lines)}"
            : $"浏览器启动后立即退出（退出码 {process.ExitCode}）");
    }
}
