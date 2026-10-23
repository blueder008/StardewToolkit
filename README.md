# 星露谷 SMAPI 一键安装器（Steam 版）

一键下载并安装最新 SMAPI：
1. 自动检测 Steam 库里的 Stardew Valley（含多库多盘位）
2. 从 GitHub Releases 下载官方安装包（下载后做 SHA-256 校验，官方 digest 来自 release asset）
3. 用静默参数调用官方安装器，不自己重写安装逻辑
4. 检测当前已装 SMAPI 版本，提示更新；同一按钮就是升级流程（官方安装器幂等）
5. 保留所有 Mods 和 `smapi-internal/config.user.json` 用户配置（官方安装器自带行为）
6. Steam 启动项给出可复制的字符串

支持的官方参数（见 SMAPI 源码 `InteractiveInstaller.cs`）：
```
"install on Windows.exe" --install --game-path "<游戏目录>" --no-prompt
"install on Windows.exe" --uninstall --game-path "<游戏目录>" --no-prompt
```

## 构建与运行

需要 .NET 8 SDK（Windows）。在项目目录下：

```bash
dotnet run --project E:\harness\StardewToolkit
```

或直接跑编译结果：
```
StardewToolkit\bin\Debug\net8.0-windows\StardewToolkit.exe
```

发布成单文件：
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 项目结构

```
StardewToolkit/
├── Program.cs                       入口 + 单实例互斥锁
├── UI/MainForm.cs                   主窗口（安装/更新/卸载入口、日志、状态、Mod 管理入口、nxm 链接接收）
├── UI/ModManagerForm.cs             Mod 管理器窗口（列表、启停、更新检查、安装/更新、日志、Nexus 密钥）
├── UI/LogViewerForm.cs              SMAPI 日志查看器（级别过滤、检索、详情/复制、跳转 Mod 管理器）
├── UI/NexusKeyDialog.cs             Nexus API key 配置（链接官方 key 页、验证、保存、nxm 联动注册）
├── UI/BackupManagerForm.cs          备份管理窗口（快照列表、恢复、删除）
├── UI/ConflictReportForm.cs         冲突检测报告窗口（分组列表、参与者、建议）
├── UI/NxmDownloadForm.cs            浏览器回传下载队列窗口（一个接一个下载安装）
└── Services/
    ├── SteamLocator.cs              Steam 库探测（注册表 + libraryfolders.vdf）
    ├── GameInspector.cs             目录状态判断（游戏/SMAPI/版本/进程）
    ├── GitHubReleaseService.cs      获取最新 Release + 下载 + SHA-256 校验
    ├── SmapiInstallerRunner.cs      解包并用静默参数调用官方安装器
    ├── ModManifest.cs               manifest.json 模型 + 宽容解析（注释/尾逗号/单值数组）
    ├── ModScanner.cs                Mods 目录扫描（含禁用区）+ 文件夹移动 + 版本比较
    ├── SmapiModCheckClient.cs       smapi.io 官方 web API 批量更新检查
    ├── SmapiLogParser.cs            SMAPI 日志解析（消息头/多行堆栈/启动横幅）
    ├── ModPackageInstaller.cs       mod 安装包安装（识别 manifest、按 ID 替换、备份；v0.9.0 起支持一键装多 mod zip）
    ├── ModConflictDetector.cs       冲突检测（重复 ID / XNB 资源覆盖 / Content Patcher 同 Target）
    ├── BackupManager.cs             Mods/.backup 快照管理（列举、恢复回滚/并存、删除）
    ├── GitHubModUpdateService.cs    GitHub mod 更新（解析 repo、挑 zip、下载）
    ├── NexusClient.cs               Nexus 官方 API v1（验证 key、查主文件、Premium 直下、nxm 授权下载）
    ├── NxmLink.cs                   nxm:// 链接解析（游戏域/mod id/文件 id/临时授权）
    ├── NxmProtocolRegistrar.cs      nxm:// 协议注册（HKCU，免管理员）
    ├── NxmPipe.cs                   跨实例管道（第二实例把链接转发给已开的主实例）
    └── AppConfig.cs                 %APPDATA% 下的本地配置
```

