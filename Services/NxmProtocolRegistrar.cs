using Microsoft.Win32;

namespace StardewToolkit.Services;

/// <summary>
/// 把本程序注册成 nxm:// 协议的处理器（HKCU，不需要管理员权限）。
///
/// 实测 KEY LESSON（v0.7.1 教训）：只写 HKCU\Software\Classes\nxm 裸协议键**不够**——
/// Windows 解析 URL 协议的优先级是：
///   1. UserChoice（用户在系统里明确指定的；带 hash 保护，不硬写）
///   2. 注册应用的能力声明（HKCU\Software\RegisteredApplications → 该应用
///      Capabilities\UrlAssociations\nxm → 自有 ProgId 类）——Stardrop/Vortex 都走这条路
///   3. 裸协议键 Classes\nxm\shell\open\command（没有任何应用注册能力时的兜底）
/// 所以必须像 Stardrop 一样把三层都写齐：自有 ProgId + Capabilities + 裸键，
/// 并在接管时清掉其他应用对 nxm 的能力声明（撤销时**不会**替它们恢复，是明确的用户选择）。
/// 浏览器（尤其 Firefox）可能还缓存着自己的处理器选择，那种情况指引设置里改。
/// </summary>
public static class NxmProtocolRegistrar
{
    private const string ProtocolName = "nxm";
    private const string ProgIdName = "StardewToolkit.nxm";
    private const string ClassesRoot = @"Software\Classes";
    private const string AppName = "StardewToolkit";
    private const string CapabilitiesRoot = @"Software\StardewToolkit\Capabilities";
    private const string RegisteredApplicationsRoot = @"Software\RegisteredApplications";