## Mod 管理器（v0.3.0 新增）

主窗口点「Mod 管理…」进入，特性对齐 Stardrop 的核心流程：

- **扫描**：递归解析 `Mods/` 下所有 `manifest.json`（内容包常嵌套在 mod 目录里），无效目录（XNB 包、SMAPI 安装器残留、垃圾文件）单独标红
- **启停**：官方语义——SMAPI 官方扫描器忽略名字以点开头的文件夹（`ModScanner.cs`），所以禁用就是把 mod 移到 `Mods/.disabled/`，启用时按索引移回原位（不一定是 Mods 根目录）
- **更新检查**：走 smapi.io 官方 web API（`POST /api/v3.0/mods`，SMAPI 自己和 Stardrop 都用它）：
  - 一个请求批量查所有 mod，免 API key
  - 支持全部 update key 来源（Nexus / GitHub / CurseForge / ModDrop / mod.io 等）
  - 顺带返回官方维基维护的**兼容性状态**（不再维护 / 已损坏 / 已废弃等，红色提示）
- **依赖感知**：禁用前检查哪些 mod 依赖它并警告；列出缺少的必需依赖；重复 MOD ID 检测
- **跳转**：自动从 update key 推断 mod 页面地址（Nexus 编号 → nexusmods 页、GitHub 作者/仓库 → 仓库页）

已知限制：

- 更新只提示结果+跳转下载页，暂不自动下载安装 mod（Nexus 的下载需要登录，后续做）
- 全部启用/一键更新按钮没做（批量改 mods 目录风险大，先保证单选操作稳妥）

## SMAPI 日志查看器（v0.4.0 新增）

主窗口点「日志查看…」进入（只读工具，游戏开着也能看）：

- **来源**：`%APPDATA%\StardewValley\ErrorLogs` 下的全部 `*.txt`，按修改时间倒序；**SMAPI-crash.txt 优先选中**（那是上次崩溃留下的完整现场）
- **行格式**：按 SMAPI 源码 `Framework/Monitor.cs` 写死的 `[HH:mm:ss LEVEL source] 消息` 解析，多行消息（异常堆栈）正确合并成一条
- **过滤**：默认隐藏 TRACE/DEBUG（开发者噪音），只看 INFO/警告/错误；搜索框按内容/来源/时间筛
- **自动定位**：加载后自动滚到第一条错误；「跳到下个错误」循环定位；出错来源（哪个 mod 在报错）直接列在概要里
- **求助友好**：选中条目在详情面板看完整堆栈，「复制这条」一键复制完整信息发给别人
- 启动横幅解析出 SMAPI/游戏版本、mod 数量、日志开始时间，显示在概要栏
- **联动**：错误条目如果是某个 mod 报的（来源不是 SMAPI/Game），详情面板会出现「在 Mod 管理器中定位…」——自动从日志解析出 Mods 目录（`Mods go here:` 横幅），打开 Mod 管理器并筛出/选中这个 mod；Mod 管理器里也能点「日志…」反向打开

## Nexus 浏览器联动（v0.7.0 新增）

- 在「Nexus 设置」小窗里可以**把本工具注册成 `nxm://` 链接的处理器**（写 HKCU 用户注册表，不需要管理员；随时可一键撤销）：
  - 之后在 Nexus 网站文件页点 **「Mod Manager Download」**，浏览器会自动拉起本工具接住下载
  - 程序没开着也没关系：先开的实例会通过命名管道把链接转交给已开着的主实例，下载队列窗口弹出来排着处理
  - 若已注册了其他 Mod 管理器（NMM/Vortex/Stardrop 等），重新注册会覆盖，可以随时改回去

**v0.7.1 修复——Stardrop 抢注 nxm 协议**：之前只写了裸协议键，
而 Windows 解析 URL 协议的优先级是 `UserChoice → 注册应用的能力声明 → 裸协议键`，
Stardrop 恰好注册了能力声明，导致点注册后浏览器下载仍拉起 Stardrop。
现在本工具按同款方式注册完整三层（自有 ProgId `StardewToolkit.nxm` + Capabilities 能力声明 + 裸键），
接管时会清掉其他应用对 nxm 的声明（撤销后不代为恢复，在对方设置里重注册即可拿回）。

**排障：注册成功但浏览器点下载还是拉起别的管理器？**
1. 关掉本工具重开，在「Nexus 设置」里确认状态显示绿色「已注册 ✓」（旧版本点的注册不完整，重新点一次覆盖注册）
2. 浏览器可能还缓着自己的处理器选择——尤其 **Firefox**：设置 → 常规 → 应用程序 → 搜 `nxm`，改成「总是询问」或直接选本工具；Chrome/Edge 一般不用管
3. 没作用就重启一次浏览器（它会重新读系统关联）
- **免费账号的大招**：nxm 链接自带 Nexus 官方的临时 key+expires 授权，是**官方给免费账号也放行**的下载路径——
  免费账号从浏览器点「Mod Manager Download」一样能全自动下载并安装，不再强制手动
- 下载队列窗口自动做的事：校验游戏域（只认 stardewvalley）、防止游戏运行时安装、
  按 Nexus mod id 找本地对应 mod（识别 `Nexus:编号` 更新键）比对版本、下载、按 manifest UniqueID 替换旧版并备份
- key 只有效几分钟，过期/复用旧链接 → 403 → 明确提示回网站重新点一次
- 一次点多个「Mod Manager Download」也没问题：排序进队列一个接一个装

## Mod 安装与更新（v0.5.0 起逐步完善）

Mod 管理器里的动作：

- **安装 zip…**：任何渠道下载的 mod 安装包（Nexus 浏览器下载、mod 官网、群传的都行）直接导入：
  - 按 manifest 的 UniqueID 自动找到已装的同一个 mod，**原文件夹名原位替换**（旧版备份到 `Mods/.backup/时间戳/`，点开头不会被 SMAPI 加载）
  - 支持 zip 子目录结构、纯根目录内容包；过滤 macOS 打包垃圾；防 zip-slip 路径穿越
  - 不用再手动找 mod 文件夹剪切粘贴
- **自动更新**（选中一个有更新的 mod），按更新键自动分流：
  - `GitHub:作者/仓库`：免 key 直连 GitHub，下载 release zip 自动安装
    - 多 mod 合仓库按 manifest Name 归一化匹配 zip 资产名（比如 Pathoschild 一个仓库出几十个 mod）
    - 仓库没有 Release 会解释清楚（比如作者只发 Nexus），不甩生硬错误码
  - `Nexus:编号`：走 Nexus 官方 API（v0.6.0 新增），先配置 personal API key：
    - Mod 管理器右上「Nexus 密钥…」粘贴 key → 一键验证（显示用户名和会员等级）→ 保存
    - **Premium 会员**：全自动——查最新主文件 → 版本确认 → 从 Nexus CDN 下载 → 按 UniqueID 替换旧版
    - **免费账号**：自动检查版本没问题；下载会打开官方文件页（带 file_id 直达对应文件），浏览器下载 zip 后「安装 zip…」两步导入（Nexus 官方限制，免费账号 API 不能直下）
- **更新页面**：其他来源去官方下载页，浏览器下载 zip 后「安装 zip…」导入

## 备份管理（v0.8.0 新增）

Mod 管理器里点「备份管理…」打开。旧版本不用删了才发现装坏了——替换/更新产生的备份都在能找回来：