    /// <summary>当前生效的 nxm 处理器描述（程序名），没人接管返回 null。</summary>
    public static string? GetNxmClaimOwner()
    {
        // 1. UserChoice 最优先
        var userChoiceProgId = GetUserChoiceProgId();
        if (!string.IsNullOrEmpty(userChoiceProgId))
            return DescribeProgId(userChoiceProgId!);

        // 2. 能力声明链（RegisteredApplications → Capabilities → UrlAssociations\nxm）
        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsRoot);
            var valueNames = registered?.GetValueNames() ?? [];
            foreach (var appName in valueNames.Where(n => !string.IsNullOrEmpty(n)).OrderBy(n => n))
            {
                var capPath = registered?.GetValue(appName) as string;
                if (string.IsNullOrEmpty(capPath))
                    continue;
                using var urlAssoc = Registry.CurrentUser.OpenSubKey($@"{capPath}\UrlAssociations");
                var progId = urlAssoc?.GetValue(ProtocolName) as string;
                if (!string.IsNullOrEmpty(progId))
                    return appName;
            }
        }
        catch
        {
            // 读不了就当下没人接管
        }

        // 3. 裸协议键兜底
        var bare = GetCommandOfProgId(ProtocolName);
        return bare is null ? null : DescribeProgId(ProtocolName);
    }

    /// <summary>裸协议键当前指向的可执行文件路径；没注册/读不到返回 null。</summary>
    public static string? GetRegisteredExePath()
    {
        var command = GetCommandOfProgId(ProgIdName) ?? GetCommandOfProgId(ProtocolName);
        return command is null ? null : ExtractExePath(command);
    }

    /// <summary>是否已注册且指向当前这个 exe（三层都检查关键点）。</summary>
    public static bool IsRegisteredForThisApp()
    {
        if (Environment.ProcessPath is null)
            return false;

        // 能力声明链完整 且 ProgId command 指向当前 exe
        if (GetCapabilitiesProgId() != ProgIdName)
            return false;
        var progIdCommand = GetCommandOfProgId(ProgIdName);
        if (string.IsNullOrEmpty(progIdCommand) || !string.Equals(ExtractExePath(progIdCommand!), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return false;

        // 裸键也要是我们的（没有它 fallback 场景还是会落到别处）
        var bareCommand = GetCommandOfProgId(ProtocolName);
        if (string.IsNullOrEmpty(bareCommand) || !string.Equals(ExtractExePath(bareCommand!), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return false;

        // 没有别人还残留 nxm 声明（Stardrop 那种），有就说明当前实际生效的可能不是我们
        return GetNxmClaimOwner() is null || GetNxmClaimOwner() == AppName;
    }

    /// <summary>注册为本工具接管（覆盖任何已有处理器，包括其他 Mod 管理器的能力声明）。</summary>
    public static void Register(string exePath)
    {
        var command = $"\"{exePath}\" \"%1\"";
        var icon = $"\"{exePath}\",0";

        // ① 自有 ProgId 类（Windows「默认应用」和声明链都指向它）
        using (var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{ProgIdName}"))
        {
            progId.SetValue(null, "URL:Nexus Mods Protocol");
            progId.SetValue("Url Protocol", "");
            using var iconKey = progId.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, icon);
            using var commandKey = progId.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(null, command);
        }

        // ② 裸协议键（无声明者时的 fallback）
        using (var bare = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{ProtocolName}"))
        {
            bare.SetValue(null, "URL:Nexus Mods Protocol");
            bare.SetValue("Url Protocol", "");
            using var iconKey = bare.CreateSubKey("DefaultIcon");
            iconKey.SetValue(null, icon);
            using var commandKey = bare.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(null, command);
        }

        // ③ 能力声明（这一层是 0.7.1 失败的关键——Stardrop 就是靠它抢的优先级）
        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesRoot))
        {
            capabilities.SetValue("ApplicationName", AppName);
            capabilities.SetValue("ApplicationDescription", "星露谷 SMAPI 工具箱（Mod 管理器 / 日志查看器）。");
            capabilities.SetValue("ApplicationIcon", icon);
            using var urlAssoc = capabilities.CreateSubKey("UrlAssociations");
            urlAssoc.SetValue(ProtocolName, ProgIdName);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsRoot))
        {
            registered.SetValue(AppName, CapabilitiesRoot);
        }

        // ④ 接管：清掉其他应用对 nxm 的能力声明（Stardrop / NMM / Vortex 等残留），否则它们的声明优先级更高仍会胜出
        ClearOtherNxmClaims();
    }

    /// <summary>撤销注册。只删自己的东西；其他应用被清掉的声明不代为恢复（点它的设置重新注册即可）。</summary>
    public static void Unregister()
    {
        var ours = Environment.ProcessPath is { } exePath
            && string.Equals(ExtractExePath(GetCommandOfProgId(ProtocolName) ?? ""), exePath, StringComparison.OrdinalIgnoreCase);

        try { Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{ProgIdName}", throwOnMissingSubKey: false); } catch { }
        if (ours)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{ProtocolName}", throwOnMissingSubKey: false); } catch { }
        }

        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsRoot, writable: true);
            var valueName = registered?.GetValueNames()
                .FirstOrDefault(n => string.Equals(n, AppName, StringComparison.OrdinalIgnoreCase));
            if (registered is not null && valueName is not null)
                registered.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch { }

        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\StardewToolkit", throwOnMissingSubKey: false); } catch { }
    }

    /*********
    ** 内部
    *********/
    /// <summary>删除其他应用对 nxm 的能力声明（Stardrop 残留的 Capabilities\UrlAssociations\nxm 等）。</summary>
    internal static void ClearOtherNxmClaims()
    {
        string[] appNames;
        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsRoot, writable: true);
            appNames = registered?.GetValueNames() ?? [];
        }
        catch
        {
            return;
        }

        foreach (var appName in appNames)
        {
            // 自己的不动
            if (appName is null || appName.Length == 0 || string.Equals(appName, AppName, StringComparison.OrdinalIgnoreCase))
                continue;

            // 逐个应用隔离处理：UWP/AppX 键有的拒绝写访问（SecurityException），别让一个顽固键毁掉整个接管
            try
            {
                string capPath;
                try
                {
                    using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsRoot);
                    capPath = registered?.GetValue(appName) as string ?? "";
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(capPath))
                    continue;

                string progId;
                try
                {
                    using var urlAssoc = Registry.CurrentUser.OpenSubKey($@"{capPath}\UrlAssociations", writable: true);
                    if (urlAssoc is null)
                        continue;
                    progId = urlAssoc.GetValue(ProtocolName) as string ?? "";
                    if (string.IsNullOrEmpty(progId))
                        continue;
                    urlAssoc.DeleteValue(ProtocolName, throwOnMissingValue: false);
                }
                catch
                {
                    continue; // 拒写就算了，裸键和我们的声明通常仍然足够
                }
            }
            catch
            {
                // 保险层
            }
        }
    }

    /// <summary>能力声明链里指向我们的 ProgId；没配置返回 null。</summary>
    private static string? GetCapabilitiesProgId()
    {
        try
        {
            using var urlAssoc = Registry.CurrentUser.OpenSubKey($@"{CapabilitiesRoot}\UrlAssociations");
            return urlAssoc?.GetValue(ProtocolName) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetUserChoiceProgId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\nxm\UserChoice");
            var progId = key?.GetValue("ProgId") as string;
            return string.IsNullOrWhiteSpace(progId) ? null : progId;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetCommandOfProgId(string progId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{progId}\shell\open\command");
            return key?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给用户看的处理器名：有识别人名用地名，否则拿 command 里抠 exe 名。</summary>
    private static string DescribeProgId(string progId)
    {
        // 能力链比对：RegisteredApplications 里哪个 app 的声明指向它
        try
        {
            using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsRoot);
            foreach (var appName in registered?.GetValueNames() ?? [])
            {
                var capPath = registered?.GetValue(appName) as string;
                if (string.IsNullOrEmpty(capPath))
                    continue;
                using var urlAssoc = Registry.CurrentUser.OpenSubKey($@"{capPath}\UrlAssociations");
                if (string.Equals(urlAssoc?.GetValue(ProtocolName) as string, progId, StringComparison.OrdinalIgnoreCase))
                    return appName;
            }
        }
        catch
        {
            // 兜底走 exe 名
        }

        var command = GetCommandOfProgId(progId);
        var exeName = command is null ? null : Path.GetFileNameWithoutExtension(ExtractExePath(command));
        return string.IsNullOrEmpty(exeName) ? progId : exeName;
    }

    /// <summary>从 command 值里抠 exe 路径（形如 "C:\...\app.exe" "%1" → C:\...\app.exe）。</summary>
    private static string? ExtractExePath(string commandValue)
    {
        commandValue = commandValue.Trim();
        if (commandValue.StartsWith('"'))
        {
            var endQuote = commandValue.IndexOf('"', 1);
            return endQuote > 1 ? commandValue[1..endQuote] : null;
        }

        // 没引号：第一个空格前是路径
        var space = commandValue.IndexOf(' ');
        return space > 0 ? commandValue[..space] : commandValue;
    }
}