- **来源**：「安装 zip…」替换旧版、「自动更新」升级、Nexus/nxm 下载安装时，旧 mod 都自动存进 `Mods/.backup/时间戳/`（点开头，SMAPI 不加载；一份快照存一批）
- **窗口布局**：左列快照（备份时间 / 条目数 / 大小），右列选中快照里的 mod（manifest 读出的名字 / 备份版本 / 文件夹名 / 大小）
- **恢复单个旧版**：选中右侧条目 →「恢复所选…」（或双击）→ 冲突时二选一：
  - **替换已装版本**（推荐，回滚语义）：当前 2.0 换回旧 1.9，2.0 自动再存一份进新快照，随时可再换回
  - **并存恢复**：加编号后缀（`SomeMod (2)`）一起恢复，SMAPI 里两个版本都存在，重复 ID 界面会提示，事后自己取舍
- **恢复整份快照**：一次撤回一整批
- **删除**：单份快照 / 清空全部备份（都带确认；移进**回收站**，需要时可以从回收站还原）
- 游戏运行时会拦截恢复操作（文件被占用）；恢复的目录一律放回 Mods 根目录

## 冲突检测（v0.9.0 新增）

Mod 管理器里点「冲突检测」扫「多个启用的 mod 改同一份资源」的组合，报告窗口里左侧分组（可按关键字筛）、右侧参与者，每组附处置建议：

- **重复 ID**：几个 mod 的 manifest UniqueID 一样——SMAPI 只加载文件夹顺序里的第一个，其余静默忽略。删掉多余的即可
- **XNB 资源覆盖**：两个 mod 都带同相对路径的 `.xnb/.xgs/.xsb/.xwb`（老式资源替换 mod 常见）——实际生效的是加载顺序最后一个，一般二选一
- **内容包 Patch 冲突**：两个 Content Patcher 内容包都在 patch 同一个 Target（如 `Maps/FarmHouse`）——依加载顺序后者覆盖前者，优先用 mod 作者提供的兼容版本
- 只看**启用中**的 mod（禁用区的副本不参与，不算冲突）；mod 的 manifest 信息缺失也能列出来（退化成按文件夹名比）
- 报告里可以直接操作：双击参与者打开文件夹；「删除所选 mod…」把多余的冲突方整棵移进回收站（不永久删），删完重新扫描

**v0.9.0 安装器加强**：功能式 zip（GitHub release / 作者合并包，一个 zip 里带多个 mod，或框架 mod 里嵌内容包）现在**一次装完**——
每个 mod 独立出结果；zip 里同一 ID 出现多份时取最浅层的；嵌在别人目录里的内容包随父级一起装、不单独恢复（避免重复 ID）。

## 已知限制

- 仅支持 Steam 版（GOG/绿色版走「手动选择目录…」）
- Steam 启动项不自动改写（因为要写 VDF、且要求 Steam 完全退出），界面提供复制按钮
- SMAPI 只在运行时从 GitHub 下载，不随程序打包（SMAPI 为 LGPL-3.0，避免再分发问题，也保证永远最新）
- 更新检查和 SMAPI 的版本映射依赖本地已装 SMAPI/游戏版本号，读不到时不给「建议版本」，只显示最新版本
- mod 更新下载的 zip 没有官方哈希可校验（SMAPI 自身的安装包仍走官方 SHA-256）；来源限制为作者官方 release
- 已禁用的 mod 被 zip 安装时，会以启用状态装到 Mods 根目录（原禁用副本进备份目录，日志有提示）
- Nexus API key 以明文存在 `%APPDATA%\StardewToolkit\config.json` 里（和官方 Mod 管理工具同级的现实取舍；本机自用风险可控）——介意的话用完可以点「Nexus 密钥…」重新生成 key 或删除配置文件
- 非 Premium 的 Nexus 账号 API 直下有限制（官方规则）——但走浏览器「Mod Manager Download」（nxm 链接自带临时授权）免费账号一样可以全自动下载安装

## 后续路线

1. 日志 → Mod 管理器一键跳转的补充：从 Mod 管理器选定行反向「在日志里找报错」
2. GOG 支持、已装版本多路径（多份游戏副本）
3. 可选静默自动更新（补丁版本、游戏未运行时）
4. Nexus 下载进度的体积/速度显示
