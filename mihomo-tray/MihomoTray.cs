using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MihomoTray
{
    static class Program
    {
        static MainForm _mainForm;
        const string SingleInstanceMutexName = @"Local\MihomoTray.Controller";

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                bool ownsMutex = createdNew;
                if (!ownsMutex)
                {
                    try { ownsMutex = mutex.WaitOne(5000, false); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                }

                if (!ownsMutex)
                    return;

                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | SecurityProtocolType.Tls;
                    ServicePointManager.Expect100Continue = false;

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    _mainForm = new MainForm();
                    Application.Run(_mainForm);
                }
                finally
                {
                    if (_mainForm != null)
                        _mainForm.Cleanup();

                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }
    }

    class MainForm : Form
    {
        NotifyIcon _trayIcon;
        ContextMenuStrip _menu;
        Process _mihomoProcess;
        System.Windows.Forms.Timer _statusTimer;
        System.Windows.Forms.Timer _systemProxyGuardTimer;
        DateTime _lastMihomoProcessLookupUtc;

        string _basePath;
        string _configPath;
        string _trayConfigPath;
        string _mihomoExePath;
        string _exePath;
        string _activeConfigPath;
        string _cachedConfigPath;
        string _cachedConfigContent;
        DateTime _cachedConfigWriteTimeUtc;
        long _cachedConfigLength;
        string _panelHost;
        int _panelPort;
        string _panelPath;
        bool _runMihomoOnStartup;
        bool _lastTunEnabled;
        bool _systemProxyDesired;
        bool _systemProxyGuardEnabled;
        bool _pendingEnableTunAfterAdmin;
        bool _loadedLastTunEnabled;
        bool _loadedSystemProxyDesired;
        bool _isUpdatingAssets;
        /// <summary>订阅更新进行中（用于串行化并禁用菜单项）。</summary>
        bool _isUpdatingSubscription;
        /// <summary>由订阅更新触发的核心重启，抑制重复的启动/停止气泡。</summary>
        bool _suppressMihomoBalloon;
        bool _cachedSystemProxyReadOk;
        bool _cachedSystemProxyEnabled;
        string _cachedSystemProxyServer;
        DateTime _lastSystemProxyReadUtc;
        Dictionary<string, AssetVersionInfo> _assetVersions = new Dictionary<string, AssetVersionInfo>(StringComparer.OrdinalIgnoreCase);

        // ──── 菜单快照缓存（解决右键菜单卡顿）────
        // 进程枚举（Process.GetProcessesByName）与被代理进程名的文件/正则解析
        // 实测合计约 1.8 秒；把它们放在 UI 线程上会让右键菜单严重延迟弹出，
        // 并出现"菜单跟着鼠标跑"的错位现象（Windows 在迟到的那一刻才定位菜单）。
        // 因此改为：后台线程按固定间隔刷新快照，UI 线程只读快照（纯内存，微秒级）。
        volatile bool _snapshotMihomoRunning;
        volatile bool _snapshotTunEnabled;
        volatile bool _snapshotSystemProxyOn;
        volatile bool _snapshotProxiFyreInstalled;
        volatile bool _snapshotProxiFyreRunning;
        /// <summary>快照中的 ProxiFyre 上游端点（已含"是否在监听"的探测结果）。</summary>
        volatile string _snapshotProxiFyreEndpoint;
        volatile bool _snapshotProxiFyreEndpointAlive;
        /// <summary>快照中的被代理进程名列表；替换引用是原子的，UI 线程只读。</summary>
        volatile List<string> _snapshotProxiFyreAppNames = new List<string>();
        /// <summary>
        /// 快照中的当前规则模式。
        /// 之所以必须放进快照：ResolveCurrentMode() 在核心运行时会调
        /// ReadCoreMode() -> GET /configs，**这是一次真实的 HTTP 请求**。
        /// 当 external-controller 端口没人监听时，Windows 需要约 2 秒才能
        /// 判定连接被拒（实测 2000~2020ms，每次都是），而该方法又被
        /// 菜单渲染链（ApplySnapshotToMenu -> RefreshModeMenu）同步调用，
        /// 于是「每次打开菜单都白等 2 秒」。这是菜单依然卡顿的真正原因。
        /// </summary>
        volatile string _snapshotMode = ModeRule;
        /// <summary>快照中的开机自启状态（注册表读，虽便宜但不该在菜单路径上做）。</summary>
        volatile bool _snapshotAutoStart;
        /// <summary>快照是否已完成首轮填充。未填充时菜单走"加载中"占位，避免显示错误状态。</summary>
        volatile bool _snapshotReady;
        System.Threading.Timer _snapshotTimer;
        /// <summary>同一时刻只允许一个后台刷新在跑，避免计时器重入导致进程枚举风暴。</summary>
        int _snapshotRefreshBusy;

        bool _isAdmin;
        /// <summary>首次运行且数据目录里没有 mihomo.exe —— 用于给出明确提示而不是静默失败。</summary>
        bool _firstRunMissingCore;

        ToolStripMenuItem _statusItem;
        ToolStripMenuItem _startStopItem;
        ToolStripMenuItem _modeMenu;
        ToolStripMenuItem _modeRuleItem;
        ToolStripMenuItem _modeGlobalItem;
        ToolStripMenuItem _modeDirectItem;
        ToolStripMenuItem _tunItem;
        ToolStripMenuItem _proxyItem;
        ToolStripMenuItem _proxyGuardItem;
        ToolStripMenuItem _subMenu;
        ToolStripMenuItem _subMgrItem;
        ToolStripMenuItem _profileItem;
        ToolStripMenuItem _appProxyMenu;
        ToolStripMenuItem _appProxyAllItem;
        ToolStripMenuItem _appProxyServiceItem;
        ToolStripMenuItem _autoStartItem;
        ToolStripMenuItem _runMihomoItem;
        ToolStripMenuItem _panelSettingsItem;
        ToolStripMenuItem _updateAssetsItem;

        // 托盘图标（KY 字母 + 右上角指示灯，设计保持不变）：
        //   _iconRunning -> 绿灯   仅系统代理
        //   _iconTun     -> 蓝灯   TUN 已开启
        //   _iconStopped -> 红灯   都未开 / 核心未运行
        //   _iconWarn    -> 黄灯   保留资产（旧版"已运行但未接管流量"语义），
        //                          当前状态机不再使用，避免删资源影响历史兼容。
        Icon _iconRunning;
        Icon _iconStopped;
        Icon _iconWarn;
        Icon _iconTun;

        List<ConfigProfile> _profiles = new List<ConfigProfile>();

        enum TunConflictAction
        {
            Cancel,
            Continue,
            StopConflictsAndContinue
        }

        class TunConflictInfo
        {
            public string DisplayName;
            public bool CanStop;
            public int ProcessId;
            public string ProcessName;
        }

        class DefaultRouteCandidate
        {
            public string Name;
            public NetworkInterfaceType NetworkType;
            public int Metric;
        }

        class UpdateRequestRoute
        {
            public string Name;
            public IWebProxy Proxy;
            public string Key;
            public int Attempts;
        }

        class AssetReleaseInfo
        {
            public string TagName;
            public string AssetName;
            public string DownloadUrl;
            public string UpdatedAt;
            public string Digest;
            public long Size;
        }

        class AssetVersionInfo
        {
            public string TagName;
            public string AssetName;
            public string UpdatedAt;
            public string Digest;
            public long Size;
        }

        // ──── P/Invoke for system proxy refresh ────

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_REFRESH = 37;
        const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int SystemProxyCacheMilliseconds = 1500;
        const int AssetApiTimeoutMilliseconds = 45000;
        const int AssetDownloadTimeoutMilliseconds = 180000;
        const int AssetRetryDelayMilliseconds = 800;
        const int AssetProxyProbeTimeoutMilliseconds = 800;
        const int AssetDownloadRouteAttempts = 3;
        const int MihomoStopTimeoutMilliseconds = 8000;
        const int SubscriptionDirectTimeoutMilliseconds = 8000;
        const int SubscriptionProxyTimeoutMilliseconds = 30000;

        static readonly Regex TunEnableRegex = new Regex(
            @"(?m)(^tun:\s*\r?\n(?:[ \t]+[^\r\n]*(?:\r?\n))*?[ \t]+enable:\s*)(true|false)",
            RegexOptions.IgnoreCase);
        static readonly Regex HttpPortRegex = new Regex(@"(?m)^port:\s*(\d+)");
        static readonly Regex MixedPortRegex = new Regex(@"(?m)^mixed-port:\s*(\d+)");
        static readonly Regex SocksPortRegex = new Regex(@"(?m)^socks-port:\s*(\d+)");
        static readonly Regex TunBlockRegex = new Regex(
            @"(?ms)^tun:\s*\r?\n(?<body>(?:^[ \t]+[^\r\n]*(?:\r?\n|$))*)",
            RegexOptions.IgnoreCase);

        // ──── 规则模式（Mode）相关 ────
        // external-controller 支持两种写法：":9090"（仅端口）与 "127.0.0.1:9090"
        static readonly Regex ExternalControllerRegex = new Regex(
            @"(?m)^external-controller:\s*['""]?([^'""\r\n#]+?)['""]?\s*(?:#.*)?$");
        static readonly Regex ControllerSecretRegex = new Regex(
            @"(?m)^secret:\s*['""]?([^'""\r\n#]+?)['""]?\s*(?:#.*)?$");
        // 从 /configs 响应中读取 "mode":"rule"
        static readonly Regex ConfigModeRegex = new Regex(
            @"""mode""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
        static readonly Regex ModeScalarRegex = new Regex(
            @"(?m)^mode:\s*['""]?([A-Za-z]+)['""]?\s*(?:#.*)?$", RegexOptions.IgnoreCase);

        const string ModeRule = "rule";
        const string ModeGlobal = "global";
        const string ModeDirect = "direct";
        const int ControllerApiTimeoutMilliseconds = 4000;

        // ──── Constructor ────

        public MainForm()
        {
            _exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;

            // 数据目录解析：支持"只下载一个 exe 就能跑"。
            //   1) 便携优先：exe 同目录已有 config.yaml / tray-config.json，
            //      说明是完整的绿色目录（也是历史行为），原样沿用，保证向后兼容。
            //   2) 否则回退到 %LOCALAPPDATA%\MihomoTray\，并在首次运行时初始化，
            //      让单文件下载后即可启动，不再硬依赖同目录的附属文件。
            _basePath = ResolveDataDirectory();
            _configPath = Path.Combine(_basePath, "config.yaml");
            _mihomoExePath = Path.Combine(_basePath, "mihomo.exe");
            _trayConfigPath = Path.Combine(_basePath, "tray-config.json");
            _activeConfigPath = _configPath;

            _panelHost = "127.0.0.1";
            _panelPort = 9097;
            _panelPath = "ui";
            _runMihomoOnStartup = false;
            _lastTunEnabled = false;
            _systemProxyDesired = false;
            _systemProxyGuardEnabled = true;
            _pendingEnableTunAfterAdmin = false;
            _loadedLastTunEnabled = false;
            _loadedSystemProxyDesired = false;

            _isAdmin = IsAdministrator();

            // 首次运行初始化：把数据目录补齐到"可启动"的最小状态。
            // 放在 _isAdmin 之后、BuildMenu 之前，确保后续一切都看到一致的文件布局。
            try { EnsureDataDirectoryInitialized(); } catch { }

            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Load += delegate { this.Hide(); };

            _trayIcon = new NotifyIcon();
            _trayIcon.Visible = true;

            _iconRunning = IconFromBase64(EmbeddedIcons.OnBase64);
            _iconStopped = IconFromBase64(EmbeddedIcons.OffBase64);
            _iconWarn = IconFromBase64(EmbeddedIcons.WarnBase64);
            _iconTun = IconFromBase64(EmbeddedIcons.TunBase64);

            BuildMenu();
            LoadTrayConfig();
            ResolveActiveConfig();
            ApplyPendingTunRequest();
            InitializeSavedProxyModes();
            ApplySavedTunMode();
            if (_runMihomoOnStartup)
            {
                StartMihomo();
            }
            // ProxiFyre 依赖本程序的 SOCKS 端口，因此放在 mihomo 之后；
            // 仅在「已有被代理进程」时拉起，避免空配置时无谓启动服务。
            EnsureProxiFyreServiceOnStartup();
            ApplySavedSystemProxyMode();
            CheckMihomoStatus();
            RefreshUI();

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 3000;
            _statusTimer.Tick += delegate
            {
                RefreshUI();
            };
            _statusTimer.Start();

            // 后台状态快照：这是菜单流畅的关键。
            // 首轮由 RefreshUI() 同步采集（启动瞬间一次，可接受），
            // 之后由该计时器周期性异步刷新，UI 线程永不枚举进程。
            _snapshotTimer = new System.Threading.Timer(
                delegate { RequestSnapshotRefresh(); },
                null, SnapshotIntervalMs, SnapshotIntervalMs);

            _systemProxyGuardTimer = new System.Windows.Forms.Timer();
            _systemProxyGuardTimer.Interval = 30000;
            _systemProxyGuardTimer.Tick += delegate
            {
                if (_systemProxyGuardEnabled)
                    EnsureSystemProxy();
                RefreshUI();
            };
            _systemProxyGuardTimer.Start();
        }

        public void Cleanup()
        {
            _runMihomoOnStartup = IsMihomoRunning();
            if (!_pendingEnableTunAfterAdmin)
                _lastTunEnabled = ReadTunStatus();
            try { SaveTrayConfig(); } catch { }
            if (_statusTimer != null) { _statusTimer.Stop(); _statusTimer.Dispose(); _statusTimer = null; }
            if (_snapshotTimer != null) { _snapshotTimer.Dispose(); _snapshotTimer = null; }
            if (_systemProxyGuardTimer != null) { _systemProxyGuardTimer.Stop(); _systemProxyGuardTimer.Dispose(); _systemProxyGuardTimer = null; }
            if (IsMihomoSystemProxyActive())
            {
                try { SetSystemProxy(false); } catch { }
            }
            KillMihomo();
            if (_iconRunning != null) { _iconRunning.Dispose(); _iconRunning = null; }
            if (_iconWarn != null) { _iconWarn.Dispose(); _iconWarn = null; }
            if (_iconStopped != null) { _iconStopped.Dispose(); _iconStopped = null; }
            if (_iconTun != null) { _iconTun.Dispose(); _iconTun = null; }
            if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        }

        // ──── Admin ────

        bool IsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        bool RestartAsAdmin()
        {
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = _exePath;
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
                Application.Exit();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("管理员提权失败: " + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // ──── Build Menu ────

        void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.ShowCheckMargin = false;
            _menu.ShowImageMargin = true;
            UiStyles.ApplyMenu(_menu);

            _statusItem = new ToolStripMenuItem("Mihomo - 已停止");
            _statusItem.Enabled = false;
            _statusItem.Image = UiStyles.MenuIcon("status-off", false);
            _statusItem.Tag = "caption";
            _menu.Items.Add(_statusItem);

            _menu.Items.Add(new ToolStripSeparator());

            var openPanelItem = new ToolStripMenuItem("打开面板", null, OnOpenPanel);
            openPanelItem.Image = UiStyles.MenuIcon("panel", false);
            _menu.Items.Add(openPanelItem);

            _startStopItem = new ToolStripMenuItem("启动 Mihomo", null, OnStartStop);
            _startStopItem.Image = UiStyles.MenuIcon("play", false);
            _menu.Items.Add(_startStopItem);

            _menu.Items.Add(new ToolStripSeparator());

            // 快捷模式切换
            _modeMenu = new ToolStripMenuItem("规则模式");
            _modeMenu.Image = UiStyles.MenuIcon("mode", false);
            _modeRuleItem = new ToolStripMenuItem("规则模式", null,
                delegate { ApplyMode(ModeRule); });
            _modeGlobalItem = new ToolStripMenuItem("全局模式", null,
                delegate { ApplyMode(ModeGlobal); });
            _modeDirectItem = new ToolStripMenuItem("直连模式", null,
                delegate { ApplyMode(ModeDirect); });
            _modeMenu.DropDownItems.Add(_modeRuleItem);
            _modeMenu.DropDownItems.Add(_modeGlobalItem);
            _modeMenu.DropDownItems.Add(_modeDirectItem);
            _menu.Items.Add(_modeMenu);

            _tunItem = new ToolStripMenuItem("TUN 模式", null, OnToggleTun);
            _tunItem.Image = UiStyles.MenuIcon("tun", false);
            _menu.Items.Add(_tunItem);

            _proxyItem = new ToolStripMenuItem("系统代理", null, OnToggleSystemProxy);
            _proxyItem.Image = UiStyles.MenuIcon("proxy", false);
            _proxyItem.Checked = _systemProxyDesired;
            _menu.Items.Add(_proxyItem);

            _proxyGuardItem = new ToolStripMenuItem("系统代理守护", null, OnToggleProxyGuard);
            _proxyGuardItem.Image = UiStyles.MenuIcon("guard", false);
            _proxyGuardItem.Checked = _systemProxyGuardEnabled;
            _menu.Items.Add(_proxyGuardItem);

            _menu.Items.Add(new ToolStripSeparator());

            // 按应用代理（ProxiFyre 联动）
            // 提升到顶层：这是本程序的核心差异化功能之一，原先埋在"工具"子菜单里
            // 导致用户反馈"没看见 ProxiFyre 相关设置"。顶层入口保证可发现性。
            _appProxyMenu = new ToolStripMenuItem("按应用代理（ProxiFyre）");
            _appProxyMenu.Image = UiStyles.MenuIcon("appproxy", false);
            _menu.Items.Add(_appProxyMenu);

            _menu.Items.Add(new ToolStripSeparator());

            var configMenu = new ToolStripMenuItem("配置与订阅");
            configMenu.Image = UiStyles.MenuIcon("profile", false);
            _profileItem = new ToolStripMenuItem("配置切换", null, OnProfileManager);
            _profileItem.Image = UiStyles.MenuIcon("profile", false);
            configMenu.DropDownItems.Add(_profileItem);

            _subMenu = new ToolStripMenuItem("更新订阅");
            _subMenu.Image = UiStyles.MenuIcon("refresh", false);
            configMenu.DropDownItems.Add(_subMenu);

            _subMgrItem = new ToolStripMenuItem("订阅管理", null, OnSubscriptionManager);
            _subMgrItem.Image = UiStyles.MenuIcon("list", false);
            configMenu.DropDownItems.Add(_subMgrItem);

            var editSubItem = new ToolStripMenuItem("编辑订阅源", null, OnEditSubConfig);
            editSubItem.Image = UiStyles.MenuIcon("edit", false);
            configMenu.DropDownItems.Add(editSubItem);
            _menu.Items.Add(configMenu);

            var toolsMenu = new ToolStripMenuItem("工具");
            toolsMenu.Image = UiStyles.MenuIcon("settings", false);

            _panelSettingsItem = new ToolStripMenuItem("面板设置", null, OnPanelSettings);
            _panelSettingsItem.Image = UiStyles.MenuIcon("settings", false);
            toolsMenu.DropDownItems.Add(_panelSettingsItem);

            _updateAssetsItem = new ToolStripMenuItem("更新组件", null, OnUpdateAssets);
            _updateAssetsItem.Image = UiStyles.MenuIcon("download", false);
            toolsMenu.DropDownItems.Add(_updateAssetsItem);
            _menu.Items.Add(toolsMenu);

            _menu.Items.Add(new ToolStripSeparator());

            var startupMenu = new ToolStripMenuItem("启动设置");
            startupMenu.Image = UiStyles.MenuIcon("startup", false);
            _runMihomoItem = new ToolStripMenuItem("启动程序时运行核心", null, OnToggleRunMihomoOnStartup);
            _runMihomoItem.Image = UiStyles.MenuIcon("startup", false);
            _runMihomoItem.Checked = _runMihomoOnStartup;
            startupMenu.DropDownItems.Add(_runMihomoItem);

            _autoStartItem = new ToolStripMenuItem("跟随系统启动", null, OnToggleAutoStart);
            _autoStartItem.Image = UiStyles.MenuIcon("power", false);
            _autoStartItem.Checked = IsAutoStartEnabled();
            startupMenu.DropDownItems.Add(_autoStartItem);
            _menu.Items.Add(startupMenu);

            _menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出", null, OnExit);
            exitItem.Image = UiStyles.MenuIcon("exit", false);
            exitItem.ForeColor = UiStyles.Danger;
            exitItem.Tag = "danger";
            _menu.Items.Add(exitItem);

            _menu.Opening += OnMenuOpening;
            UiStyles.ApplyMenuItems(_menu);

            _trayIcon.ContextMenuStrip = _menu;
        }

        void OnMenuOpening(object sender, CancelEventArgs e)
        {
            // 性能要求：这里必须"零阻塞"。
            // 之前此处会同步做进程枚举 + 文件解析 + TCP 探测，实测合计约 1804ms，
            // 导致菜单延迟弹出、并且因为延时期间鼠标已移动而"跟着鼠标跑"。
            // 现在一律只读后台快照（纯内存字段），如果快照尚未就绪则立刻触发一次
            // 异步刷新并显示"加载中"，绝不在 UI 线程上等结果。
            if (!_snapshotReady)
            {
                RequestSnapshotRefresh();
                ShowMenuLoadingPlaceholder();
                return;
            }

            ApplySnapshotToMenu();
            // 顺带异步刷新一次，让下次打开更"新鲜"，但不阻塞本次
            RequestSnapshotRefresh();
        }

        /// <summary>快照未就绪时的菜单占位，避免显示错误的初始状态。</summary>
        void ShowMenuLoadingPlaceholder()
        {
            _statusItem.Text = "Mihomo - 读取中…";
            _statusItem.Image = UiStyles.MenuIcon("status-off", false);
            if (_appProxyMenu != null)
            {
                _appProxyMenu.DropDownItems.Clear();
                var loading = new ToolStripMenuItem("(读取中…)");
                loading.Enabled = false;
                loading.Image = UiStyles.MenuIcon("empty", false);
                _appProxyMenu.DropDownItems.Add(loading);
                UiStyles.ApplyMenuItems(_appProxyMenu.DropDown);
            }
        }

        // ──── 后台状态快照 ────
        //
        // 把"贵"的状态采集（进程枚举、ProxiFyre 配置解析、端口探测）全部搬到后台线程，
        // UI 线程只读 volatile 字段。这是解决菜单卡顿的关键结构性改动。
        //
        // 刷新节奏：启动时立刻来一次；之后由计时器按 SnapshotIntervalMs 周期性刷新。
        // 菜单打开时若快照已就绪则直接渲染，并触发一次异步刷新以提升下次的新鲜度。

        /// <summary>后台快照刷新间隔。3 秒足够跟上进程启停，又不会造成枚举风暴。</summary>
        const int SnapshotIntervalMs = 3000;

        /// <summary>请求一次后台快照刷新。多次请求会合并，不会并发重入。</summary>
        void RequestSnapshotRefresh()
        {
            if (Interlocked.CompareExchange(ref _snapshotRefreshBusy, 1, 0) != 0)
                return;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    CaptureSnapshot();
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _snapshotRefreshBusy, 0);
                }
            });
        }

        /// <summary>
        /// 在后台线程采集一次完整状态。注意：本方法内不得触碰任何 UI 控件。
        /// </summary>
        void CaptureSnapshot()
        {
            // 1) 核心进程：直接走全量枚举（跳过 CheckMihomoStatus 的 5 秒缓存，
            //    因为本方法本来就只在后台低频执行）。
            bool mihomoRunning;
            try
            {
                var procs = FindManagedMihomoProcesses();
                mihomoRunning = procs.Count > 0;
                foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
            catch { mihomoRunning = false; }

            // 2) TUN / 系统代理：读配置与注册表（相对便宜，但仍放后台）
            bool tunEnabled = false;
            bool proxyOn = false;
            try { tunEnabled = ReadTunStatus(); } catch { }
            try { proxyOn = IsMihomoSystemProxyActive(); } catch { }

            // 3) ProxiFyre 状态
            bool pfInstalled = false;
            bool pfRunning = false;
            var appNames = new List<string>();
            string endpoint = null;
            bool endpointAlive = false;

            try { pfInstalled = IsProxiFyreInstalled(); } catch { }
            if (pfInstalled)
            {
                try { pfRunning = IsProxiFyreRunning(); } catch { }
                try { appNames = ReadProxiFyreAppNames(); } catch { appNames = new List<string>(); }
                try
                {
                    endpoint = ResolveProxiFyreEndpoint();
                    int epPort;
                    endpointAlive = !string.IsNullOrEmpty(endpoint)
                        && TryExtractPort(endpoint, out epPort)
                        && IsLocalPortListening(epPort);
                }
                catch { }
            }

            // 4) 当前规则模式。
            //    这一步在快照线程上做，因为核心运行时它真发一次 GET /configs。
            //    放进快照后，菜单渲染只是读一个 volatile 字段。
            string mode = ModeRule;
            try { mode = ResolveCurrentMode(); } catch { }

            // 5) 开机自启状态（注册表读，放后台免得脏了菜单路径）
            bool autoStart = false;
            try { autoStart = IsAutoStartEnabled(); } catch { }

            // 6) 一次性发布（先写数据，最后置 Ready，保证 UI 读到的是自洽的一组值）
            _snapshotMihomoRunning = mihomoRunning;
            _snapshotTunEnabled = tunEnabled;
            _snapshotSystemProxyOn = proxyOn;
            _snapshotProxiFyreInstalled = pfInstalled;
            _snapshotProxiFyreRunning = pfRunning;
            _snapshotProxiFyreAppNames = appNames;
            _snapshotProxiFyreEndpoint = endpoint;
            _snapshotProxiFyreEndpointAlive = endpointAlive;
            _snapshotMode = mode;
            _snapshotAutoStart = autoStart;
            _snapshotReady = true;
        }

        /// <summary>
        /// 用当前快照渲染整个菜单。纯内存操作，UI 线程上开销在微秒级。
        /// </summary>
        void ApplySnapshotToMenu()
        {
            bool running = _snapshotMihomoRunning;
            bool tunOn = _snapshotTunEnabled;
            bool proxyOn = _snapshotSystemProxyOn;
            string adminTag = _isAdmin ? " [管理员]" : " [普通权限]";

            if (running)
            {
                _startStopItem.Text = "停止 Mihomo";
                _startStopItem.Image = UiStyles.MenuIcon("stop", false);
                _trayIcon.Text = "Mihomo - 运行中"
                    + (tunOn ? " (TUN:开启)" : "")
                    + (proxyOn ? " (代理:开启)" : "")
                    + adminTag;
                if (_statusItem != null)
                {
                    _statusItem.Text = "Mihomo - 运行中" + adminTag;
                    _statusItem.Image = UiStyles.MenuIcon("status-on", false);
                }

                // 托盘灯语义（用户指定）：
                //   TUN 已开启  -> 蓝灯
                //   仅系统代理  -> 绿灯
                //   两者都没开  -> 红灯
                if (tunOn)
                    _trayIcon.Icon = _iconTun;
                else if (proxyOn)
                    _trayIcon.Icon = _iconRunning;
                else
                    _trayIcon.Icon = _iconStopped;
            }
            else
            {
                _startStopItem.Text = "启动 Mihomo";
                _startStopItem.Image = UiStyles.MenuIcon("play", false);
                _trayIcon.Icon = _iconStopped;
                _trayIcon.Text = "Mihomo - 已停止" + adminTag;
                if (_statusItem != null)
                {
                    _statusItem.Text = "Mihomo - 已停止" + adminTag;
                    _statusItem.Image = UiStyles.MenuIcon("status-off", false);
                }
            }

            // 首次运行缺少核心：这是"单文件下载后"最可能的第一个障碍，
            // 必须明说缺什么、放哪里，不能让用户看到"启动失败"却不知所以然。
            if (_firstRunMissingCore && !running)
            {
                _startStopItem.Text = "启动 Mihomo（缺少核心 mihomo.exe）";
                _startStopItem.Tag = "warn";
            }

            _tunItem.Checked = tunOn;
            _tunItem.Text = tunOn ? "TUN 模式已开启" : "TUN 模式";
            _tunItem.Image = UiStyles.MenuIcon("tun", tunOn);

            RefreshModeMenuFromSnapshot(running);

            _proxyItem.Checked = _systemProxyDesired;
            if (_systemProxyDesired && !proxyOn)
                _proxyItem.Text = _systemProxyGuardEnabled
                    ? "系统代理修复中"
                    : "系统代理未指向本程序";
            else
                _proxyItem.Text = proxyOn ? "系统代理已开启" : "系统代理";
            _proxyItem.Image = UiStyles.MenuIcon("proxy", _systemProxyDesired && proxyOn);

            if (_proxyGuardItem != null)
            {
                _proxyGuardItem.Checked = _systemProxyGuardEnabled;
                _proxyGuardItem.Text = "系统代理守护";
                _proxyGuardItem.Image = UiStyles.MenuIcon("guard", _systemProxyGuardEnabled);
            }

            if (_runMihomoItem != null)
            {
                _runMihomoItem.Checked = _runMihomoOnStartup;
                _runMihomoItem.Image = UiStyles.MenuIcon("startup", _runMihomoOnStartup);
            }

            if (_autoStartItem != null)
            {
                _autoStartItem.Checked = _snapshotAutoStart;
                _autoStartItem.Image = UiStyles.MenuIcon("power", _snapshotAutoStart);
            }

            ApplySnapshotToAppProxyMenu();
        }

        /// <summary>用快照渲染规则模式子菜单（避免在 UI 线程再次枚举进程或发 HTTP）。</summary>
        void RefreshModeMenuFromSnapshot(bool running)
        {
            if (_modeMenu == null)
                return;

            // 只读快照；绝不在这里调 ResolveCurrentMode()——
            // 那会在核心运行时发出真实的 GET /configs，端口不通时阻塞约 2 秒。
            string mode = _snapshotMode ?? ModeRule;
            if (_modeRuleItem != null) _modeRuleItem.Checked = mode == ModeRule;
            if (_modeGlobalItem != null) _modeGlobalItem.Checked = mode == ModeGlobal;
            if (_modeDirectItem != null) _modeDirectItem.Checked = mode == ModeDirect;

            _modeMenu.Text = running
                ? "规则模式：" + DescribeMode(mode)
                : "规则模式：" + DescribeMode(mode) + "（未运行）";
            _modeMenu.Image = UiStyles.MenuIcon("mode", mode != ModeRule);
        }

        /// <summary>
        /// 用快照重建「按应用代理」子菜单。纯内存操作，不做任何进程枚举或 I/O。
        /// 这是菜单渲染的唯一入口；真实状态由后台快照线程负责更新。
        /// </summary>
        void ApplySnapshotToAppProxyMenu()
        {
            if (_appProxyMenu == null)
                return;

            _appProxyMenu.DropDownItems.Clear();

            if (!_snapshotProxiFyreInstalled)
            {
                var missing = new ToolStripMenuItem("(未检测到 ProxiFyre)");
                missing.Enabled = false;
                missing.Image = UiStyles.MenuIcon("empty", false);
                _appProxyMenu.DropDownItems.Add(missing);

                // 即便未安装，也把入口留在菜单里，避免用户完全找不到该功能
                _appProxyMenu.DropDownItems.Add(new ToolStripSeparator());
                var hint = new ToolStripMenuItem("ProxiFyre 未安装 · 点此查看说明", null, OnShowProxiFyreHelp);
                hint.Image = UiStyles.MenuIcon("list", false);
                _appProxyMenu.DropDownItems.Add(hint);

                _appProxyMenu.Text = "按应用代理";
                _appProxyMenu.Image = UiStyles.MenuIcon("appproxy", false);
                UiStyles.ApplyMenuItems(_appProxyMenu.DropDown);
                return;
            }

            bool running = _snapshotProxiFyreRunning;
            var names = _snapshotProxiFyreAppNames ?? new List<string>();

            var status = new ToolStripMenuItem(
                running
                    ? string.Format("ProxiFyre 运行中 · {0} 个应用", names.Count)
                    : string.Format("ProxiFyre 已停止 · {0} 个应用", names.Count));
            status.Enabled = false;
            status.Image = UiStyles.MenuIcon(running ? "status-on" : "status-off", running);
            status.Tag = "caption";
            _appProxyMenu.DropDownItems.Add(status);

            string endpoint = _snapshotProxiFyreEndpoint;
            if (!string.IsNullOrEmpty(endpoint))
            {
                bool alive = _snapshotProxiFyreEndpointAlive;
                string suffix = _proxiFyrePortOverride > 0 ? "（已自定义）" : "";
                var ep = new ToolStripMenuItem(
                    "上游 " + endpoint + suffix + (alive ? " · 可用" : " · 未监听"));
                // Enabled=false 会被 MenuTextColor 统一改成 MutedText，警示色就丢了；
                // 因此保持 Enabled=true 但点不动（Tag=caption/warn 让它走弱化配色通道）。
                ep.Tag = alive ? "caption" : "warn";
                ep.Image = UiStyles.MenuIcon(alive ? "status-on" : "status-off", alive);
                _appProxyMenu.DropDownItems.Add(ep);

                if (!alive)
                {
                    var probe = new ToolStripMenuItem("自动检测可用端口…", null, OnAutoDetectProxiFyrePort);
                    probe.Image = UiStyles.MenuIcon("refresh", false);
                    _appProxyMenu.DropDownItems.Add(probe);
                }
            }

            _appProxyMenu.DropDownItems.Add(new ToolStripSeparator());

            if (names.Count == 0)
            {
                var none = new ToolStripMenuItem("(尚未选择应用)");
                none.Enabled = false;
                none.Image = UiStyles.MenuIcon("empty", false);
                _appProxyMenu.DropDownItems.Add(none);
            }
            else
            {
                foreach (var name in names)
                {
                    string captured = name;
                    var item = new ToolStripMenuItem(captured, null,
                        delegate { ToggleProxiFyreApp(captured, false); });
                    item.Checked = true;
                    item.Image = UiStyles.MenuIcon("app", false);
                    _appProxyMenu.DropDownItems.Add(item);
                }
            }

            _appProxyMenu.DropDownItems.Add(new ToolStripSeparator());

            // 总开关：关闭/启动 ProxiFyre 服务本身。
            // 用户明确反馈"缺少关闭选项"——原先只有「停用全部代理」（清空名单+重启服务），
            // 服务仍在运行；这里给出真正把服务停掉的一键开关。
            _appProxyServiceItem = new ToolStripMenuItem(
                running ? "关闭 ProxiFyre（停止服务）" : "启动 ProxiFyre 服务",
                null, OnToggleProxiFyreService);
            _appProxyServiceItem.Image = UiStyles.MenuIcon(running ? "off" : "on", running);
            _appProxyServiceItem.Tag = running ? "danger" : null;
            if (!_isAdmin)
                _appProxyServiceItem.Text += "（需管理员）";
            _appProxyMenu.DropDownItems.Add(_appProxyServiceItem);

            _appProxyMenu.DropDownItems.Add(new ToolStripSeparator());

            _appProxyAllItem = new ToolStripMenuItem("启用全部代理", null, OnToggleAllAppProxy);
            _appProxyAllItem.Image = UiStyles.MenuIcon("appproxy", false);
            _appProxyAllItem.Checked = names.Count > 0;
            _appProxyAllItem.Enabled = _snapshotProxiFyreInstalled;
            _appProxyMenu.DropDownItems.Add(_appProxyAllItem);

            _appProxyMenu.DropDownItems.Add(new ToolStripSeparator());

            var addItem = new ToolStripMenuItem("添加应用…", null, OnAddAppProxy);
            addItem.Image = UiStyles.MenuIcon("plus", false);
            _appProxyMenu.DropDownItems.Add(addItem);

            var manageItem = new ToolStripMenuItem("管理应用列表…", null, OnManageAppProxy);
            manageItem.Image = UiStyles.MenuIcon("list", false);
            _appProxyMenu.DropDownItems.Add(manageItem);

            var portItem = new ToolStripMenuItem(
                string.Format("设置代理端口…（当前 {0}）", ExtractPort(endpoint)),
                null, OnEditProxiFyrePort);
            portItem.Image = UiStyles.MenuIcon("edit", false);
            _appProxyMenu.DropDownItems.Add(portItem);

            var restartItem = new ToolStripMenuItem("重启 ProxiFyre 服务", null, OnRestartAppProxy);
            restartItem.Image = UiStyles.MenuIcon("refresh", false);
            _appProxyMenu.DropDownItems.Add(restartItem);

            _appProxyMenu.Text = running
                ? string.Format("按应用代理（{0}）", names.Count)
                : "按应用代理（停止）";
            _appProxyMenu.Image = UiStyles.MenuIcon("appproxy", running);

            UiStyles.ApplyMenuItems(_appProxyMenu.DropDown);
        }

        /// <summary>ProxiFyre 未安装时的说明入口，保证功能"可发现"。</summary>
        void OnShowProxiFyreHelp(object sender, EventArgs e)
        {
            MessageBox.Show(
                "未在本机检测到 ProxiFyre。\n\n" +
                "「按应用代理」依赖 ProxiFyre（基于 Windows Packet Filter 驱动），" +
                "它可以让指定进程（如游戏）的流量单独走代理，而不影响系统其他程序。\n\n" +
                "预期安装位置：\n" + ProxiFyreDir + "\n\n" +
                "安装后重启本程序，该菜单即会列出可管理的应用。",
                "按应用代理 · 说明",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>从 "host:port" 中取出端口部分；失败时原样返回。</summary>
        static string ExtractPort(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint))
                return "-";
            int i = endpoint.LastIndexOf(':');
            return i >= 0 && i < endpoint.Length - 1 ? endpoint.Substring(i + 1) : endpoint;
        }

        void OnToggleAllAppProxy(object sender, EventArgs e)
        {
            bool currentlyOn = ReadProxiFyreAppNames().Count > 0;
            if (currentlyOn)
                SetAllGameAppsEnabled(false);
            else
                SetAllGameAppsEnabledFromSnapshot();
        }

        /// <summary>
        /// 自动检测本机正在监听的代理端口，并把它应用为 ProxiFyre 的上游。
        /// 适用于外部核心（clash-verge-rev / mihomo-party 等）占用端口的场景。
        /// </summary>
        void OnAutoDetectProxiFyrePort(object sender, EventArgs e)
        {
            var found = new List<int>();
            foreach (int p in CommonProxyPorts)
                if (IsLocalPortListening(p))
                    found.Add(p);

            if (found.Count == 0)
            {
                _trayIcon.ShowBalloonTip(4000, "Mihomo",
                    "未检测到任何正在监听的常见代理端口，" +
                    "请先启动核心，或手动设置端口。", ToolTipIcon.Warning);
                return;
            }

            int picked = found[0];
            ApplyProxiFyrePort(picked,
                string.Format("已自动切换到 {0}", picked) +
                (found.Count > 1 ? "（同时发现：" + string.Join(", ", found.ConvertAll(x => x.ToString()).ToArray()) + "）" : ""));
        }

        /// <summary>把指定端口写成 ProxiFyre 上游并重启服务。</summary>
        void ApplyProxiFyrePort(int port, string successMessage)
        {
            _proxiFyrePortOverride = port;
            SaveTrayConfig();

            string error;
            if (!WriteProxiFyreAppNames(ReadProxiFyreAppNames(), out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "写入 ProxiFyre 配置失败：" + error, ToolTipIcon.Error);
                return;
            }

            if (!RestartProxiFyreService(out error))
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "端口已保存，但重启 ProxiFyreService 失败：" + error, ToolTipIcon.Warning);
            else
                _trayIcon.ShowBalloonTip(2500, "Mihomo", successMessage, ToolTipIcon.Info);

            RefreshUI();
        }

        void OnEditProxiFyrePort(object sender, EventArgs e)
        {
            string current = ResolveProxiFyreEndpoint();
            string input = AppProxyEditForm.Prompt(this, "设置上游 SOCKS5 端口",
                ExtractPort(current));
            if (string.IsNullOrEmpty(input))
                return;

            int port;
            if (!int.TryParse(input.Trim(), out port) || port <= 0 || port > 65535)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo", "端口无效，应为 1-65535", ToolTipIcon.Error);
                return;
            }

            ApplyProxiFyrePort(port, string.Format("上游端口已改为 {0}", port));
        }

        void OnAddAppProxy(object sender, EventArgs e)
        {
            string name = AppProxyEditForm.Prompt(this, "添加应用", "");
            if (string.IsNullOrEmpty(name))
                return;
            ToggleProxiFyreApp(name, true);
        }

        void OnManageAppProxy(object sender, EventArgs e)
        {
            using (var dlg = new AppProxyManagerForm(ReadProxiFyreAppNames(), ResolveProxiFyreEndpoint()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                string error;
                if (!WriteProxiFyreAppNames(dlg.GetAppNames(), out error))
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo", "写入失败：" + error, ToolTipIcon.Error);
                    return;
                }
                if (!RestartProxiFyreService(out error))
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "配置已保存，但重启服务失败：" + error, ToolTipIcon.Warning);
                else
                    _trayIcon.ShowBalloonTip(2000, "Mihomo", "按应用代理列表已更新", ToolTipIcon.Info);
                RefreshUI();
            }
        }

        void OnRestartAppProxy(object sender, EventArgs e)
        {
            string error;
            if (RestartProxiFyreService(out error))
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "ProxiFyre 服务已重启", ToolTipIcon.Info);
            else
                _trayIcon.ShowBalloonTip(3000, "Mihomo", "重启失败：" + error, ToolTipIcon.Warning);
            RefreshUI();
        }

        // ──── Refresh UI ────

        /// <summary>
        /// 重建「更新订阅」子菜单。
        /// 每个订阅是一个二级菜单：点开后可分别「更新」或「应用为当前配置」，
        /// 避免原先「点名字=更新」把两个不同语义的动作混在一个入口上。
        /// </summary>
        void RefreshSubscriptions()
        {
            _subMenu.DropDownItems.Clear();

            var subs = LoadSubscriptions();
            if (subs.Count == 0)
            {
                var empty = new ToolStripMenuItem("(无订阅配置)");
                empty.Enabled = false;
                empty.Image = UiStyles.MenuIcon("empty", false);
                _subMenu.DropDownItems.Add(empty);

                var addHint = new ToolStripMenuItem("添加订阅…", null, OnSubscriptionManager);
                addHint.Image = UiStyles.MenuIcon("plus", false);
                _subMenu.DropDownItems.Add(addHint);
            }
            else
            {
                foreach (var sub in subs)
                {
                    SubscriptionInfo subRef = sub;

                    var parent = new ToolStripMenuItem(
                        BuildSubscriptionLabel(subRef),
                        UiStyles.MenuIcon("refresh", false));

                    var updateOne = new ToolStripMenuItem("更新此订阅", null,
                        delegate { OnUpdateSubscription(subRef); });
                    updateOne.Image = UiStyles.MenuIcon("refresh", false);
                    parent.DropDownItems.Add(updateOne);

                    var switchOne = new ToolStripMenuItem("应用为当前配置", null,
                        delegate { OnApplySubscription(subRef); });
                    switchOne.Image = UiStyles.MenuIcon("profile", false);
                    parent.DropDownItems.Add(switchOne);

                    var copyUrl = new ToolStripMenuItem("复制订阅链接", null,
                        delegate { CopySubscriptionUrl(subRef); });
                    copyUrl.Image = UiStyles.MenuIcon("list", false);
                    parent.DropDownItems.Add(copyUrl);

                    _subMenu.DropDownItems.Add(parent);
                }

                _subMenu.DropDownItems.Add(new ToolStripSeparator());

                var updateAll = new ToolStripMenuItem("更新全部订阅", null, OnUpdateAllSubscriptions);
                updateAll.Image = UiStyles.MenuIcon("download", false);
                _subMenu.DropDownItems.Add(updateAll);

                var manage = new ToolStripMenuItem("订阅管理…", null, OnSubscriptionManager);
                manage.Image = UiStyles.MenuIcon("list", false);
                _subMenu.DropDownItems.Add(manage);
            }

            UiStyles.ApplyMenuItems(_subMenu.DropDown);
        }

        /// <summary>订阅菜单项标题：名称 + 主机名，便于区分同名的不同机场。</summary>
        static string BuildSubscriptionLabel(SubscriptionInfo sub)
        {
            string name = string.IsNullOrEmpty(sub.Name) ? "(未命名)" : sub.Name;
            string host = ExtractUrlHost(sub.Url);
            if (host.Length == 0)
                return name;
            return name + "  —  " + host;
        }

        static string ExtractUrlHost(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "";
            try
            {
                var m = Regex.Match(url, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://([^/?#]+)");
                if (!m.Success)
                    return "";
                string hostPort = m.Groups[1].Value;
                // 去掉 user:pass@ 前缀
                int at = hostPort.LastIndexOf('@');
                if (at >= 0)
                    hostPort = hostPort.Substring(at + 1);
                return hostPort;
            }
            catch
            {
                return "";
            }
        }

        void CopySubscriptionUrl(SubscriptionInfo sub)
        {
            try
            {
                if (string.IsNullOrEmpty(sub.Url))
                {
                    _trayIcon.ShowBalloonTip(2000, "Mihomo", "该订阅没有链接", ToolTipIcon.Warning);
                    return;
                }
                Clipboard.SetText(sub.Url);
                _trayIcon.ShowBalloonTip(1500, "Mihomo", "订阅链接已复制到剪贴板", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "复制失败: " + ex.Message, ToolTipIcon.Error);
            }
        }

        /// <summary>
        /// 把订阅的 proxies 应用到当前活动配置。
        /// 这是「切换」语义：只替换节点，保留活动配置中的端口/规则等本地设置。
        /// </summary>
        void OnApplySubscription(SubscriptionInfo sub)
        {
            if (_isUpdatingSubscription)
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "订阅更新正在进行中，请稍候", ToolTipIcon.Info);
                return;
            }

            _isUpdatingSubscription = true;
            if (_subMgrItem != null) _subMgrItem.Enabled = false;
            if (_subMenu != null) _subMenu.Enabled = false;

            var self = this;
            new Thread((ThreadStart)delegate
            {
                string error = null;
                bool success = false;
                try
                {
                    success = DownloadAndMergeSubscription(sub.Url, sub.Name, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                self.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        if (success)
                        {
                            RestartMihomoQuietly();
                            _trayIcon.ShowBalloonTip(2500, "Mihomo",
                                "已应用订阅 [" + sub.Name + "] 的节点", ToolTipIcon.Info);
                        }
                        else
                        {
                            _trayIcon.ShowBalloonTip(4000, "Mihomo",
                                "应用订阅 [" + sub.Name + "] 失败："
                                + (error == null ? "未知错误" : error),
                                ToolTipIcon.Error);
                        }
                    }
                    finally
                    {
                        _isUpdatingSubscription = false;
                        if (_subMgrItem != null) _subMgrItem.Enabled = true;
                        if (_subMenu != null) _subMenu.Enabled = true;
                        RefreshUI();
                    }
                }));
            }).Start();
        }

        void RefreshUI()
        {
            // 兼容既有调用点：所有"操作后刷新"都走这里。
            // 现在只读快照渲染（零阻塞），并异步请求一次后台刷新，
            // 这样用户操作后界面立刻响应，真实状态在几十毫秒后自动校正。
            if (!_snapshotReady)
            {
                // 快照还没好：同步采集一次，保证启动路径能立刻显示正确状态。
                // 这只在启动瞬间发生一次，不影响菜单交互性能。
                try { CaptureSnapshot(); } catch { }
            }

            ApplySnapshotToMenu();
            RequestSnapshotRefresh();
        }

        /// <summary>同步规则模式子菜单的勾选状态与标题。</summary>
        void RefreshModeMenu()
        {
            RefreshModeMenuFromSnapshot(_snapshotMihomoRunning);
        }

        void InitializeSavedProxyModes()
        {
            if (!_loadedLastTunEnabled)
                _lastTunEnabled = ReadTunStatus();

            if (!_loadedSystemProxyDesired)
                _systemProxyDesired = IsMihomoSystemProxyActive();
        }

        void ApplyPendingTunRequest()
        {
            if (!_pendingEnableTunAfterAdmin)
                return;

            if (_isAdmin)
            {
                _lastTunEnabled = true;
                _loadedLastTunEnabled = true;
                _pendingEnableTunAfterAdmin = false;
                SaveTrayConfig();
            }
        }

        void ApplySavedTunMode()
        {
            try
            {
                if (_lastTunEnabled && !PrepareTunEnableWithConflictHandling())
                {
                    _lastTunEnabled = false;
                    WriteTunStatus(false);
                    SaveTrayConfig();
                }
                else if (ReadTunStatus() != _lastTunEnabled)
                {
                    WriteTunStatus(_lastTunEnabled);
                }
            }
            catch { }
        }

        void ApplySavedSystemProxyMode()
        {
            try
            {
                if (_systemProxyDesired)
                {
                    EnsureSystemProxy();
                }
                else if (IsMihomoSystemProxyActive())
                {
                    SetSystemProxy(false);
                }
            }
            catch { }
        }

        bool PrepareTunEnableWithConflictHandling()
        {
            List<TunConflictInfo> conflicts = FindTunConflictPrograms();
            if (conflicts.Count == 0)
                return PrepareTunConfigForCurrentNetwork();

            TunConflictAction action = ShowTunConflictDialog(conflicts);
            if (action == TunConflictAction.Cancel)
                return false;

            if (action == TunConflictAction.Continue)
                return PrepareTunConfigForCurrentNetwork();

            return StopTunConflictProcesses(conflicts) && PrepareTunConfigForCurrentNetwork();
        }

        bool PrepareTunConfigForCurrentNetwork()
        {
            try
            {
                string interfaceName = GetDefaultPhysicalInterfaceName();
                if (string.IsNullOrEmpty(interfaceName))
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "未找到可用的物理默认网络接口，TUN 模式暂不开启。",
                        ToolTipIcon.Warning);
                    return false;
                }

                if (!PatchTunConfigForInterface(interfaceName))
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "无法写入 TUN 网络接口配置。",
                        ToolTipIcon.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "准备 TUN 配置失败: " + ex.Message,
                    ToolTipIcon.Warning);
                return false;
            }
        }

        TunConflictAction ShowTunConflictDialog(List<TunConflictInfo> conflicts)
        {
            var sb = new StringBuilder();
            foreach (TunConflictInfo conflict in conflicts)
            {
                sb.Append(" - ").Append(conflict.DisplayName);
                if (conflict.CanStop)
                    sb.Append(" (").Append(conflict.ProcessName).Append(", PID ").Append(conflict.ProcessId).Append(")");
                sb.Append("\r\n");
            }

            bool hasStoppableProcess = HasStoppableTunConflicts(conflicts);
            TunConflictAction action = TunConflictAction.Cancel;

            using (var dlg = new Form())
            using (var intro = new Label())
            using (var detail = new TextBox())
            using (var cancelButton = new Button())
            using (var continueButton = new Button())
            using (var stopButton = new Button())
            {
                dlg.Text = "TUN 模式冲突检测";
                UiStyles.ApplyForm(dlg);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(560, 320);

                intro.Text = "检测到可能与 TUN 模式冲突的程序或虚拟网卡。建议先停用冲突项，以免路由异常。";
                intro.ForeColor = UiStyles.MutedText;
                intro.SetBounds(16, 16, 528, 36);

                detail.Multiline = true;
                detail.ReadOnly = true;
                detail.ScrollBars = ScrollBars.Vertical;
                detail.Text = sb.ToString();
                UiStyles.ApplyTextBox(detail);
                detail.SetBounds(16, 58, 528, 176);

                cancelButton.Text = "不开启";
                cancelButton.SetBounds(146, 258, 90, 32);
                cancelButton.Click += delegate
                {
                    action = TunConflictAction.Cancel;
                    dlg.DialogResult = DialogResult.Cancel;
                    dlg.Close();
                };

                continueButton.Text = "开启";
                continueButton.SetBounds(246, 258, 90, 32);
                continueButton.Click += delegate
                {
                    action = TunConflictAction.Continue;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };

                stopButton.Text = hasStoppableProcess ? "停用冲突进程后开启" : "无可停用进程";
                stopButton.Enabled = hasStoppableProcess;
                stopButton.SetBounds(346, 258, 160, 32);
                stopButton.Click += delegate
                {
                    action = TunConflictAction.StopConflictsAndContinue;
                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };

                dlg.Controls.Add(intro);
                dlg.Controls.Add(detail);
                dlg.Controls.Add(cancelButton);
                dlg.Controls.Add(continueButton);
                dlg.Controls.Add(stopButton);
                dlg.CancelButton = cancelButton;
                UiStyles.StyleDialogButtons(continueButton, cancelButton, stopButton);

                dlg.ShowDialog(this);
            }

            return action;
        }

        bool HasStoppableTunConflicts(List<TunConflictInfo> conflicts)
        {
            foreach (TunConflictInfo conflict in conflicts)
            {
                if (conflict.CanStop)
                    return true;
            }
            return false;
        }

        bool StopTunConflictProcesses(List<TunConflictInfo> conflicts)
        {
            var errors = new StringBuilder();
            var stopped = new List<int>();

            foreach (TunConflictInfo conflict in conflicts)
            {
                if (!conflict.CanStop || stopped.Contains(conflict.ProcessId))
                    continue;

                try
                {
                    using (Process process = Process.GetProcessById(conflict.ProcessId))
                    {
                        if (!process.HasExited)
                        {
                            bool closed = false;
                            try
                            {
                                if (process.MainWindowHandle != IntPtr.Zero)
                                    closed = process.CloseMainWindow();
                            }
                            catch { }

                            if (!closed || !process.WaitForExit(3000))
                            {
                                process.Kill();
                                process.WaitForExit(3000);
                            }
                        }
                    }
                    stopped.Add(conflict.ProcessId);
                }
                catch (Exception ex)
                {
                    errors.Append(conflict.DisplayName)
                        .Append(" (PID ")
                        .Append(conflict.ProcessId)
                        .Append("): ")
                        .Append(ex.Message)
                        .Append("\r\n");
                }
            }

            if (errors.Length > 0)
            {
                MessageBox.Show(
                    "以下冲突进程停用失败，TUN 模式暂不开启：\r\n\r\n" + errors,
                    "停用冲突进程失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            System.Threading.Thread.Sleep(500);
            return true;
        }

        string GetDefaultPhysicalInterfaceName()
        {
            try
            {
                string bestInterface = GetBestPhysicalInterfaceName();
                if (!string.IsNullOrEmpty(bestInterface))
                    return bestInterface;

                var candidates = new List<DefaultRouteCandidate>();
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();

                foreach (NetworkInterface adapter in adapters)
                {
                    try
                    {
                        if (adapter.OperationalStatus != OperationalStatus.Up)
                            continue;
                        if (IsVirtualOrConflictingAdapter(adapter))
                            continue;

                        IPInterfaceProperties props = adapter.GetIPProperties();
                        if (props == null || props.GatewayAddresses == null)
                            continue;

                        bool hasIpv4Gateway = false;
                        foreach (GatewayIPAddressInformation gateway in props.GatewayAddresses)
                        {
                            if (gateway != null &&
                                gateway.Address != null &&
                                gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                                !gateway.Address.Equals(IPAddress.Any))
                            {
                                hasIpv4Gateway = true;
                                break;
                            }
                        }

                        if (!hasIpv4Gateway)
                            continue;

                        IPInterfaceProperties ipProps = adapter.GetIPProperties();
                        IPv4InterfaceProperties ipv4 = ipProps == null ? null : ipProps.GetIPv4Properties();
                        candidates.Add(new DefaultRouteCandidate
                        {
                            Name = adapter.Name,
                            NetworkType = adapter.NetworkInterfaceType,
                            Metric = ipv4 == null ? 9999 : ipv4.Index
                        });
                    }
                    catch { }
                }

                candidates.Sort(delegate(DefaultRouteCandidate left, DefaultRouteCandidate right)
                {
                    int scoreCompare = GetAdapterPreferenceScore(left).CompareTo(GetAdapterPreferenceScore(right));
                    if (scoreCompare != 0)
                        return scoreCompare;
                    return left.Metric.CompareTo(right.Metric);
                });

                if (candidates.Count > 0)
                    return candidates[0].Name;
            }
            catch { }

            return "";
        }

        string GetBestPhysicalInterfaceName()
        {
            try
            {
                uint bestIndex;
                uint cloudflareDns = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
                if (GetBestInterface(cloudflareDns, out bestIndex) != 0)
                    return "";

                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface adapter in adapters)
                {
                    try
                    {
                        IPInterfaceProperties props = adapter.GetIPProperties();
                        IPv4InterfaceProperties ipv4 = props == null ? null : props.GetIPv4Properties();
                        if (ipv4 == null || (uint)ipv4.Index != bestIndex)
                            continue;

                        if (adapter.OperationalStatus == OperationalStatus.Up && !IsVirtualOrConflictingAdapter(adapter))
                            return adapter.Name;
                    }
                    catch { }
                }
            }
            catch { }

            return "";
        }

        int GetAdapterPreferenceScore(DefaultRouteCandidate candidate)
        {
            if (candidate.NetworkType == NetworkInterfaceType.Wireless80211)
                return 0;
            if (candidate.NetworkType == NetworkInterfaceType.Ethernet ||
                candidate.NetworkType == NetworkInterfaceType.GigabitEthernet ||
                candidate.NetworkType == NetworkInterfaceType.FastEthernetFx ||
                candidate.NetworkType == NetworkInterfaceType.FastEthernetT)
                return 1;
            return 2;
        }

        bool IsVirtualOrConflictingAdapter(NetworkInterface adapter)
        {
            string text = ((adapter.Name ?? "") + " " + (adapter.Description ?? "")).ToLowerInvariant();
            string[] tokens = new string[]
            {
                "zerotier", "tailscale", "wireguard", "openvpn", "tap-windows", "wintun",
                "hyper-v", "vethernet", "virtual", "vmware", "virtualbox",
                "qmtap", "accelerator", "booster", "netpas", "uubooster", "neteaseuu",
                "leigod", "xunyou", "qiyou"
            };

            foreach (string token in tokens)
            {
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        List<TunConflictInfo> FindTunConflictPrograms()
        {
            var conflicts = new List<TunConflictInfo>();

            AddTunProcessConflict(conflicts, "ZeroTier", new string[] { "zerotier" });
            AddTunProcessConflict(conflicts, "Tailscale", new string[] { "tailscale" });
            AddTunProcessConflict(conflicts, "WireGuard", new string[] { "wireguard" });
            AddTunProcessConflict(conflicts, "OpenVPN", new string[] { "openvpn" });
            AddTunProcessConflict(conflicts, "网易 UU 加速器", new string[] { "uubooster", "uuplugin", "uuaccelerator", "neteaseuu" });
            AddTunProcessConflict(conflicts, "雷神加速器", new string[] { "leigod", "leishen" });
            AddTunProcessConflict(conflicts, "迅游加速器", new string[] { "xunyou" });
            AddTunProcessConflict(conflicts, "奇游加速器", new string[] { "qiyou" });
            AddTunProcessConflict(conflicts, "薄荷加速器", new string[] { "bohe" });
            AddTunProcessConflict(conflicts, "海豚加速器", new string[] { "dolphin" });
            AddTunProcessConflict(conflicts, "迅游/游戏加速器", new string[] { "accelerator", "booster", "netpas" });

            AddTunAdapterConflict(conflicts, "ZeroTier", new string[] { "zerotier" });
            AddTunAdapterConflict(conflicts, "Tailscale", new string[] { "tailscale" });
            AddTunAdapterConflict(conflicts, "WireGuard", new string[] { "wireguard" });
            AddTunAdapterConflict(conflicts, "OpenVPN/TAP", new string[] { "openvpn", "tap-windows" });
            AddTunAdapterConflict(conflicts, "游戏加速器", new string[] { "accelerator", "booster", "netpas", "uubooster", "neteaseuu", "leigod", "xunyou", "qiyou" });

            return conflicts;
        }

        void AddTunProcessConflict(List<TunConflictInfo> conflicts, string displayName, string[] keywords)
        {
            try
            {
                foreach (Process process in Process.GetProcesses())
                {
                    try
                    {
                        string processName = process.ProcessName;
                        if (ContainsAnyKeyword(processName, keywords))
                        {
                            AddUniqueConflict(conflicts, new TunConflictInfo
                            {
                                DisplayName = displayName + " 进程",
                                CanStop = true,
                                ProcessId = process.Id,
                                ProcessName = processName
                            });
                        }
                    }
                    catch { }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        void AddTunAdapterConflict(List<TunConflictInfo> conflicts, string displayName, string[] keywords)
        {
            try
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface adapter in adapters)
                {
                    try
                    {
                        if (adapter.OperationalStatus != OperationalStatus.Up)
                            continue;

                        string adapterText = adapter.Name + " " + adapter.Description;
                        if (ContainsAnyKeyword(adapterText, keywords))
                        {
                            AddUniqueConflict(conflicts, new TunConflictInfo
                            {
                                DisplayName = displayName + " 虚拟网卡: " + adapter.Name,
                                CanStop = false,
                                ProcessId = 0,
                                ProcessName = ""
                            });
                            return;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        bool ContainsAnyKeyword(string value, string[] keywords)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (string keyword in keywords)
            {
                if (value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        void AddUniqueConflict(List<TunConflictInfo> conflicts, TunConflictInfo item)
        {
            foreach (TunConflictInfo conflict in conflicts)
            {
                if (item.CanStop && conflict.CanStop && conflict.ProcessId == item.ProcessId)
                    return;

                if (!item.CanStop && !conflict.CanStop &&
                    string.Equals(conflict.DisplayName, item.DisplayName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            conflicts.Add(item);
        }

        // ──── 数据目录解析（支持单文件运行）────

        /// <summary>
        /// 程序数据目录（存放 config.yaml / tray-config.json / profiles / mihomo.exe）。
        ///
        /// 目标：**只下载一个 exe 就能运行**。
        /// 判定顺序：
        ///   1) exe 同目录已存在 config.yaml 或 tray-config.json
        ///      → 视为「便携/绿色目录」，直接使用，完全保持历史行为与向后兼容。
        ///   2) 否则回退到 %LOCALAPPDATA%\MihomoTray\
        ///      → 单文件放在任意位置（含只读目录、下载文件夹）都能跑起来。
        /// 之所以还要先判断同目录：老用户已经把 mihomo.exe、配置、订阅都放在
        /// 一起了，不能因为引入单文件支持就把他们的数据目录悄悄搬走。
        /// </summary>
        static string ResolveDataDirectory()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1) 便携优先
            try
            {
                if (File.Exists(Path.Combine(exeDir, "config.yaml")) ||
                    File.Exists(Path.Combine(exeDir, "tray-config.json")))
                {
                    return exeDir;
                }
            }
            catch { }

            // 2) 回退到用户数据目录
            try
            {
                string root = Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(root))
                    return Path.Combine(root, "MihomoTray");
            }
            catch { }

            // 3) 极端兜底：仍然用 exe 同目录（保持最基本可用性）
            return exeDir;
        }

        /// <summary>
        /// 首次运行时把数据目录补齐到「可启动」的最小状态。
        /// 幂等：已存在的文件一律不覆盖，避免抹掉用户配置。
        /// </summary>
        void EnsureDataDirectoryInitialized()
        {
            try { Directory.CreateDirectory(_basePath); } catch { return; }

            // 最小可用配置：给出一个能启动的骨架，并明确标注待用户填写的位置。
            // 注意不要写"看起来能用但其实连不上"的假配置，否则用户会以为程序坏了。
            if (!File.Exists(_configPath))
            {
                string skeleton =
                    "# Mihomo 配置（由 MihomoTray 首次运行自动生成）\r\n" +
                    "#\r\n" +
                    "# 请任选其一：\r\n" +
                    "#   1. 用托盘菜单「配置与订阅 → 编辑订阅源」填入订阅链接后更新；\r\n" +
                    "#   2. 直接替换本文件为你的 mihomo 配置。\r\n" +
                    "#\r\n" +
                    "# 另外需要把 mihomo 核心可执行文件放到本目录并命名为 mihomo.exe：\r\n" +
                    "#   " + _mihomoExePath + "\r\n" +
                    "\r\n" +
                    "mixed-port: 7893\r\n" +
                    "socks-port: 7891\r\n" +
                    "allow-lan: false\r\n" +
                    "mode: rule\r\n" +
                    "log-level: info\r\n" +
                    "external-controller: 127.0.0.1:9097\r\n" +
                    "proxy-providers: {}\r\n" +
                    "proxies: []\r\n" +
                    "proxy-groups: []\r\n" +
                    "rules:\r\n" +
                    "  - MATCH,DIRECT\r\n";
                WriteUtf8FileAtomic(_configPath, skeleton);
            }

            // profiles 目录：配置切换器会往里放配置文件
            try { Directory.CreateDirectory(Path.Combine(_basePath, "profiles")); } catch { }

            _firstRunMissingCore = !File.Exists(_mihomoExePath);
        }


        void CheckMihomoStatus()
        {
            if (_mihomoProcess != null)
            {
                try
                {
                    if (_mihomoProcess.HasExited)
                    {
                        _mihomoProcess.Dispose();
                        _mihomoProcess = null;
                    }
                }
                catch
                {
                    _mihomoProcess = null;
                }
            }
            if (_mihomoProcess == null)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastMihomoProcessLookupUtc).TotalSeconds < 5)
                    return;

                _lastMihomoProcessLookupUtc = now;
                _mihomoProcess = FindMihomoProcess();
            }
        }

        bool IsMihomoRunning()
        {
            CheckMihomoStatus();
            return _mihomoProcess != null && !_mihomoProcess.HasExited;
        }

        Process FindMihomoProcess()
        {
            List<Process> processes = FindManagedMihomoProcesses();
            if (processes.Count == 0)
                return null;

            Process first = processes[0];
            for (int i = 1; i < processes.Count; i++)
            {
                try { processes[i].Dispose(); } catch { }
            }
            return first;
        }

        List<Process> FindManagedMihomoProcesses()
        {
            var result = new List<Process>();
            var seen = new List<int>();
            string[] names = { "mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta" };
            foreach (string name in names)
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    bool keep = false;
                    try
                    {
                        if (!p.HasExited && !seen.Contains(p.Id) && IsKnownMihomoProcess(p))
                        {
                            result.Add(p);
                            seen.Add(p.Id);
                            keep = true;
                        }
                    }
                    catch { }

                    if (!keep)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            return result;
        }

        void KillMihomo()
        {
            string details;
            StopManagedMihomoProcesses(MihomoStopTimeoutMilliseconds, out details);
        }

        bool StopManagedMihomoProcesses(int timeoutMilliseconds, out string details)
        {
            details = "";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);

            while (DateTime.UtcNow <= deadline)
            {
                List<Process> processes = FindManagedMihomoProcesses();
                if (processes.Count == 0)
                {
                    ClearMihomoProcessCache();
                    return true;
                }

                foreach (Process process in processes)
                    RequestMihomoProcessExit(process);

                DisposeProcesses(processes);

                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    break;

                System.Threading.Thread.Sleep(Math.Min(250, remaining));
            }

            List<Process> alive = FindManagedMihomoProcesses();
            if (alive.Count == 0)
            {
                ClearMihomoProcessCache();
                return true;
            }

            details = DescribeMihomoProcesses(alive);
            DisposeProcesses(alive);
            ClearMihomoProcessCache();
            return false;
        }

        void RequestMihomoProcessExit(Process process)
        {
            try
            {
                if (process == null || process.HasExited)
                    return;

                bool closed = false;
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        closed = process.CloseMainWindow();
                }
                catch { }

                try
                {
                    if (closed && process.WaitForExit(1500))
                        return;
                }
                catch { }

                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }

                try { process.WaitForExit(1500); } catch { }
            }
            catch { }
        }

        string DescribeMihomoProcesses(List<Process> processes)
        {
            var sb = new StringBuilder();
            foreach (Process process in processes)
            {
                if (sb.Length > 0)
                    sb.Append("; ");

                try
                {
                    sb.Append(process.ProcessName).Append(" PID ").Append(process.Id);
                    string path = GetProcessPath(process);
                    if (!string.IsNullOrEmpty(path))
                        sb.Append(" (").Append(path).Append(")");
                }
                catch
                {
                    sb.Append("PID 未知");
                }
            }
            return sb.ToString();
        }

        string GetProcessPath(Process process)
        {
            try
            {
                string path = process.MainModule.FileName;
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
            catch { }

            return GetProcessImagePath(process);
        }

        string GetProcessImagePath(Process process)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                if (handle == IntPtr.Zero)
                    return "";

                int capacity = 1024;
                var path = new StringBuilder(capacity);
                if (QueryFullProcessImageName(handle, 0, path, ref capacity))
                    return path.ToString();
            }
            catch { }
            finally
            {
                if (handle != IntPtr.Zero)
                    CloseHandle(handle);
            }

            return "";
        }

        void DisposeProcesses(List<Process> processes)
        {
            foreach (Process process in processes)
            {
                try { process.Dispose(); } catch { }
            }
        }

        void ClearMihomoProcessCache()
        {
            _lastMihomoProcessLookupUtc = DateTime.MinValue;

            if (_mihomoProcess != null)
            {
                try { _mihomoProcess.Dispose(); } catch { }
                _mihomoProcess = null;
            }
        }

        bool IsManagedMihomoProcess(Process process)
        {
            try
            {
                if (process == null)
                    return false;

                string processPath = GetProcessPath(process);
                return PathsEqual(processPath, _mihomoExePath) || IsPathInsideBasePath(processPath);
            }
            catch
            {
                return false;
            }
        }

        bool IsKnownMihomoProcess(Process process)
        {
            if (IsCachedMihomoProcess(process))
                return true;

            return IsManagedMihomoProcess(process);
        }

        bool IsCachedMihomoProcess(Process process)
        {
            try
            {
                if (process == null || _mihomoProcess == null)
                    return false;

                return process.Id == _mihomoProcess.Id;
            }
            catch
            {
                return false;
            }
        }

        bool PathsEqual(string left, string right)
        {
            try
            {
                if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                    return false;

                string normalizedLeft = Path.GetFullPath(left).TrimEnd('\\', '/');
                string normalizedRight = Path.GetFullPath(right).TrimEnd('\\', '/');
                return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        bool IsPathInsideBasePath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(_basePath))
                    return false;

                string normalizedPath = Path.GetFullPath(path);
                string normalizedBase = Path.GetFullPath(_basePath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                return normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ──── System Proxy ────

        bool IsSystemProxyEnabled()
        {
            bool enabled;
            string proxyServer;
            if (ReadSystemProxySettings(out enabled, out proxyServer))
                return enabled;
            return false;
        }

        bool IsMihomoSystemProxyActive()
        {
            bool enabled;
            string proxyServer;
            if (!ReadSystemProxySettings(out enabled, out proxyServer))
                return false;
            return enabled && ProxyServerMatchesCore(proxyServer);
        }

        bool IsSystemProxyPointingToMihomo()
        {
            bool enabled;
            string proxyServer;
            if (!ReadSystemProxySettings(out enabled, out proxyServer))
                return false;
            return ProxyServerMatchesCore(proxyServer);
        }

        bool ReadSystemProxySettings(out bool enabled, out string proxyServer)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastSystemProxyReadUtc).TotalMilliseconds < SystemProxyCacheMilliseconds)
            {
                enabled = _cachedSystemProxyEnabled;
                proxyServer = _cachedSystemProxyServer ?? "";
                return _cachedSystemProxyReadOk;
            }

            enabled = false;
            proxyServer = "";
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    if (key == null) return false;
                    var val = key.GetValue("ProxyEnable");
                    int proxyEnable = 0;
                    if (val is int)
                    {
                        proxyEnable = (int)val;
                    }
                    else if (val != null)
                    {
                        int.TryParse(Convert.ToString(val), out proxyEnable);
                    }
                    enabled = proxyEnable == 1;

                    var serverValue = key.GetValue("ProxyServer");
                    if (serverValue != null)
                        proxyServer = Convert.ToString(serverValue);
                    CacheSystemProxySettings(true, enabled, proxyServer);
                    return true;
                }
            }
            catch
            {
                CacheSystemProxySettings(false, false, "");
                return false;
            }
        }

        void CacheSystemProxySettings(bool readOk, bool enabled, string proxyServer)
        {
            _cachedSystemProxyReadOk = readOk;
            _cachedSystemProxyEnabled = enabled;
            _cachedSystemProxyServer = proxyServer ?? "";
            _lastSystemProxyReadUtc = DateTime.UtcNow;
        }

        void InvalidateSystemProxyCache()
        {
            _lastSystemProxyReadUtc = DateTime.MinValue;
        }

        bool ProxyServerMatchesCore(string proxyServer)
        {
            if (string.IsNullOrWhiteSpace(proxyServer))
                return false;

            int httpPort = ReadHttpPort();
            if (proxyServer.IndexOf('=') < 0)
                return ProxyEndpointMatchesCore(proxyServer, httpPort);

            bool checkedAny = false;
            string[] entries = proxyServer.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawEntry in entries)
            {
                string entry = rawEntry.Trim();
                int equalIndex = entry.IndexOf('=');
                if (equalIndex < 0) continue;

                string scheme = entry.Substring(0, equalIndex).Trim().ToLowerInvariant();
                if (scheme != "http" && scheme != "https")
                    continue;

                checkedAny = true;
                if (!ProxyEndpointMatchesCore(entry.Substring(equalIndex + 1), httpPort))
                    return false;
            }

            return checkedAny;
        }

        bool ProxyEndpointMatchesCore(string endpoint, int expectedPort)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            endpoint = endpoint.Trim().Trim('"');

            int schemeIndex = endpoint.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0)
                endpoint = endpoint.Substring(schemeIndex + 3);

            int slashIndex = endpoint.IndexOfAny(new char[] { '/', '\\' });
            if (slashIndex >= 0)
                endpoint = endpoint.Substring(0, slashIndex);

            int atIndex = endpoint.LastIndexOf('@');
            if (atIndex >= 0)
                endpoint = endpoint.Substring(atIndex + 1);

            string host;
            string portText;

            if (endpoint.StartsWith("[", StringComparison.Ordinal))
            {
                int closeIndex = endpoint.IndexOf(']');
                if (closeIndex < 0 || endpoint.Length <= closeIndex + 2 || endpoint[closeIndex + 1] != ':')
                    return false;
                host = endpoint.Substring(1, closeIndex - 1);
                portText = endpoint.Substring(closeIndex + 2);
            }
            else
            {
                int colonIndex = endpoint.LastIndexOf(':');
                if (colonIndex <= 0 || colonIndex >= endpoint.Length - 1)
                    return false;
                host = endpoint.Substring(0, colonIndex);
                portText = endpoint.Substring(colonIndex + 1);
            }

            int port;
            if (!int.TryParse(portText, out port))
                return false;

            host = host.Trim().Trim('[', ']').ToLowerInvariant();
            return port == expectedPort &&
                (host == "localhost" || host == "127.0.0.1" || host == "::1");
        }

        void EnsureSystemProxy()
        {
            if (!_systemProxyDesired || !_systemProxyGuardEnabled)
                return;

            bool enabled;
            string proxyServer;
            if (!ReadSystemProxySettings(out enabled, out proxyServer) ||
                !enabled ||
                !ProxyServerMatchesCore(proxyServer))
            {
                SetSystemProxy(true);
            }
        }

        void SetSystemProxy(bool enable)
        {
            try
            {
                int httpPort = ReadHttpPort();
                string proxyAddr = "localhost:" + httpPort;

                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true))
                {
                    if (key == null) return;

                    if (enable)
                    {
                        InvalidateSystemProxyCache();
                        bool currentlyEnabled;
                        string currentServer;
                        if (ReadSystemProxySettings(out currentlyEnabled, out currentServer) &&
                            currentlyEnabled &&
                            ProxyServerMatchesCore(currentServer))
                        {
                            return;
                        }
                        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                        key.SetValue("ProxyServer", proxyAddr, RegistryValueKind.String);
                        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
                    }
                    else
                    {
                        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    }
                }

                NotifyProxyChanged();
                InvalidateSystemProxyCache();
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "修改系统代理失败: " + ex.Message,
                    ToolTipIcon.Error);
            }
        }

        void NotifyProxyChanged()
        {
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            catch { }
        }

        int ReadHttpPort()
        {
            try
            {
                string content = ReadActiveConfigContent();
                if (string.IsNullOrEmpty(content)) return 7890;
                var match = HttpPortRegex.Match(content);
                if (match.Success)
                {
                    int port;
                    if (int.TryParse(match.Groups[1].Value, out port) && port > 0 && port <= 65535)
                        return port;
                }
            }
            catch { }
            return 7890;
        }

        // ──── Auto Start ────

        bool IsAutoStartEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    var value = key.GetValue("MihomoTray") as string;
                    return value != null && value == _exePath;
                }
            }
            catch { return false; }
        }

        void SetAutoStart(bool enable)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        key.SetValue("MihomoTray", _exePath);
                    }
                    else
                    {
                        key.DeleteValue("MihomoTray", false);
                    }
                }
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "修改自启失败: " + ex.Message,
                    ToolTipIcon.Error);
            }
        }

        // ──── Event Handlers ────

        void OnStartStop(object sender, EventArgs e)
        {
            _lastMihomoProcessLookupUtc = DateTime.MinValue;
            if (IsMihomoRunning())
            {
                StopMihomo();
            }
            else
            {
                StartMihomo();
            }
            RefreshUI();
        }

        void OnToggleTun(object sender, EventArgs e)
        {
            bool current = ReadTunStatus();
            bool newState = !current;

            if (newState && !_isAdmin)
            {
                var result = MessageBox.Show(
                    "TUN 模式需要管理员权限才能运行。\n\n" +
                    "是否以管理员身份重启本程序？\n" +
                    "（重启后请再次开启 TUN 模式）",
                    "需要管理员权限",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (result == DialogResult.Yes)
                {
                    _pendingEnableTunAfterAdmin = true;
                    _lastTunEnabled = true;
                    SaveTrayConfig();

                    if (!RestartAsAdmin())
                    {
                        _pendingEnableTunAfterAdmin = false;
                        _lastTunEnabled = ReadTunStatus();
                        SaveTrayConfig();
                    }
                }
                return;
            }

            if (newState && !PrepareTunEnableWithConflictHandling())
            {
                return;
            }

            if (WriteTunStatus(newState))
            {
                _lastTunEnabled = newState;
                SaveTrayConfig();

                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    newState ? "TUN 模式已开启，正在重启..." : "TUN 模式已关闭，正在重启...",
                    ToolTipIcon.Info);

                if (IsMihomoRunning())
                {
                    StopMihomo();
                    System.Threading.Thread.Sleep(500);
                    StartMihomo();
                }
            }
            else
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    "无法修改配置文件",
                    ToolTipIcon.Error);
            }
            RefreshUI();
        }

        void OnToggleSystemProxy(object sender, EventArgs e)
        {
            bool current = _systemProxyDesired;
            bool newState = !current;

            _systemProxyDesired = newState;
            if (newState)
            {
                SetSystemProxy(true);
            }
            else if (IsMihomoSystemProxyActive())
            {
                SetSystemProxy(false);
            }
            SaveTrayConfig();

            int httpPort = ReadHttpPort();
            _trayIcon.ShowBalloonTip(2000, "Mihomo",
                newState ? "系统代理已开启: localhost:" + httpPort : "系统代理已关闭",
                ToolTipIcon.Info);

            RefreshUI();
        }

        void OnToggleProxyGuard(object sender, EventArgs e)
        {
            _systemProxyGuardEnabled = !_systemProxyGuardEnabled;
            if (_proxyGuardItem != null)
                _proxyGuardItem.Checked = _systemProxyGuardEnabled;

            SaveTrayConfig();

            if (_systemProxyGuardEnabled)
                EnsureSystemProxy();

            _trayIcon.ShowBalloonTip(2000, "Mihomo",
                _systemProxyGuardEnabled ? "系统代理守护已开启" : "系统代理守护已关闭",
                ToolTipIcon.Info);

            RefreshUI();
        }

        void OnOpenPanel(object sender, EventArgs e)
        {
            try
            {
                string url = BuildPanelUrl();
                Process.Start(url);
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "无法打开浏览器: " + ex.Message,
                    ToolTipIcon.Error);
            }
        }

        void OnPanelSettings(object sender, EventArgs e)
        {
            using (var dlg = new PanelSettingsForm(_panelHost, _panelPort, _panelPath))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                _panelHost = dlg.PanelHost;
                _panelPort = dlg.PanelPort;
                _panelPath = dlg.PanelPath;
                SaveTrayConfig();
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    "面板地址已更新: " + BuildPanelUrl(),
                    ToolTipIcon.Info);
            }
        }

        void OnProfileManager(object sender, EventArgs e)
        {
            using (var dlg = new ConfigProfileManagerForm(_profiles, _activeConfigPath, _basePath))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                string previousActiveConfigPath = _activeConfigPath;
                bool activeChanged;
                _lastMihomoProcessLookupUtc = DateTime.MinValue;
                bool wasRunning = IsMihomoRunning();

                _profiles = dlg.GetProfiles();
                _activeConfigPath = dlg.GetActiveConfigPath();
                ResolveActiveConfig();
                activeChanged = !PathsEqual(previousActiveConfigPath, _activeConfigPath);

                ApplySavedTunMode();
                SaveTrayConfig();

                if (wasRunning && activeChanged)
                {
                    StopMihomo();
                    System.Threading.Thread.Sleep(500);
                    StartMihomo();
                }

                ApplySavedSystemProxyMode();
                RefreshUI();

                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    activeChanged ? "已切换到: " + Path.GetFileName(_activeConfigPath) : "配置列表已更新",
                    ToolTipIcon.Info);
            }
        }

        void OnToggleRunMihomoOnStartup(object sender, EventArgs e)
        {
            _runMihomoOnStartup = !_runMihomoOnStartup;
            if (_runMihomoItem != null)
            {
                _runMihomoItem.Checked = _runMihomoOnStartup;
            }
            SaveTrayConfig();
            _trayIcon.ShowBalloonTip(2000, "Mihomo",
                _runMihomoOnStartup ? "启动时自动运行 Mihomo 已开启" : "启动时自动运行 Mihomo 已关闭",
                ToolTipIcon.Info);
        }

        void OnUpdateAssets(object sender, EventArgs e)
        {
            if (_isUpdatingAssets)
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "组件更新正在进行中", ToolTipIcon.Info);
                return;
            }

            List<AssetUpdateOption> options;
            using (var dlg = new AssetUpdateForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                options = dlg.GetOptions();
            }

            if (options.Count == 0)
                return;

            _isUpdatingAssets = true;
            if (_updateAssetsItem != null)
                _updateAssetsItem.Enabled = false;

            var self = this;
            new System.Threading.Thread((System.Threading.ThreadStart)delegate
            {
                bool changedCore = false;
                bool coreSelected = false;
                bool coreStopped = false;
                bool wasRunning = false;
                int skipped = 0;
                int changed = 0;
                var errors = new StringBuilder();
                var apiCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    SortAssetUpdateOptions(options);
                    foreach (var opt in options)
                    {
                        try
                        {
                            string apiJson;
                            if (!apiCache.TryGetValue(opt.ApiUrl, out apiJson))
                            {
                                apiJson = DownloadString(opt.ApiUrl);
                                apiCache[opt.ApiUrl] = apiJson;
                            }
                            if (string.IsNullOrEmpty(apiJson))
                                throw new Exception("API 返回空");

                            if (apiJson.Contains("\"message\""))
                                throw new Exception(ExtractApiError(apiJson));

                            AssetReleaseInfo releaseInfo = FindAssetReleaseInfo(apiJson, opt);
                            if (releaseInfo == null || string.IsNullOrEmpty(releaseInfo.DownloadUrl))
                                throw new Exception("未找到匹配的下载地址");

                            string skipReason;
                            if (IsAssetAlreadyCurrent(opt, releaseInfo, out skipReason))
                            {
                                skipped++;
                                continue;
                            }

                            Action beforeReplace = null;
                            if (opt.IsCore)
                            {
                                if (errors.Length > 0)
                                    throw new Exception("前置组件更新失败，已跳过核心替换以避免停止运行中的核心");

                                coreSelected = true;
                                beforeReplace = delegate
                                {
                                    if (coreStopped)
                                        return;

                                    bool stoppedForReplace = true;
                                    string stopDetails = "";
                                    self.Invoke(new Action(delegate
                                    {
                                        _lastMihomoProcessLookupUtc = DateTime.MinValue;
                                        wasRunning = IsMihomoRunning();
                                        if (wasRunning)
                                        {
                                            stoppedForReplace = StopManagedMihomoProcesses(MihomoStopTimeoutMilliseconds, out stopDetails);
                                        }
                                    }));

                                    if (!stoppedForReplace)
                                    {
                                        string message = "mihomo.exe 仍在运行，无法替换核心";
                                        if (!string.IsNullOrEmpty(stopDetails))
                                            message += ": " + stopDetails;
                                        throw new Exception(message);
                                    }

                                    coreStopped = wasRunning;
                                    if (wasRunning)
                                        System.Threading.Thread.Sleep(800);
                                };
                            }

                            DownloadFile(releaseInfo.DownloadUrl, opt.IsZip || opt.IsGz, opt.IsGz, opt.TargetPath, beforeReplace);
                            RecordAssetVersion(opt, releaseInfo);
                            changed++;
                            if (opt.IsCore)
                                changedCore = true;
                        }
                        catch (Exception ex)
                        {
                            errors.Append(opt.DisplayName).Append(": ").Append(ex.Message).Append("\r\n");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Append("Update flow: ").Append(ex.Message).Append("\r\n");
                }

                Action finish = delegate
                {
                    FinishAssetUpdate(coreSelected, coreStopped, wasRunning, changedCore, changed, skipped, errors);
                };

                try
                {
                    if (!self.IsDisposed && self.IsHandleCreated)
                        self.BeginInvoke(finish);
                    else
                        ResetAssetUpdateState();
                }
                catch
                {
                    ResetAssetUpdateState();
                }
            })
            { IsBackground = true }.Start();
        }

        void FinishAssetUpdate(bool coreSelected, bool coreStopped, bool wasRunning, bool changedCore, int changed, int skipped, StringBuilder errors)
        {
            try
            {
                if (coreSelected && coreStopped && wasRunning)
                    StartMihomo();

                ApplySavedSystemProxyMode();

                if (errors.Length > 0)
                    MessageBox.Show(errors.ToString(), "部分更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                {
                    string message;
                    if (changed == 0 && skipped > 0)
                        message = "组件已是最新版";
                    else if (skipped > 0)
                        message = (changedCore ? "核心和组件更新完成" : "组件更新完成") + "，已跳过 " + skipped + " 项最新版";
                    else
                        message = changedCore ? "核心和组件更新完成" : "组件更新完成";

                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        message,
                        ToolTipIcon.Info);
                }

                RefreshUI();
            }
            finally
            {
                ResetAssetUpdateState();
            }
        }

        void ResetAssetUpdateState()
        {
            _isUpdatingAssets = false;
            try
            {
                if (_updateAssetsItem != null)
                    _updateAssetsItem.Enabled = true;
            }
            catch { }
        }

        string ExtractApiError(string json)
        {
            var m = Regex.Match(json, @"""message""\s*:\s*""([^""]+)""");
            if (m.Success) return m.Groups[1].Value;
            return "API error (unknown)";
        }

        void SortAssetUpdateOptions(List<AssetUpdateOption> options)
        {
            options.Sort(delegate(AssetUpdateOption left, AssetUpdateOption right)
            {
                if (left.IsCore == right.IsCore)
                    return 0;
                return left.IsCore ? 1 : -1;
            });
        }

        bool IsAssetAlreadyCurrent(AssetUpdateOption opt, AssetReleaseInfo releaseInfo, out string reason)
        {
            reason = "";
            if (opt == null || releaseInfo == null || string.IsNullOrEmpty(opt.TargetPath) || !File.Exists(opt.TargetPath))
                return false;

            if (opt.IsCore)
            {
                string localVersion = ReadLocalMihomoVersion();
                string latestVersion = NormalizeMihomoVersion(releaseInfo.TagName);
                if (!string.IsNullOrEmpty(localVersion) &&
                    !string.IsNullOrEmpty(latestVersion) &&
                    string.Equals(localVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "本地内核已是 " + latestVersion;
                    return true;
                }
            }

            string expectedSha256 = ExtractSha256Digest(releaseInfo.Digest);
            if (!string.IsNullOrEmpty(expectedSha256) && !opt.IsZip && !opt.IsGz)
            {
                try
                {
                    string localSha256 = ComputeFileSha256(opt.TargetPath);
                    if (string.Equals(localSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = "本地文件哈希一致";
                        return true;
                    }
                }
                catch { }
            }

            AssetVersionInfo recorded;
            if (TryGetRecordedAssetVersion(opt, out recorded) && AssetVersionMatches(recorded, releaseInfo))
            {
                if (opt.IsZip || opt.IsGz)
                {
                    reason = "本地记录已是最新版";
                    return true;
                }

                try
                {
                    long localLength = new FileInfo(opt.TargetPath).Length;
                    if (releaseInfo.Size <= 0 || localLength == releaseInfo.Size)
                    {
                        reason = "本地记录已是最新版";
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        bool TryGetRecordedAssetVersion(AssetUpdateOption opt, out AssetVersionInfo info)
        {
            info = null;
            if (_assetVersions == null)
                return false;

            return _assetVersions.TryGetValue(GetAssetVersionKey(opt), out info);
        }

        bool AssetVersionMatches(AssetVersionInfo recorded, AssetReleaseInfo releaseInfo)
        {
            if (recorded == null || releaseInfo == null)
                return false;

            return string.Equals(recorded.TagName ?? "", releaseInfo.TagName ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(recorded.AssetName ?? "", releaseInfo.AssetName ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(recorded.UpdatedAt ?? "", releaseInfo.UpdatedAt ?? "", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(recorded.Digest ?? "", releaseInfo.Digest ?? "", StringComparison.OrdinalIgnoreCase) &&
                (recorded.Size <= 0 || releaseInfo.Size <= 0 || recorded.Size == releaseInfo.Size);
        }

        void RecordAssetVersion(AssetUpdateOption opt, AssetReleaseInfo releaseInfo)
        {
            if (opt == null || releaseInfo == null)
                return;

            if (_assetVersions == null)
                _assetVersions = new Dictionary<string, AssetVersionInfo>(StringComparer.OrdinalIgnoreCase);

            _assetVersions[GetAssetVersionKey(opt)] = new AssetVersionInfo
            {
                TagName = releaseInfo.TagName ?? "",
                AssetName = releaseInfo.AssetName ?? opt.AssetName ?? "",
                UpdatedAt = releaseInfo.UpdatedAt ?? "",
                Digest = releaseInfo.Digest ?? "",
                Size = releaseInfo.Size
            };

            try { SaveTrayConfig(); } catch { }
        }

        string GetAssetVersionKey(AssetUpdateOption opt)
        {
            if (opt == null)
                return "";

            if (!string.IsNullOrEmpty(opt.AssetName))
                return opt.AssetName.ToLowerInvariant();

            if (!string.IsNullOrEmpty(opt.DisplayName))
                return opt.DisplayName.ToLowerInvariant();

            return NormalizeRelativePath(opt.TargetPath).ToLowerInvariant();
        }

        string ReadLocalMihomoVersion()
        {
            if (!File.Exists(_mihomoExePath))
                return "";

            Process process = null;
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = _mihomoExePath;
                psi.Arguments = "-v";
                psi.WorkingDirectory = _basePath;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                process = Process.Start(psi);
                if (process == null)
                    return "";

                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    return "";
                }

                string output = "";
                try { output += process.StandardOutput.ReadToEnd(); } catch { }
                try { output += "\n" + process.StandardError.ReadToEnd(); } catch { }
                return NormalizeMihomoVersion(output);
            }
            catch
            {
                return "";
            }
            finally
            {
                if (process != null)
                {
                    try { process.Dispose(); } catch { }
                }
            }
        }

        string NormalizeMihomoVersion(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            Match match = Regex.Match(text, @"v?\d+(?:\.\d+)+", RegexOptions.IgnoreCase);
            if (!match.Success)
                return text.Trim();

            string version = match.Value.Trim();
            if (!version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                version = "v" + version;
            return version.ToLowerInvariant();
        }

        string ExtractSha256Digest(string digest)
        {
            if (string.IsNullOrWhiteSpace(digest))
                return "";

            Match match = Regex.Match(digest, @"sha256:([0-9a-fA-F]{64})");
            if (!match.Success)
                return "";

            return match.Groups[1].Value.ToLowerInvariant();
        }

        string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        void OnSubscriptionManager(object sender, EventArgs e)
        {
            using (var dlg = new SubscriptionManagerForm(new List<SubscriptionInfo>(subs)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                subs = dlg.GetSubscriptions();
                SaveTrayConfig();
                RefreshSubscriptions();
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    "订阅列表已更新",
                    ToolTipIcon.Info);
            }
        }

        void OnToggleAutoStart(object sender, EventArgs e)
        {
            bool current = IsAutoStartEnabled();
            bool newState = !current;
            SetAutoStart(newState);
            _autoStartItem.Checked = IsAutoStartEnabled();
            _trayIcon.ShowBalloonTip(2000, "Mihomo",
                newState ? "已启用开机自启" : "已关闭开机自启",
                ToolTipIcon.Info);
        }

        void OnRestartAsAdmin(object sender, EventArgs e)
        {
            RestartAsAdmin();
        }

        /// <summary>
        /// 更新单个订阅。
        /// 关键约束：并发更新会同时写同一个活动配置并争抢核心重启，因此用标志位串行化；
        /// 且「全部更新」必须只重启一次核心，而不是每个订阅各重启一次。
        /// </summary>
        void OnUpdateSubscription(SubscriptionInfo sub)
        {
            UpdateSubscriptions(new List<SubscriptionInfo> { sub });
        }

        void OnUpdateAllSubscriptions(object sender, EventArgs e)
        {
            var list = LoadSubscriptions();
            if (list.Count == 0)
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "没有可更新的订阅", ToolTipIcon.Warning);
                return;
            }
            UpdateSubscriptions(list);
        }

        /// <summary>
        /// 顺序更新一批订阅，结束后统一重启一次核心。
        /// 串行而非并行：多个订阅会合并进同一个活动配置，并行写必然互相覆盖。
        /// </summary>
        void UpdateSubscriptions(List<SubscriptionInfo> list)
        {
            if (_isUpdatingSubscription)
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "订阅更新正在进行中，请稍候", ToolTipIcon.Info);
                return;
            }
            if (list == null || list.Count == 0)
                return;

            _isUpdatingSubscription = true;
            if (_subMgrItem != null) _subMgrItem.Enabled = false;
            if (_subMenu != null) _subMenu.Enabled = false;

            var self = this;
            new Thread((ThreadStart)delegate
            {
                var ok = new List<string>();
                var failed = new List<string>();
                string lastError = null;

                for (int i = 0; i < list.Count; i++)
                {
                    SubscriptionInfo subRef = list[i];
                    int seq = i + 1;
                    int total = list.Count;

                    try
                    {
                        self.BeginInvoke(new Action(delegate
                        {
                            _trayIcon.ShowBalloonTip(1000, "Mihomo",
                                total > 1
                                    ? string.Format("正在更新订阅 ({0}/{1}): {2}", seq, total, subRef.Name)
                                    : "正在更新订阅: " + subRef.Name + " ...",
                                ToolTipIcon.Info);
                        }));

                        string error;
                        if (DownloadAndMergeSubscription(subRef.Url, subRef.Name, out error))
                            ok.Add(subRef.Name);
                        else
                        {
                            failed.Add(subRef.Name);
                            lastError = error;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add(subRef.Name);
                        lastError = ex.Message;
                    }
                }

                self.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        // 只要有一个订阅成功，就要让核心重新加载合并后的配置
                        if (ok.Count > 0)
                        {
                            RestartMihomoQuietly();
                        }

                        if (failed.Count == 0)
                        {
                            _trayIcon.ShowBalloonTip(2500, "Mihomo",
                                ok.Count == 1
                                    ? "订阅 [" + ok[0] + "] 更新成功"
                                    : string.Format("{0} 个订阅全部更新成功", ok.Count),
                                ToolTipIcon.Info);
                        }
                        else if (ok.Count == 0)
                        {
                            _trayIcon.ShowBalloonTip(4000, "Mihomo",
                                failed.Count == 1
                                    ? "订阅 [" + failed[0] + "] 更新失败："
                                      + (lastError == null ? "未知错误" : lastError)
                                    : string.Format("{0} 个订阅全部更新失败：{1}", failed.Count,
                                        lastError == null ? "未知错误" : lastError),
                                ToolTipIcon.Error);
                        }
                        else
                        {
                            _trayIcon.ShowBalloonTip(4000, "Mihomo",
                                string.Format("更新完成：成功 {0} 个，失败 {1} 个（{2}）",
                                    ok.Count, failed.Count, string.Join("、", failed.ToArray())),
                                ToolTipIcon.Warning);
                        }
                    }
                    finally
                    {
                        _isUpdatingSubscription = false;
                        if (_subMgrItem != null) _subMgrItem.Enabled = true;
                        if (_subMenu != null) _subMenu.Enabled = true;
                        RefreshUI();
                    }
                }));
            }).Start();
        }

        /// <summary>静默重启核心（保留「原本是否在运行」的语义，不弹启动提示）。</summary>
        void RestartMihomoQuietly()
        {
            try
            {
                if (!IsMihomoRunning())
                    return;
                _suppressMihomoBalloon = true;
                StopMihomo();
                Thread.Sleep(500);
                StartMihomo();
            }
            catch { }
            finally
            {
                _suppressMihomoBalloon = false;
            }
        }

        void OnEditSubConfig(object sender, EventArgs e)
        {
            if (!File.Exists(_trayConfigPath))
            {
                SaveDefaultSubscriptions();
            }
            try
            {
                Process.Start("notepad.exe", "\"" + _trayConfigPath + "\"");
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    "无法打开配置文件: " + ex.Message,
                    ToolTipIcon.Error);
            }
        }

        void OnExit(object sender, EventArgs e)
        {
            Application.Exit();
        }

        // ──── Process Management ────

        void StartMihomo()
        {
            try
            {
                if (!File.Exists(_mihomoExePath))
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "找不到 mihomo.exe: " + _mihomoExePath,
                        ToolTipIcon.Error);
                    return;
                }

                string activePath = _activeConfigPath;
                if (!File.Exists(activePath)) activePath = _configPath;
                string workDir = Path.GetDirectoryName(activePath);
                if (string.IsNullOrEmpty(workDir))
                {
                    workDir = _basePath.TrimEnd('\\');
                }
                bool tunOn = ReadTunStatus();
                bool needElevation = tunOn && !_isAdmin;

                if (needElevation)
                {
                    var psi = new ProcessStartInfo();
                    psi.FileName = _mihomoExePath;
                    psi.Arguments = "-d \"" + workDir + "\"";
                    psi.WorkingDirectory = workDir;
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    _mihomoProcess = Process.Start(psi);
                }
                else
                {
                    var psi = new ProcessStartInfo();
                    psi.FileName = _mihomoExePath;
                    psi.Arguments = "-d \"" + workDir + "\"";
                    psi.WorkingDirectory = workDir;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    _mihomoProcess = Process.Start(psi);
                }

                string msg = "已启动";
                if (tunOn) msg += " (TUN 模式)";
                if (needElevation) msg += "\n已通过管理员权限启动";
                if (!_suppressMihomoBalloon)
                    _trayIcon.ShowBalloonTip(2000, "Mihomo", msg, ToolTipIcon.Info);

                RefreshUI();
            }
            catch (Win32Exception)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "用户取消了管理员提权",
                    ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "启动失败: " + ex.Message,
                    ToolTipIcon.Error);
            }
        }

        void StopMihomo()
        {
            try
            {
                string details;
                if (StopManagedMihomoProcesses(MihomoStopTimeoutMilliseconds, out details))
                {
                    if (!_suppressMihomoBalloon)
                        _trayIcon.ShowBalloonTip(2000, "Mihomo", "已停止", ToolTipIcon.Info);
                }
                else
                {
                    string message = "仍有 mihomo.exe 在运行";
                    if (!string.IsNullOrEmpty(details))
                        message += ": " + details;
                    _trayIcon.ShowBalloonTip(3000, "Mihomo", message, ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "停止失败: " + ex.Message, ToolTipIcon.Error);
            }
        }

        // ──── Config YAML Helpers ────

        string GetActiveConfigPath()
        {
            string configPath = _activeConfigPath;
            if (!File.Exists(configPath)) configPath = _configPath;
            if (!File.Exists(configPath)) return null;
            return configPath;
        }

        string ReadActiveConfigContent()
        {
            string configPath = GetActiveConfigPath();
            if (string.IsNullOrEmpty(configPath))
                return null;

            var info = new FileInfo(configPath);
            if (_cachedConfigContent != null &&
                string.Equals(_cachedConfigPath, configPath, StringComparison.OrdinalIgnoreCase) &&
                _cachedConfigWriteTimeUtc == info.LastWriteTimeUtc &&
                _cachedConfigLength == info.Length)
            {
                return _cachedConfigContent;
            }

            string content = File.ReadAllText(configPath, Encoding.UTF8);
            _cachedConfigPath = configPath;
            _cachedConfigContent = content;
            _cachedConfigWriteTimeUtc = info.LastWriteTimeUtc;
            _cachedConfigLength = info.Length;
            return content;
        }

        void UpdateActiveConfigCache(string configPath, string content)
        {
            try
            {
                var info = new FileInfo(configPath);
                _cachedConfigPath = configPath;
                _cachedConfigContent = content;
                _cachedConfigWriteTimeUtc = info.LastWriteTimeUtc;
                _cachedConfigLength = info.Length;
            }
            catch
            {
                InvalidateActiveConfigCache();
            }
        }

        void InvalidateActiveConfigCache()
        {
            _cachedConfigPath = null;
            _cachedConfigContent = null;
            _cachedConfigWriteTimeUtc = DateTime.MinValue;
            _cachedConfigLength = 0;
        }

        // ──── mihomo External Controller API ────
        // 通过 mihomo 的 RESTful API 热切换规则模式，避免「改 YAML + 重启核心」导致连接中断。

        /// <summary>
        /// 从当前生效配置中解析 external-controller，返回可用的 API 基地址。
        /// 支持 ":9090"、"127.0.0.1:9090"、"0.0.0.0:9090" 三种写法。
        /// 解析失败时返回 null（调用方降级为改写 YAML）。
        /// </summary>
        string ResolveControllerBaseUrl(out string secret)
        {
            secret = null;
            try
            {
                string content = ReadActiveConfigContent();
                if (string.IsNullOrEmpty(content))
                    return null;

                var match = ExternalControllerRegex.Match(content);
                if (!match.Success)
                    return null;

                string raw = match.Groups[1].Value.Trim();
                if (raw.Length == 0)
                    return null;

                // 仅给出端口（":9090"）时补全为回环地址
                if (raw.StartsWith(":"))
                    raw = "127.0.0.1" + raw;
                // 通配监听地址对客户端无意义，改为回环地址访问
                else if (raw.StartsWith("0.0.0.0:"))
                    raw = "127.0.0.1" + raw.Substring("0.0.0.0".Length);
                else if (raw.StartsWith("[::]:"))
                    raw = "127.0.0.1" + raw.Substring("[::]".Length);

                if (raw.IndexOf(':') < 0)
                    return null;

                var secretMatch = ControllerSecretRegex.Match(content);
                if (secretMatch.Success)
                {
                    string s = secretMatch.Groups[1].Value.Trim();
                    if (s.Length > 0)
                        secret = s;
                }

                return "http://" + raw;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 调用 mihomo API。method 为 GET / PATCH / PUT；成功时通过 body 返回响应正文。
        /// </summary>
        bool TryControllerApi(string method, string relativePath, string requestBody, out string responseBody)
        {
            responseBody = null;
            string secret;
            string baseUrl = ResolveControllerBaseUrl(out secret);
            if (string.IsNullOrEmpty(baseUrl))
                return false;

            // 便宜的预检：先确认端口有人监听，再发 HTTP。
            // 没有这一步时，端口无人监听会让 Windows 花约 2 秒才判定连接被拒，
            // 而 TryControllerApi 位于菜单渲染路径上，代价是「每次开菜单卡 2 秒」。
            // 用 150ms 的连接超时探测：真在监听的端口是回环连接，通常 <1ms 完成。
            if (!IsLoopbackEndpointReachable(baseUrl))
                return false;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(baseUrl + relativePath);
                request.Method = method;
                request.Timeout = ControllerApiTimeoutMilliseconds;
                request.ReadWriteTimeout = ControllerApiTimeoutMilliseconds;
                request.Proxy = null;   // 面板/API 走回环地址，禁止再经系统代理
                request.KeepAlive = false;
                request.UserAgent = "MihomoTray";
                if (!string.IsNullOrEmpty(secret))
                    request.Headers["Authorization"] = "Bearer " + secret;

                if (!string.IsNullOrEmpty(requestBody))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(requestBody);
                    request.ContentType = "application/json";
                    request.ContentLength = payload.Length;
                    using (var stream = request.GetRequestStream())
                        stream.Write(payload, 0, payload.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    responseBody = reader.ReadToEnd();
                }
                return true;
            }
            catch
            {
                // 核心未运行、未开启 external-controller、或鉴权失败
                return false;
            }
        }

        /// <summary>读取核心当前生效的规则模式（rule / global / direct），失败返回 null。</summary>
        string ReadCoreMode()
        {
            string body;
            if (!TryControllerApi("GET", "/configs", null, out body) || string.IsNullOrEmpty(body))
                return null;

            var match = ConfigModeRegex.Match(body);
            if (!match.Success)
                return null;

            return NormalizeMode(match.Groups[1].Value);
        }

        /// <summary>读取 YAML 中声明的规则模式（核心未运行时的回退来源）。</summary>
        string ReadConfigFileMode()
        {
            try
            {
                string content = ReadActiveConfigContent();
                if (string.IsNullOrEmpty(content))
                    return ModeRule;

                var match = ModeScalarRegex.Match(content);
                if (!match.Success)
                    return ModeRule;

                return NormalizeMode(match.Groups[1].Value);
            }
            catch { }
            return ModeRule;
        }

        /// <summary>规范化模式字符串，未知取值一律回退为 rule。</summary>
        static string NormalizeMode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return ModeRule;

            switch (value.Trim().ToLowerInvariant())
            {
                case ModeGlobal: return ModeGlobal;
                case ModeDirect: return ModeDirect;
                default: return ModeRule;
            }
        }

        /// <summary>
        /// 当前生效模式：核心运行中优先取 API（真实运行态），否则回退读配置文件。
        /// </summary>
        string ResolveCurrentMode()
        {
            if (IsMihomoRunning())
            {
                string live = ReadCoreMode();
                if (live != null)
                    return live;
            }
            return ReadConfigFileMode();
        }

        /// <summary>
        /// 切换规则模式。优先调用 API 热生效（不断开现有连接）；
        /// API 不可用时降级为改写 YAML 并重启核心。
        /// </summary>
        void ApplyMode(string mode)
        {
            mode = NormalizeMode(mode);
            bool running = IsMihomoRunning();

            if (running)
            {
                string payload = "{\"mode\":\"" + mode + "\"}";
                string ignored;
                if (TryControllerApi("PATCH", "/configs", payload, out ignored))
                {
                    WriteModeToConfigFile(mode);   // 保持配置文件与运行态一致，便于下次冷启动
                    RefreshUI();
                    _trayIcon.ShowBalloonTip(2000, "Mihomo", "规则模式已切换为 " + DescribeMode(mode), ToolTipIcon.Info);
                    return;
                }
            }

            // 降级路径：改写配置文件后重启核心
            if (WriteModeToConfigFile(mode))
            {
                if (running)
                {
                    StopMihomo();
                    StartMihomo();
                }
            }
            else
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "切换规则模式失败：配置中未找到 mode 字段，且外部控制接口不可用。",
                    ToolTipIcon.Warning);
            }
            RefreshUI();
        }

        /// <summary>把 mode 写回配置文件，保持冷启动一致。返回是否写入成功。</summary>
        bool WriteModeToConfigFile(string mode)
        {
            try
            {
                string configPath = GetActiveConfigPath();
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
                    return false;

                string content = File.ReadAllText(configPath, Encoding.UTF8);
                if (ModeScalarRegex.IsMatch(content))
                {
                    content = ModeScalarRegex.Replace(content, "mode: " + mode);
                }
                else
                {
                    content = InsertTopLevelScalar(content, "mode", mode);
                }

                WriteUtf8FileAtomic(configPath, content);
                UpdateActiveConfigCache(configPath, content);
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>在顶层插入一个标量配置项（置于文件头部，紧跟注释之后）。</summary>
        static string InsertTopLevelScalar(string content, string key, string value)
        {
            if (string.IsNullOrEmpty(content))
                return key + ": " + value + "\n";

            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            int insertAt = 0;
            // 跳过开头的空行与注释行，保持文件原有排版
            while (insertAt < lines.Length)
            {
                string trimmed = lines[insertAt].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    insertAt++;
                else
                    break;
            }

            var result = new List<string>(lines.Length + 1);
            for (int i = 0; i < insertAt; i++)
                result.Add(lines[i]);
            result.Add(key + ": " + value);
            for (int i = insertAt; i < lines.Length; i++)
                result.Add(lines[i]);

            return string.Join("\r\n", result);
        }

        static string DescribeMode(string mode)
        {
            switch (NormalizeMode(mode))
            {
                case ModeGlobal: return "全局模式";
                case ModeDirect: return "直连模式";
                default: return "规则模式";
            }
        }

        // ──── ProxiFyre 按应用代理集成 ────
        // ProxiFyre 借助 Windows Packet Filter (NDISAPI) 驱动按进程名重定向流量，
        // 与本程序的 TUN 模式互不冲突，且不需要 TUN。本程序只做管理：
        // 读写 app-config.json、控制 ProxiFyreService 启停，不复用其驱动逻辑。

        string ProxiFyreDir
        {
            get
            {
                // 优先用环境变量，避免 32 位进程被重定向到 Program Files (x86)
                string programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(programFiles))
                    programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(programFiles, "ProxiFyre");
            }
        }

        string ProxiFyreConfigPath
        {
            get { return Path.Combine(ProxiFyreDir, "app-config.json"); }
        }

        string ProxiFyreExePath
        {
            get { return Path.Combine(ProxiFyreDir, "ProxiFyre.exe"); }
        }

        /// <summary>停用按应用代理时的进程名单快照（放在本程序配置目录，避免污染 ProxiFyre 安装目录）。</summary>
        string ProxiFyreDisabledSnapshotPath
        {
            get { return Path.Combine(_basePath, "appproxy-disabled.json"); }
        }

        const string ProxiFyreServiceName = "ProxiFyreService";
        const string ProxiFyreProcessName = "ProxiFyre";

        // 用户指定的上游 SOCKS5 端口。0 表示自动探测本程序配置中的端口。
        int _proxiFyrePortOverride;

        /// <summary>
        /// 解析 ProxiFyre 应使用的上游 SOCKS5 端点。
        /// 优先级：用户显式指定 > ProxiFyre 配置中已写且仍在监听的端点 >
        ///         本程序配置推断出的端口（若正在监听）> 常见端口探测 > 默认 7890。
        /// 关键点：必须校验端口"真的在监听"，否则会把 ProxiFyre 指向一个死端口。
        /// </summary>
        string ResolveProxiFyreEndpoint()
        {
            if (_proxiFyrePortOverride > 0 && _proxiFyrePortOverride <= 65535)
                return "127.0.0.1:" + _proxiFyrePortOverride;

            // ProxiFyre 配置里已经写过的端点：只要还在监听就直接沿用，避免无谓改动
            string existing = ReadProxiFyreEndpoint();
            int existingPort;
            if (!string.IsNullOrEmpty(existing) &&
                TryExtractPort(existing, out existingPort) &&
                IsLocalPortListening(existingPort))
                return existing;

            // 本程序配置推断出的端口，仅当确实在监听时采用
            int inferred = ReadSocksPort();
            if (IsLocalPortListening(inferred))
                return "127.0.0.1:" + inferred;

            // 兜底：探测常见端口（外部核心可能跑在别的端口上）
            foreach (int candidate in CommonProxyPorts)
            {
                if (candidate == inferred) continue;
                if (IsLocalPortListening(candidate))
                    return "127.0.0.1:" + candidate;
            }

            // 都不在监听：仍然返回推断值，交给探测器报错，好过静默指向 7890
            return "127.0.0.1:" + inferred;
        }

        /// <summary>常见代理端口，按优先级排列，用于上游端点兜底探测。</summary>
        static readonly int[] CommonProxyPorts = { 7897, 7890, 7891, 7893, 7899, 1080, 10808, 2080 };

        /// <summary>从 "host:port" 中取出端口号。</summary>
        static bool TryExtractPort(string endpoint, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(endpoint)) return false;
            int idx = endpoint.LastIndexOf(':');
            if (idx < 0 || idx + 1 >= endpoint.Length) return false;
            return int.TryParse(endpoint.Substring(idx + 1).Trim(), out port) &&
                   port > 0 && port <= 65535;
        }

        /// <summary>本机 127.0.0.1 的指定端口是否有服务在监听。</summary>
        static bool IsLocalPortListening(int port)
        {
            if (port <= 0 || port > 65535) return false;
            var client = new System.Net.Sockets.TcpClient();
            try
            {
                var result = client.BeginConnect("127.0.0.1", port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(120, false))
                    return false;
                client.EndConnect(result);
                return true;
            }
            catch { return false; }
            finally { try { client.Close(); } catch { } }
        }

        /// <summary>
        /// 廉价判断 "http://host:port" 是否值得去发一次真实的 HTTP 请求。
        ///
        /// 动机：Windows 对一个"无人监听"的回环端口，判定连接被拒要花约 2 秒
        /// （实测 9090/9097/7891 均为 2000~2020ms）。HttpWebRequest 的 Timeout
        /// 设成 4000ms 也没用——拒绝不是超时，而是同步阻塞 2 秒后才返回。
        /// 由于 TryControllerApi 会被菜单渲染路径间接调用，这 2 秒直接变成
        /// "每次打开菜单都卡 2 秒"。
        ///
        /// 这里用 150ms 的连接探测做闸门：端口真在监听时是回环连接、通常 &lt;1ms 返回，
        /// 所以正常情况下一分钱都不多花；端口不通时把 2000ms 压到 150ms 上限。
        /// </summary>
        static bool IsLoopbackEndpointReachable(string baseUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(baseUrl))
                    return false;

                var uri = new Uri(baseUrl, UriKind.Absolute);
                // 只对回环地址做预检：面板/API 本就只应走本机。
                // 非回环地址（用户自己填了局域网 IP）直接放行，避免误伤。
                //
                // 注意：这里刻意不写 `out var addr`（C# 7.0 内联声明）。
                // 本项目用 .NET Framework 4.x 自带的旧编译器构建，语言级别是 C# 5，
                // 内联 out 变量会直接报 CS1026/CS1525。必须用先声明再传出的老写法。
                System.Net.IPAddress addr;
                if (!System.Net.IPAddress.TryParse(uri.Host, out addr) ||
                    !System.Net.IPAddress.IsLoopback(addr))
                {
                    return true;
                }

                return IsLocalPortListening(uri.Port);
            }
            catch { return true; }   // 解析失败时不阻断原路径，保持旧行为
        }

        /// <summary>ProxiFyre 是否已安装到本机。</summary>
        bool IsProxiFyreInstalled()
        {
            try { return File.Exists(ProxiFyreExePath); }
            catch { return false; }
        }

        /// <summary>ProxiFyreService 是否正在运行。</summary>
        bool IsProxiFyreRunning()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(ProxiFyreProcessName))
                {
                    try { p.Dispose(); } catch { }
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 一键启用/停用全部游戏进程的按应用代理。
        /// 启用 = 把配置中所有该代理的进程名写入 ProxiFyre；停用 = 清空 appNames。
        /// 停用后 ProxiFyre 不再劫持任何进程，游戏流量回归系统默认路由。
        /// </summary>
        void SetAllGameAppsEnabled(bool enable)
        {
            var all = ReadProxiFyreAppNames();
            var target = enable ? all : new List<string>();

            string error;
            // 记录停用前的名单，便于再次启用时恢复
            if (!enable && all.Count > 0)
                SaveProxiFyreDisabledSnapshot(all);

            if (!WriteProxiFyreAppNames(target, out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "写入 ProxiFyre 配置失败：" + error, ToolTipIcon.Error);
                return;
            }

            if (!RestartProxiFyreService(out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "配置已保存，但重启 ProxiFyreService 失败：" + error, ToolTipIcon.Warning);
            }
            else
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    enable
                        ? string.Format("按应用代理已启用 · {0} 个进程", target.Count)
                        : "按应用代理已停用（游戏流量回归默认路由）",
                    ToolTipIcon.Info);
            }
            RefreshUI();
        }

        /// <summary>把停用前的进程名单写入快照文件，供再次启用时恢复。</summary>
        void SaveProxiFyreDisabledSnapshot(List<string> names)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\r\n  \"appNames\": [\r\n");
                for (int i = 0; i < names.Count; i++)
                {
                    sb.Append("    \"").Append(EscapeJsonString(names[i])).Append("\"");
                    if (i < names.Count - 1) sb.Append(",");
                    sb.Append("\r\n");
                }
                sb.Append("  ]\r\n}\r\n");
                WriteUtf8FileAtomic(ProxiFyreDisabledSnapshotPath, sb.ToString());
            }
            catch { }
        }

        /// <summary>读取停用前的进程名单快照；不存在或为空时返回空列表。</summary>
        List<string> ReadProxiFyreDisabledSnapshot()
        {
            var names = new List<string>();
            try
            {
                if (!File.Exists(ProxiFyreDisabledSnapshotPath))
                    return names;
                string json = File.ReadAllText(ProxiFyreDisabledSnapshotPath, Encoding.UTF8);
                foreach (Match item in Regex.Matches(json, @"""([^""]+)"""))
                {
                    string name = item.Groups[1].Value.Trim();
                    if (name.Length > 0 && name != "appNames" && !names.Contains(name))
                        names.Add(name);
                }
            }
            catch { }
            return names;
        }

        /// <summary>一键启用：优先恢复快照中的名单，快照为空则用配置中现有的名单。</summary>
        void SetAllGameAppsEnabledFromSnapshot()
        {
            var snapshot = ReadProxiFyreDisabledSnapshot();
            if (snapshot.Count == 0)
            {
                // 无快照：说明当前配置里的名单已是被清空前的原名单，直接启用现有内容
                SetAllGameAppsEnabled(true);
                return;
            }

            string error;
            if (!WriteProxiFyreAppNames(snapshot, out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "写入 ProxiFyre 配置失败：" + error, ToolTipIcon.Error);
                return;
            }
            if (!RestartProxiFyreService(out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "配置已保存，但重启 ProxiFyreService 失败：" + error, ToolTipIcon.Warning);
            }
            else
            {
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    string.Format("按应用代理已启用 · 恢复 {0} 个进程", snapshot.Count),
                    ToolTipIcon.Info);
            }
            RefreshUI();
        }

        /// <summary>
        /// 本程序配置中对外提供的 SOCKS5 端口，供 ProxiFyre 作为上游。
        /// 优先级 socks-port > mixed-port > port；值为 0 表示该出入口被禁用，继续向下回退。
        /// </summary>
        int ReadSocksPort()
        {
            try
            {
                string content = ReadActiveConfigContent();
                if (!string.IsNullOrEmpty(content))
                {
                    foreach (var re in new[] { SocksPortRegex, MixedPortRegex, HttpPortRegex })
                    {
                        var m = re.Match(content);
                        if (m.Success)
                        {
                            int port;
                            // port == 0 是 mihomo 的"禁用"写法，必须跳过而非返回
                            if (int.TryParse(m.Groups[1].Value, out port) && port > 0 && port <= 65535)
                                return port;
                        }
                    }
                }
            }
            catch { }
            return 7890;
        }

        /// <summary>
        /// 读取 ProxiFyre 配置中所有被代理的进程名（去重、排序）。
        /// 用轻量解析而非完整反序列化，避免引入 JSON 序列化依赖。
        /// </summary>
        List<string> ReadProxiFyreAppNames()
        {
            var names = new List<string>();
            try
            {
                if (!File.Exists(ProxiFyreConfigPath))
                    return names;

                string json = File.ReadAllText(ProxiFyreConfigPath, Encoding.UTF8);
                var block = Regex.Match(json, @"""appNames""\s*:\s*\[(?<body>[^\]]*)\]",
                    RegexOptions.Singleline);
                foreach (Match m in Regex.Matches(json,
                    @"""appNames""\s*:\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline))
                {
                    foreach (Match item in Regex.Matches(m.Groups["body"].Value, @"""([^""]+)"""))
                    {
                        string name = item.Groups[1].Value.Trim();
                        if (name.Length > 0 && !names.Contains(name))
                            names.Add(name);
                    }
                }
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>读取 ProxiFyre 配置的上游 SOCKS5 端点（取第一组的）。</summary>
        string ReadProxiFyreEndpoint()
        {
            try
            {
                if (!File.Exists(ProxiFyreConfigPath))
                    return "";
                string json = File.ReadAllText(ProxiFyreConfigPath, Encoding.UTF8);
                var m = Regex.Match(json, @"""socks5ProxyEndpoint""\s*:\s*""([^""]+)""");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 把进程名列表写回 ProxiFyre 配置。
        /// 保留原有的分组结构与其它字段（端口、协议、excludes），只调整每个分组内的 appNames：
        /// 原组内被移除的进程会删除，新加入的进程追加到最后一组，避免破坏用户既有分组语义。
        /// </summary>
        bool WriteProxiFyreAppNames(List<string> appNames, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(ProxiFyreConfigPath))
                {
                    error = "未找到 app-config.json";
                    return false;
                }

                string original = File.ReadAllText(ProxiFyreConfigPath, Encoding.UTF8);

                // 保留原文件中的非 proxies 设置
                string logLevel = MatchScalar(original, "logLevel", "Error");
                bool bypassLan = string.Equals(MatchScalar(original, "bypassLan", "false"), "true",
                    StringComparison.OrdinalIgnoreCase);

                // 逐组重建：先按原顺序保留各组中仍被勾选的进程
                var desired = new List<string>(appNames ?? new List<string>());
                var groups = ReadProxiFyreGroups(original);
                var emitted = new List<string>();
                var result = new List<List<string>>();

                foreach (var group in groups)
                {
                    var kept = new List<string>();
                    foreach (var name in group)
                    {
                        if (ContainsName(desired, name) && !ContainsName(emitted, name))
                        {
                            kept.Add(name);
                            emitted.Add(name);
                        }
                    }
                    if (kept.Count > 0)
                        result.Add(kept);
                }

                // 剩余新增的进程并入一组（沿用最后一组的代理设置）
                var added = new List<string>();
                foreach (var name in desired)
                {
                    if (!ContainsName(emitted, name))
                    {
                        added.Add(name);
                        emitted.Add(name);
                    }
                }
                if (added.Count > 0)
                    result.Add(added);

                string endpoint = ResolveProxiFyreEndpoint();

                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"logLevel\": \"").Append(EscapeJsonString(logLevel)).Append("\",\r\n");
                sb.Append("  \"bypassLan\": ").Append(bypassLan ? "true" : "false").Append(",\r\n");
                sb.Append("  \"proxies\": [\r\n");
                for (int g = 0; g < result.Count; g++)
                {
                    sb.Append("    {\r\n");
                    sb.Append("      \"appNames\": [\r\n");
                    for (int i = 0; i < result[g].Count; i++)
                    {
                        sb.Append("        \"").Append(EscapeJsonString(result[g][i])).Append("\"");
                        if (i < result[g].Count - 1) sb.Append(",");
                        sb.Append("\r\n");
                    }
                    sb.Append("      ],\r\n");
                    sb.Append("      \"socks5ProxyEndpoint\": \"").Append(EscapeJsonString(endpoint)).Append("\",\r\n");
                    sb.Append("      \"username\": \"\",\r\n");
                    sb.Append("      \"password\": \"\",\r\n");
                    sb.Append("      \"socks5Transport\": \"TCP\",\r\n");
                    sb.Append("      \"tlsAllowInvalidCertificate\": false,\r\n");
                    sb.Append("      \"supportedProtocols\": [\r\n        \"TCP\",\r\n        \"UDP\"\r\n      ],\r\n");
                    sb.Append("      \"supportedAddressFamilies\": [\r\n        \"IPv4\",\r\n        \"IPv6\"\r\n      ]\r\n");
                    sb.Append("    }");
                    if (g < result.Count - 1) sb.Append(",");
                    sb.Append("\r\n");
                }
                sb.Append("  ],\r\n");
                sb.Append("  \"excludes\": []\r\n");
                sb.Append("}\r\n");

                // 先备份，避免写坏后无法恢复
                try { File.Copy(ProxiFyreConfigPath, ProxiFyreConfigPath + ".bak", true); } catch { }
                WriteUtf8FileAtomic(ProxiFyreConfigPath, sb.ToString());
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>按原始顺序读取 ProxiFyre 配置中的分组（每组一个进程名列表）。</summary>
        static List<List<string>> ReadProxiFyreGroups(string json)
        {
            var groups = new List<List<string>>();
            try
            {
                foreach (Match m in Regex.Matches(json,
                    @"""appNames""\s*:\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline))
                {
                    var group = new List<string>();
                    foreach (Match item in Regex.Matches(m.Groups["body"].Value, @"""([^""]+)"""))
                    {
                        string name = item.Groups[1].Value.Trim();
                        if (name.Length > 0 && !group.Contains(name))
                            group.Add(name);
                    }
                    if (group.Count > 0)
                        groups.Add(group);
                }
            }
            catch { }
            return groups;
        }

        static bool ContainsName(List<string> list, string name)
        {
            foreach (var n in list)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static string MatchScalar(string json, string key, string fallback)
        {
            var m = Regex.Match(json, @"""" + Regex.Escape(key) + @"""\s*:\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : fallback;
        }

        /// <summary>
        /// 让 ProxiFyre 重新加载配置。ProxiFyre 只在启动时读配置，
        /// 因此需要重启服务才能生效。
        /// </summary>
        bool RestartProxiFyreService(out string error)
        {
            error = null;
            if (!_isAdmin)
            {
                error = "需要管理员权限才能重启 ProxiFyreService";
                return false;
            }

            try
            {
                KillProxiFyreProcesses();
                RunSc("stop " + ProxiFyreServiceName);
                Thread.Sleep(1200);
                string output = RunSc("start " + ProxiFyreServiceName);
                Thread.Sleep(1500);
                if (output.IndexOf("失败", StringComparison.Ordinal) >= 0 ||
                    output.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    error = output.Trim();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 托盘启动时按需拉起 ProxiFyre 服务。
        /// 仅当「ProxiFyre 已安装」且「配置中存在被代理进程」时才尝试启动，
        /// 避免在用户没有配置按应用代理时无谓地拉起服务。失败保持静默，
        /// 用户可在托盘菜单里手动重试。
        /// </summary>
        void EnsureProxiFyreServiceOnStartup()
        {
            try
            {
                if (!IsProxiFyreInstalled())
                    return;
                if (ReadProxiFyreAppNames().Count == 0)
                    return;
                string error;
                EnsureProxiFyreRunning(out error);
            }
            catch { }
        }

        /// <summary>
        /// 确保 ProxiFyreService 处于运行状态；已运行则直接返回。
        /// 供托盘启动时与用户手动调用，避免「配置已写好但服务没开」的静默失效。
        /// </summary>
        bool EnsureProxiFyreRunning(out string error)
        {
            error = null;
            if (!IsProxiFyreInstalled())
            {
                error = "未检测到 ProxiFyre";
                return false;
            }
            if (IsProxiFyreRunning())
                return true;

            if (!_isAdmin)
            {
                error = "需要管理员权限才能启动 ProxiFyreService";
                return false;
            }

            try
            {
                string output = RunSc("start " + ProxiFyreServiceName);
                Thread.Sleep(1500);
                if (IsProxiFyreRunning())
                    return true;

                error = string.IsNullOrEmpty(output.Trim())
                    ? "服务未能启动" : output.Trim();
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        void KillProxiFyreProcesses()
        {
            foreach (var name in new[] { ProxiFyreProcessName, "ProxiFyreUI" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); } catch { }
                        try { p.Dispose(); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 停止 ProxiFyreService（用户所说的"关闭 ProxiFyre"）。
        /// 与「停用全部代理」的区别：
        ///   停用全部代理 = 清空 appNames 后重启服务，服务仍在跑但不再劫持任何进程；
        ///   停止服务     = 服务本身停掉，ProxiFyre 的 NDIS 过滤驱动卸载，
        ///                  所有按应用代理规则彻底失效，直到再次启动。
        /// 两者都让流量回归系统默认路由，但停止服务更彻底（也不占内存/不占驱动）。
        /// </summary>
        bool StopProxiFyreService(out string error)
        {
            error = null;
            if (!IsProxiFyreInstalled())
            {
                error = "未检测到 ProxiFyre";
                return false;
            }
            if (!_isAdmin)
            {
                error = "需要管理员权限才能停止 ProxiFyreService";
                return false;
            }

            try
            {
                string output = RunSc("stop " + ProxiFyreServiceName);
                // sc stop 是异步的：发完请求就返回，服务需要一点时间真正退出。
                // 这里轮询等待，避免"刚点完停止、快照里还是运行中"的观感错位。
                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    if (!IsProxiFyreRunning())
                        return true;
                    Thread.Sleep(250);
                }

                // 兜底：服务管理器没停下来就强杀进程，保证"关闭"这个语义生效
                KillProxiFyreProcesses();
                Thread.Sleep(400);

                if (!IsProxiFyreRunning())
                    return true;

                error = string.IsNullOrEmpty(output.Trim())
                    ? "服务未能停止" : output.Trim();
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>ProxiFyre 服务的启停开关（菜单项回调）。</summary>
        void OnToggleProxiFyreService(object sender, EventArgs e)
        {
            string error;

            // 这里刻意**不**用 _snapshotProxiFyreRunning 决定动作：
            // 快照最多有 SnapshotIntervalMs(3s) 的滞后，用户连续点击时
            // 很可能读到过期的状态，于是同一个动作被执行两次
            // （想开变成又想关，或者反之），表现为"点了没反应/越点越乱"。
            // 启停是低频的用户显式操作，改为现场问一次权威状态，代价可接受。
            bool running = IsProxiFyreRunning();

            if (running)
            {
                if (!StopProxiFyreService(out error))
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "停止 ProxiFyre 服务失败：" + error, ToolTipIcon.Error);
                    RefreshUI();
                    return;
                }
                _trayIcon.ShowBalloonTip(2500, "Mihomo",
                    "ProxiFyre 已关闭 · 按应用代理已失效，流量回归默认路由",
                    ToolTipIcon.Info);
            }
            else
            {
                if (!EnsureProxiFyreRunning(out error))
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "启动 ProxiFyre 服务失败：" + error, ToolTipIcon.Error);
                    RefreshUI();
                    return;
                }
                _trayIcon.ShowBalloonTip(2500, "Mihomo",
                    "ProxiFyre 已启动 · 按应用代理已生效",
                    ToolTipIcon.Info);
            }

            RefreshUI();
        }


        /// <summary>调用 sc.exe 并返回输出。</summary>
        static string RunSc(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo("sc.exe", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);
                    return stdout + stderr;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>切换某个进程的代理开关（加入/移出 appNames）。</summary>
        void ToggleProxiFyreApp(string appName, bool enable)
        {
            var names = ReadProxiFyreAppNames();
            if (enable)
            {
                if (!names.Contains(appName)) names.Add(appName);
            }
            else
            {
                names.RemoveAll(n => string.Equals(n, appName, StringComparison.OrdinalIgnoreCase));
            }

            string error;
            if (!WriteProxiFyreAppNames(names, out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo", "写入 ProxiFyre 配置失败：" + error, ToolTipIcon.Error);
                return;
            }

            if (!RestartProxiFyreService(out error))
            {
                _trayIcon.ShowBalloonTip(3000, "Mihomo",
                    "配置已保存，但重启 ProxiFyreService 失败：" + error, ToolTipIcon.Warning);
            }
            else
            {
                string action = enable ? "已加入" : "已移出";
                _trayIcon.ShowBalloonTip(2000, "Mihomo",
                    appName + " " + action + "按应用代理", ToolTipIcon.Info);
            }
            RefreshUI();
        }

        bool ReadTunStatus()
        {
            try
            {
                string content = ReadActiveConfigContent();
                if (string.IsNullOrEmpty(content)) return false;
                var match = TunEnableRegex.Match(content);
                if (match.Success)
                    return string.Equals(match.Groups[2].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        bool WriteTunStatus(bool enable)
        {
            try
            {
                string configPath = GetActiveConfigPath();
                if (string.IsNullOrEmpty(configPath)) return false;
                string content = ReadActiveConfigContent();
                if (string.IsNullOrEmpty(content)) return false;
                if (enable)
                {
                    string interfaceName = GetDefaultPhysicalInterfaceName();
                    if (!string.IsNullOrEmpty(interfaceName))
                    {
                        content = SetTopLevelYamlScalar(content, "interface-name", interfaceName);
                        content = SetTunYamlScalar(content, "auto-detect-interface", "false");
                        content = SetTunYamlScalar(content, "auto-route", "true");
                        content = SetTunYamlScalar(content, "auto-redirect", "true");
                    }
                }
                string newContent = TunEnableRegex.Replace(content,
                    "$1" + (enable ? "true" : "false"),
                    1);

                if (newContent != content)
                {
                    WriteUtf8FileAtomic(configPath, newContent);
                    UpdateActiveConfigCache(configPath, newContent);
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        // ──── Subscription ────

        bool PatchTunConfigForInterface(string interfaceName)
        {
            if (string.IsNullOrWhiteSpace(interfaceName))
                return false;

            string configPath = GetActiveConfigPath();
            if (string.IsNullOrEmpty(configPath))
                return false;

            string content = ReadActiveConfigContent();
            if (string.IsNullOrEmpty(content))
                return false;

            string patched = SetTopLevelYamlScalar(content, "interface-name", interfaceName);
            patched = SetTunYamlScalar(patched, "auto-detect-interface", "false");
            patched = SetTunYamlScalar(patched, "auto-route", "true");
            patched = SetTunYamlScalar(patched, "auto-redirect", "true");

            if (patched != content)
            {
                WriteUtf8FileAtomic(configPath, patched);
                UpdateActiveConfigCache(configPath, patched);
            }

            return true;
        }

        string SetTopLevelYamlScalar(string content, string key, string value)
        {
            string line = key + ": " + EscapeYamlScalar(value);
            Regex regex = new Regex(@"(?m)^" + Regex.Escape(key) + @":\s*.*$", RegexOptions.IgnoreCase);
            if (regex.IsMatch(content))
                return regex.Replace(content, line, 1);

            return line + "\r\n" + content;
        }

        string SetTunYamlScalar(string content, string key, string value)
        {
            Match match = TunBlockRegex.Match(content);
            if (!match.Success)
                return content;

            string block = match.Value;
            string line = "  " + key + ": " + value;
            Regex regex = new Regex(@"(?m)^[ \t]+" + Regex.Escape(key) + @":\s*.*$", RegexOptions.IgnoreCase);
            string newBlock;
            if (regex.IsMatch(block))
            {
                newBlock = regex.Replace(block, line, 1);
            }
            else
            {
                int insertIndex = block.Length;
                if (insertIndex > 0 && block[insertIndex - 1] != '\n')
                    newBlock = block + "\r\n" + line + "\r\n";
                else
                    newBlock = block + line + "\r\n";
            }

            return content.Substring(0, match.Index) + newBlock + content.Substring(match.Index + match.Length);
        }

        string EscapeYamlScalar(string value)
        {
            if (value == null)
                return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// 下载订阅并合并其 proxies 到活动配置。
        /// 不再直接弹气球：错误通过 error 回传，由调用方汇总后一次性提示，
        /// 避免「更新全部」时弹出十几个提示框互相覆盖。
        /// </summary>
        bool DownloadAndMergeSubscription(string url, string name, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(url) || url.Trim().Length == 0)
            {
                error = "订阅链接为空";
                return false;
            }

            string downloadedYaml;
            try
            {
                downloadedYaml = DownloadUrl(url);
            }
            catch (Exception ex)
            {
                error = "下载失败: " + ex.Message;
                return false;
            }

            if (string.IsNullOrWhiteSpace(downloadedYaml))
            {
                error = "返回内容为空";
                return false;
            }

            // 部分机场会对失效订阅返回一句错误说明而不是配置
            string head = downloadedYaml.TrimStart();
            if (head.Length > 0 && head[0] == '{')
            {
                error = "返回的是 JSON 而非配置，订阅可能已失效";
                return false;
            }
            if (head.StartsWith("<", StringComparison.Ordinal))
            {
                error = "返回的是网页而非配置（链接可能已过期或需要登录）";
                return false;
            }

            if (IsBase64String(downloadedYaml))
            {
                string decoded = null;
                try
                {
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(downloadedYaml));
                }
                catch
                {
                    try
                    {
                        decoded = Encoding.UTF8.GetString(
                            Convert.FromBase64String(downloadedYaml.Trim().Replace(" ", "+")));
                    }
                    catch
                    {
                        decoded = null;
                    }
                }

                if (string.IsNullOrWhiteSpace(decoded))
                {
                    error = "Base64 解码失败";
                    return false;
                }
                downloadedYaml = decoded;
            }

            return MergeConfig(downloadedYaml, name, out error);
        }

        bool MergeConfig(string downloadedYaml, string name, out string error)
        {
            error = null;
            try
            {
                string activePath = _activeConfigPath;
                if (!File.Exists(activePath)) activePath = _configPath;
                if (!File.Exists(activePath))
                {
                    error = "活动配置不存在: " + activePath;
                    return false;
                }

                string originalConfig = File.ReadAllText(activePath, Encoding.UTF8);

                string subscriptionProxiesPart = ExtractSectionFromYaml(downloadedYaml, "proxies:");

                if (string.IsNullOrWhiteSpace(subscriptionProxiesPart))
                {
                    error = "订阅中未找到 proxies 配置";
                    return false;
                }

                int proxiesIndex = FindTopLevelKeyIndex(originalConfig, "proxies:");
                if (proxiesIndex < 0)
                {
                    error = "活动配置中未找到 proxies 配置";
                    return false;
                }

                string headerPart = originalConfig.Substring(0, proxiesIndex).TrimEnd();
                string newConfig = headerPart + "\r\n" + subscriptionProxiesPart;

                WriteUtf8FileAtomic(activePath, newConfig);
                UpdateActiveConfigCache(activePath, newConfig);
                return true;
            }
            catch (Exception ex)
            {
                error = "合并配置失败: " + ex.Message;
                return false;
            }
        }

        string ExtractSectionFromYaml(string yaml, string key)
        {
            int idx = FindTopLevelKeyIndex(yaml, key);
            if (idx < 0) return null;

            string fromKey = yaml.Substring(idx);
            return fromKey.Trim();
        }

        int FindTopLevelKeyIndex(string yaml, string key)
        {
            var match = Regex.Match(yaml, @"^" + Regex.Escape(key), RegexOptions.Multiline);
            if (match.Success)
                return match.Index;
            return -1;
        }

        // ──── Subscription Config ────

		List<SubscriptionInfo> subs = new List<SubscriptionInfo>();

		/// <summary>
		/// 读取订阅列表。
		/// 解析使用「提取 JSON 数组正文 + 逐对象扫描」而非单条正则：
		/// 正则 [^}]* 遇到含 } 的链接、或 name/url 顺序颠倒、含转义引号时都会失配，
		/// 导致订阅静默丢失或被截断。
		/// </summary>
		List<SubscriptionInfo> LoadSubscriptions()
        {
            return ReadSubscriptionsFromJson(ReadTrayConfigJson());
        }

        List<SubscriptionInfo> ReadSubscriptionsFromJson(string json)
        {
            var list = new List<SubscriptionInfo>();
            string inner = ExtractJsonArrayBody(json, "subscriptions");
            if (inner == null)
                return list;

            foreach (string obj in SplitTopLevelObjects(inner))
            {
                string name = ExtractObjectString(obj, "name");
                string url = ExtractObjectString(obj, "url");
                if (name == null && url == null)
                    continue;
                list.Add(new SubscriptionInfo
                {
                    Name = name == null ? "" : name,
                    Url = url == null ? "" : url
                });
            }
            return list;
        }

        string ReadTrayConfigJson()
        {
            try
            {
                if (!File.Exists(_trayConfigPath))
                    SaveDefaultSubscriptions();
                return File.ReadAllText(_trayConfigPath, Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 取出顶层 "key": [ ... ] 中括号内的正文，能正确跨过字符串与嵌套括号。
        /// </summary>
        static string ExtractJsonArrayBody(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return null;

            int keyIdx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (keyIdx < 0)
                return null;

            int i = json.IndexOf(':', keyIdx + key.Length + 2);
            if (i < 0)
                return null;
            i++;

            while (i < json.Length && char.IsWhiteSpace(json[i]))
                i++;
            if (i >= json.Length || json[i] != '[')
                return null;

            int start = i + 1;
            int depth = 1;
            bool inString = false;
            bool escaped = false;

            for (i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                        return json.Substring(start, i - start);
                }
            }
            return null;
        }

        /// <summary>把数组正文按顶层 { } 切分为若干对象字面量。</summary>
        static List<string> SplitTopLevelObjects(string body)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(body))
                return result;

            int depth = 0;
            int start = -1;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }

                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(body.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return result;
        }

        /// <summary>从单个对象字面量中取字符串字段；无该字段返回 null，字段为空串返回 ""。</summary>
        static string ExtractObjectString(string obj, string field)
        {
            if (string.IsNullOrEmpty(obj))
                return null;

            string needle = "\"" + field + "\"";
            int idx = obj.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0)
                return null;

            int i = obj.IndexOf(':', idx + needle.Length);
            if (i < 0)
                return null;
            i++;

            while (i < obj.Length && char.IsWhiteSpace(obj[i]))
                i++;
            if (i >= obj.Length || obj[i] != '"')
                return null;

            i++;
            var sb = new StringBuilder();
            bool escaped = false;
            for (; i < obj.Length; i++)
            {
                char c = obj[i];
                if (escaped)
                {
                    sb.Append('\\').Append(c);
                    escaped = false;
                    continue;
                }
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return UnescapeJsonString(sb.ToString());
        }


        void SaveDefaultSubscriptions()
        {
            string defaultJson =
                "{\r\n" +
                "  \"panel\": {\r\n" +
                "    \"host\": \"127.0.0.1\",\r\n" +
                "    \"port\": 9097,\r\n" +
                "    \"path\": \"ui\"\r\n" +
                "  },\r\n" +
                "  \"profiles\": [\r\n" +
                "    {\r\n" +
                "      \"name\": \"默认配置\",\r\n" +
                "      \"path\": \"config.yaml\"\r\n" +
                "    }\r\n" +
                "  ],\r\n" +
                "  \"activeConfigPath\": \"config.yaml\",\r\n" +
                "  \"runMihomoOnStartup\": false,\r\n" +
                "  \"lastTunEnabled\": false,\r\n" +
                "  \"lastSystemProxyEnabled\": false,\r\n" +
                "  \"systemProxyGuardEnabled\": true,\r\n" +
                "  \"pendingEnableTunAfterAdmin\": false,\r\n" +
                "  \"subscriptions\": [\r\n" +
                "    {\r\n" +
                "      \"name\": \"订阅1\",\r\n" +
                "      \"url\": \"https://your-subscription-url.com/link?token=xxx\"\r\n" +
                "    }\r\n" +
                "  ]\r\n" +
                "}";
            WriteUtf8FileAtomic(_trayConfigPath, defaultJson);
        }

        void LoadTrayConfig()
        {
            if (!File.Exists(_trayConfigPath))
            {
                SaveDefaultSubscriptions();
            }

            try
            {
                string json = File.ReadAllText(_trayConfigPath, Encoding.UTF8);

                var hostMatch = Regex.Match(json, @"""host""\s*:\s*""([^""]+)""");
                if (hostMatch.Success)
                {
                    _panelHost = hostMatch.Groups[1].Value;
                }

                var portMatch = Regex.Match(json, @"""port""\s*:\s*(\d+)");
                if (portMatch.Success)
                {
                    int parsedPort;
                    if (int.TryParse(portMatch.Groups[1].Value, out parsedPort))
                    {
                        _panelPort = parsedPort;
                    }
                }

                var pathMatch = Regex.Match(json, @"""path""\s*:\s*""([^""]+)""");
                if (pathMatch.Success)
                {
                    _panelPath = pathMatch.Groups[1].Value;
                }

                var activeConfigMatch = Regex.Match(json, @"""activeConfigPath""\s*:\s*""([^""]+)""");
                if (activeConfigMatch.Success)
                {
                    _activeConfigPath = ResolveRelativePath(activeConfigMatch.Groups[1].Value);
                }

                var runMihomoMatch = Regex.Match(json, @"""runMihomoOnStartup""\s*:\s*(true|false)");
                if (runMihomoMatch.Success)
                {
                    _runMihomoOnStartup = runMihomoMatch.Groups[1].Value == "true";
                }

                var lastTunMatch = Regex.Match(json, @"""lastTunEnabled""\s*:\s*(true|false)");
                if (lastTunMatch.Success)
                {
                    _lastTunEnabled = lastTunMatch.Groups[1].Value == "true";
                    _loadedLastTunEnabled = true;
                }

                var lastSystemProxyMatch = Regex.Match(json, @"""lastSystemProxyEnabled""\s*:\s*(true|false)");
                if (lastSystemProxyMatch.Success)
                {
                    _systemProxyDesired = lastSystemProxyMatch.Groups[1].Value == "true";
                    _loadedSystemProxyDesired = true;
                }

                var proxyGuardMatch = Regex.Match(json, @"""systemProxyGuardEnabled""\s*:\s*(true|false)");
                if (proxyGuardMatch.Success)
                {
                    _systemProxyGuardEnabled = proxyGuardMatch.Groups[1].Value == "true";
                }

                var pendingTunMatch = Regex.Match(json, @"""pendingEnableTunAfterAdmin""\s*:\s*(true|false)");
                if (pendingTunMatch.Success)
                {
                    _pendingEnableTunAfterAdmin = pendingTunMatch.Groups[1].Value == "true";
                }

                var pfPortMatch = Regex.Match(json, @"""proxiFyrePort""\s*:\s*(\d+)");
                if (pfPortMatch.Success)
                {
                    int pfPort;
                    if (int.TryParse(pfPortMatch.Groups[1].Value, out pfPort) &&
                        pfPort > 0 && pfPort <= 65535)
                    {
                        _proxiFyrePortOverride = pfPort;
                    }
                }

                LoadAssetVersions(json);

                var profilesMatch = Regex.Matches(json,
                    @"""profiles""\s*:\s*\[(.*?)\]",
                    RegexOptions.Singleline);

                _profiles = new List<ConfigProfile>();
                if (profilesMatch.Count > 0)
                {
                    string innerProfiles = profilesMatch[0].Groups[1].Value;
                    var profileEntries = Regex.Matches(innerProfiles,
                        @"{[^}]*""name""\s*:\s*""([^""]+)""[^}]*""path""\s*:\s*""([^""]+)""[^}]*}");
                    foreach (Match entry in profileEntries)
                    {
                        _profiles.Add(new ConfigProfile
                        {
                            Name = entry.Groups[1].Value,
                            Path = ResolveRelativePath(entry.Groups[2].Value)
                        });
                    }
                }

				subs = ReadSubscriptionsFromJson(json);
			}
			catch { }
		}

        void SaveTrayConfig()
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"panel\": {\r\n");
            sb.Append("    \"host\": \"").Append(EscapeJsonString(_panelHost)).Append("\",\r\n");
            sb.Append("    \"port\": ").Append(_panelPort).Append(",\r\n");
            sb.Append("    \"path\": \"").Append(EscapeJsonString(_panelPath)).Append("\"\r\n");
            sb.Append("  },\r\n");
            sb.Append("  \"profiles\": [\r\n");
            for (int i = 0; i < _profiles.Count; i++)
            {
                var p = _profiles[i];
                sb.Append("    {\r\n");
                sb.Append("      \"name\": \"").Append(EscapeJsonString(p.Name)).Append("\",\r\n");
                sb.Append("      \"path\": \"").Append(EscapeJsonString(NormalizeRelativePath(p.Path))).Append("\"\r\n");
                sb.Append("    }");
                if (i < _profiles.Count - 1) sb.Append(",");
                sb.Append("\r\n");
            }
            sb.Append("  ],\r\n");
            sb.Append("  \"activeConfigPath\": \"").Append(EscapeJsonString(NormalizeRelativePath(_activeConfigPath))).Append("\",\r\n");
            sb.Append("  \"runMihomoOnStartup\": ").Append(_runMihomoOnStartup ? "true" : "false").Append(",\r\n");
            sb.Append("  \"lastTunEnabled\": ").Append(_lastTunEnabled ? "true" : "false").Append(",\r\n");
            sb.Append("  \"lastSystemProxyEnabled\": ").Append(_systemProxyDesired ? "true" : "false").Append(",\r\n");
            sb.Append("  \"systemProxyGuardEnabled\": ").Append(_systemProxyGuardEnabled ? "true" : "false").Append(",\r\n");
            sb.Append("  \"pendingEnableTunAfterAdmin\": ").Append(_pendingEnableTunAfterAdmin ? "true" : "false").Append(",\r\n");
            sb.Append("  \"proxiFyrePort\": ").Append(_proxiFyrePortOverride).Append(",\r\n");
            AppendAssetVersionsJson(sb);
            sb.Append(",\r\n");
            sb.Append("  \"subscriptions\": [\r\n");
            for (int i = 0; i < subs.Count; i++)
            {
                var s = subs[i];
                sb.Append("    {\r\n");
                sb.Append("      \"name\": \"").Append(EscapeJsonString(s.Name)).Append("\",\r\n");
                sb.Append("      \"url\": \"").Append(EscapeJsonString(s.Url)).Append("\"\r\n");
                sb.Append("    }");
                if (i < subs.Count - 1) sb.Append(",");
                sb.Append("\r\n");
            }
            sb.Append("  ]\r\n");
            sb.Append("}\r\n");
            WriteUtf8FileAtomic(_trayConfigPath, sb.ToString());
        }

        void LoadAssetVersions(string json)
        {
            var versions = new Dictionary<string, AssetVersionInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var assetVersionsMatch = Regex.Match(json,
                    @"""assetVersions""\s*:\s*\{(?<body>.*?)\}\s*,\s*""subscriptions""",
                    RegexOptions.Singleline);
                if (!assetVersionsMatch.Success)
                    assetVersionsMatch = Regex.Match(json,
                        @"""assetVersions""\s*:\s*\{(?<body>.*?)\}\s*\}",
                        RegexOptions.Singleline);

                if (assetVersionsMatch.Success)
                {
                    string body = assetVersionsMatch.Groups["body"].Value;
                    var entries = Regex.Matches(body,
                        @"""(?<key>[^""]+)""\s*:\s*\{(?<value>.*?)\}",
                        RegexOptions.Singleline);

                    foreach (Match entry in entries)
                    {
                        string key = UnescapeJsonString(entry.Groups["key"].Value);
                        string value = entry.Groups["value"].Value;
                        long size = 0;
                        long.TryParse(ExtractJsonNumber(value, "size"), out size);

                        versions[key] = new AssetVersionInfo
                        {
                            TagName = ExtractJsonString(value, "tagName"),
                            AssetName = ExtractJsonString(value, "assetName"),
                            UpdatedAt = ExtractJsonString(value, "updatedAt"),
                            Digest = ExtractJsonString(value, "digest"),
                            Size = size
                        };
                    }
                }
            }
            catch { }

            _assetVersions = versions;
        }

        void AppendAssetVersionsJson(StringBuilder sb)
        {
            sb.Append("  \"assetVersions\": {\r\n");
            var keys = new List<string>();
            if (_assetVersions != null)
            {
                foreach (KeyValuePair<string, AssetVersionInfo> pair in _assetVersions)
                {
                    if (pair.Value != null)
                        keys.Add(pair.Key);
                }

                for (int i = 0; i < keys.Count; i++)
                {
                    string key = keys[i];
                    AssetVersionInfo info = _assetVersions[key];
                    sb.Append("    \"").Append(EscapeJsonString(key)).Append("\": {\r\n");
                    sb.Append("      \"tagName\": \"").Append(EscapeJsonString(info.TagName)).Append("\",\r\n");
                    sb.Append("      \"assetName\": \"").Append(EscapeJsonString(info.AssetName)).Append("\",\r\n");
                    sb.Append("      \"updatedAt\": \"").Append(EscapeJsonString(info.UpdatedAt)).Append("\",\r\n");
                    sb.Append("      \"digest\": \"").Append(EscapeJsonString(info.Digest)).Append("\",\r\n");
                    sb.Append("      \"size\": ").Append(info.Size < 0 ? 0 : info.Size).Append("\r\n");
                    sb.Append("    }");
                    if (i < keys.Count - 1)
                        sb.Append(",");
                    sb.Append("\r\n");
                }
            }
            sb.Append("  }");
        }

        void ResolveActiveConfig()
        {
            string previousConfigPath = _activeConfigPath;
            _activeConfigPath = ResolveRelativePath(_activeConfigPath);
            if (!File.Exists(_activeConfigPath))
            {
                _activeConfigPath = _configPath;
            }
            if (!string.Equals(previousConfigPath, _activeConfigPath, StringComparison.OrdinalIgnoreCase))
                InvalidateActiveConfigCache();
        }

        string ResolveRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return _configPath;
            if (Path.IsPathRooted(path))
                return path;
            return Path.Combine(_basePath, path);
        }

        string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "config.yaml";
            if (!Path.IsPathRooted(path))
                return path.Replace('\\', '/');
            if (path.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
                return path.Substring(_basePath.Length).Replace('\\', '/');
            return path;
        }

        string BuildPanelUrl()
        {
            string path = _panelPath == null ? "ui" : _panelPath.Trim();
            path = path.Trim('/');
            if (path.Length == 0) path = "ui";
            return "http://" + _panelHost + ":" + _panelPort + "/" + path + "/";
        }

        void WriteUtf8FileAtomic(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir))
                dir = _basePath;

            string tempPath = Path.Combine(dir,
                Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            File.WriteAllText(tempPath, content, Encoding.UTF8);
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch
                    {
                        File.Copy(tempPath, path, true);
                    }
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch { }
            }
        }

        string FindAssetUrl(string apiJson, AssetUpdateOption opt)
        {
            AssetReleaseInfo info = FindAssetReleaseInfo(apiJson, opt);
            if (info == null)
                throw new Exception("未在发布页中找到匹配资源: " + opt.AssetName);
            return info.DownloadUrl;
        }

        AssetReleaseInfo FindAssetReleaseInfo(string apiJson, AssetUpdateOption opt)
        {
            if (string.IsNullOrEmpty(apiJson))
                throw new Exception("GitHub API 返回为空");

            string tagName = ExtractJsonString(apiJson, "tag_name");
            var matches = Regex.Matches(apiJson, @"""browser_download_url""\s*:\s*""([^""]+)""");
            AssetReleaseInfo fallback = null;

            foreach (Match m in matches)
            {
                AssetReleaseInfo info = ParseAssetReleaseInfo(apiJson, m, tagName);
                string fileName = (info.AssetName ?? Path.GetFileName(info.DownloadUrl)).ToLowerInvariant();

                if (opt.MatchPattern != null)
                {
                    if (Regex.IsMatch(fileName, opt.MatchPattern, RegexOptions.IgnoreCase))
                        return info;
                }
                else if (fileName == opt.AssetName.ToLowerInvariant())
                {
                    return info;
                }

                if (fallback == null && info.DownloadUrl.ToLowerInvariant().Contains(opt.AssetName.ToLowerInvariant()))
                    fallback = info;
            }

            if (fallback != null)
                return fallback;

            throw new Exception("未在发布页中找到匹配资源: " + opt.AssetName);
        }

        AssetReleaseInfo ParseAssetReleaseInfo(string apiJson, Match downloadMatch, string tagName)
        {
            string downloadUrl = UnescapeJsonString(downloadMatch.Groups[1].Value);
            int windowStart = Math.Max(0, downloadMatch.Index - 12000);
            string beforeDownloadUrl = apiJson.Substring(windowStart, downloadMatch.Index - windowStart);

            long size = 0;
            string sizeText = ExtractLastJsonNumber(beforeDownloadUrl, "size");
            long.TryParse(sizeText, out size);

            string name = ExtractLastJsonString(beforeDownloadUrl, "name");
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(downloadUrl))
                name = Path.GetFileName(downloadUrl);

            return new AssetReleaseInfo
            {
                TagName = tagName ?? "",
                AssetName = name ?? "",
                DownloadUrl = downloadUrl ?? "",
                UpdatedAt = ExtractLastJsonString(beforeDownloadUrl, "updated_at"),
                Digest = ExtractLastJsonString(beforeDownloadUrl, "digest"),
                Size = size
            };
        }

        string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";

            Match match = Regex.Match(json, @"""" + Regex.Escape(key) + @"""\s*:\s*""([^""]*)""");
            if (!match.Success)
                return "";

            return UnescapeJsonString(match.Groups[1].Value);
        }

        /// <summary>
        /// 还原 JSON 字符串字面量中的转义序列。
        /// 注意不能使用 Regex.Unescape：它会把 \d \s \w 等非 JSON 转义按正则语义处理，
        /// 导致订阅链接中的片段被悄悄替换（例如 "https:\/\/x" 之外的 \d 被吃掉）。
        /// 这里只处理 JSON 规范定义的转义。
        /// </summary>
        static string UnescapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0)
                return s ?? "";

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char n = s[++i];
                switch (n)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            else
                            {
                                sb.Append('u');
                            }
                        }
                        else
                        {
                            sb.Append('u');
                        }
                        break;
                    default:
                        // 非法转义：保留原样，避免吞字符
                        sb.Append('\\').Append(n);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>按 JSON 规范转义字符串（含控制字符），与 UnescapeJsonString 互逆。</summary>
        static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";

            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        string ExtractLastJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";

            var matches = Regex.Matches(json, @"""" + Regex.Escape(key) + @"""\s*:\s*""([^""]*)""");
            if (matches.Count == 0)
                return "";

            return UnescapeJsonString(matches[matches.Count - 1].Groups[1].Value);
        }

        string ExtractJsonNumber(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";

            Match match = Regex.Match(json, @"""" + Regex.Escape(key) + @"""\s*:\s*(\d+)");
            if (!match.Success)
                return "";

            return match.Groups[1].Value;
        }

        string ExtractLastJsonNumber(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return "";

            var matches = Regex.Matches(json, @"""" + Regex.Escape(key) + @"""\s*:\s*(\d+)");
            if (matches.Count == 0)
                return "";

            return matches[matches.Count - 1].Groups[1].Value;
        }

        void DownloadFile(string url, bool compressed, bool isGz, string targetPath)
        {
            DownloadFile(url, compressed, isGz, targetPath, null);
        }

        void DownloadFile(string url, bool compressed, bool isGz, string targetPath, Action beforeReplace)
        {
            string targetDir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(targetDir))
                targetDir = _basePath;
            Directory.CreateDirectory(targetDir);

            string fileName = Path.GetFileName(targetPath);
            string tempFile = Path.Combine(targetDir,
                "." + fileName + ".download." + Guid.NewGuid().ToString("N") + ".tmp");
            string preparedFile = Path.Combine(targetDir,
                "." + fileName + ".prepared." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                DownloadUpdateFileWithRetry(url, tempFile);

                if (compressed)
                {
                    if (isGz)
                    {
                        DecompressGzToFile(tempFile, preparedFile);
                    }
                    else
                    {
                        string extractDir = Path.Combine(Path.GetTempPath(),
                            "mihomo_ext_" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            ZipFile.ExtractToDirectory(tempFile, extractDir);
                            string found = FindFileRecursive(extractDir, Path.GetFileName(targetPath));
                            if (string.IsNullOrEmpty(found))
                            {
                                found = FindCoreInZip(extractDir);
                            }
                            if (string.IsNullOrEmpty(found))
                                throw new Exception("压缩包中未找到目标文件");

                            File.Copy(found, preparedFile, true);
                        }
                        finally
                        {
                            try { Directory.Delete(extractDir, true); } catch { }
                        }
                    }

                    if (!File.Exists(preparedFile) || new FileInfo(preparedFile).Length == 0)
                        throw new Exception("更新文件为空");

                    if (beforeReplace != null)
                        beforeReplace();
                    ReplaceTargetFile(preparedFile, targetPath);
                }
                else
                {
                    if (beforeReplace != null)
                        beforeReplace();
                    ReplaceTargetFile(tempFile, targetPath);
                }
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
                try { File.Delete(preparedFile); } catch { }
            }
        }

        void DecompressGzToFile(string gzPath, string outputPath)
        {
            using (var input = File.OpenRead(gzPath))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = File.Create(outputPath))
            {
                byte[] buf = new byte[8192];
                int read;
                while ((read = gz.Read(buf, 0, buf.Length)) > 0)
                    output.Write(buf, 0, read);
            }
        }

        void ReplaceTargetFile(string sourcePath, string targetPath)
        {
            string targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            string backupPath = targetPath + ".bak";
            try { File.Delete(backupPath); } catch { }

            if (!File.Exists(targetPath))
            {
                File.Move(sourcePath, targetPath);
                return;
            }

            try
            {
                File.Replace(sourcePath, targetPath, backupPath);
                try { File.Delete(backupPath); } catch { }
            }
            catch
            {
                try
                {
                    File.Copy(targetPath, backupPath, true);
                    File.Copy(sourcePath, targetPath, true);
                    try { File.Delete(backupPath); } catch { }
                }
                catch
                {
                    try
                    {
                        if (File.Exists(backupPath))
                            File.Copy(backupPath, targetPath, true);
                    }
                    catch { }
                    throw;
                }
            }
        }

        string DownloadString(string url)
        {
            using (var stream = OpenUpdateResponseStream(url, true))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        void DownloadUpdateFileWithRetry(string url, string tempFile)
        {
            var errors = new StringBuilder();
            List<UpdateRequestRoute> routes = BuildUpdateRequestRoutes();

            foreach (UpdateRequestRoute route in routes)
            {
                int attempts = route.Attempts;
                if (attempts < AssetDownloadRouteAttempts && route.Proxy != null)
                    attempts = AssetDownloadRouteAttempts;

                for (int attempt = 1; attempt <= attempts; attempt++)
                {
                    try
                    {
                        try { File.Delete(tempFile); } catch { }
                        DownloadUpdateFileOnce(url, tempFile, route.Proxy);

                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new Exception("下载文件为空");

                        return;
                    }
                    catch (WebException ex)
                    {
                        string message = DescribeUpdateWebException(ex);
                        errors.Append(route.Name).Append(" 第 ").Append(attempt).Append(" 次: ")
                            .Append(message).Append("\r\n");
                    }
                    catch (Exception ex)
                    {
                        errors.Append(route.Name).Append(" 第 ").Append(attempt).Append(" 次: ")
                            .Append(ex.Message).Append("\r\n");
                    }

                    try { File.Delete(tempFile); } catch { }
                    if (attempt < attempts)
                        System.Threading.Thread.Sleep(AssetRetryDelayMilliseconds);
                }
            }

            throw new Exception("下载文件失败，已尝试可用代理和直连。\r\n" + errors.ToString().TrimEnd());
        }

        void DownloadUpdateFileOnce(string url, string tempFile, IWebProxy proxy)
        {
            var request = CreateUpdateRequest(url, false, proxy);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (stream == null)
                    throw new IOException("响应流为空");

                byte[] buf = new byte[64 * 1024];
                long totalRead = 0;
                int read;
                while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, read);
                    totalRead += read;
                }

                long expectedLength = response.ContentLength;
                if (expectedLength > 0 && totalRead != expectedLength)
                    throw new IOException("下载不完整: " + totalRead + "/" + expectedLength + " bytes");
            }
        }

        Stream OpenUpdateResponseStream(string url, bool json)
        {
            var errors = new StringBuilder();
            List<UpdateRequestRoute> routes = BuildUpdateRequestRoutes();

            foreach (UpdateRequestRoute route in routes)
            {
                for (int attempt = 1; attempt <= route.Attempts; attempt++)
                {
                    try
                    {
                        HttpWebRequest request = CreateUpdateRequest(url, json, route.Proxy);
                        var response = (HttpWebResponse)request.GetResponse();
                        Stream responseStream = response.GetResponseStream();
                        if (responseStream == null)
                        {
                            response.Close();
                            throw new IOException("响应流为空");
                        }

                        return new WebResponseStream(response, responseStream);
                    }
                    catch (WebException ex)
                    {
                        string message = DescribeUpdateWebException(ex);
                        errors.Append(route.Name).Append(" 第 ").Append(attempt).Append(" 次: ")
                            .Append(message).Append("\r\n");
                    }
                    catch (Exception ex)
                    {
                        errors.Append(route.Name).Append(" 第 ").Append(attempt).Append(" 次: ")
                            .Append(ex.Message).Append("\r\n");
                    }

                    if (attempt < route.Attempts)
                        System.Threading.Thread.Sleep(AssetRetryDelayMilliseconds);
                }
            }

            throw new Exception("下载超时或网络不可达。已尝试可用代理和直连。\r\n" + errors.ToString().TrimEnd());
        }

        HttpWebRequest CreateUpdateRequest(string url, bool json, IWebProxy proxy)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = proxy;
            request.Timeout = json ? AssetApiTimeoutMilliseconds : AssetDownloadTimeoutMilliseconds;
            request.ReadWriteTimeout = json ? AssetApiTimeoutMilliseconds : AssetDownloadTimeoutMilliseconds;
            request.UserAgent = "MihomoTray/1.0 (+https://github.com/wsdbb72/mhmtray)";
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;
            request.ProtocolVersion = HttpVersion.Version11;
            request.KeepAlive = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (json)
                request.Accept = "application/vnd.github.v3+json";
            return request;
        }

        List<UpdateRequestRoute> BuildUpdateRequestRoutes()
        {
            var routes = new List<UpdateRequestRoute>();
            AddSystemProxyRoutes(routes);
            AddConfigProxyFallbackRoutes(routes);
            AddUpdateRequestRoute(routes, "直连", null, "direct", 1);

            return routes;
        }

        void AddSystemProxyRoutes(List<UpdateRequestRoute> routes)
        {
            int routeCountBefore = routes.Count;
            var endpoints = new List<string>();
            bool enabled;
            string proxyServer;
            if (ReadSystemProxySettings(out enabled, out proxyServer) && enabled)
                AddSystemProxyEndpoints(endpoints, proxyServer);

            foreach (string endpoint in endpoints)
            {
                string normalized = NormalizeProxyEndpoint(endpoint, false, false);
                if (string.IsNullOrEmpty(normalized))
                    continue;

                AddUpdateRequestRoute(routes,
                    "系统代理 " + normalized,
                    new WebProxy("http://" + normalized, false),
                    "system:" + normalized.ToLowerInvariant(),
                    2);
            }

            if (routes.Count == routeCountBefore && HasWindowsAutoProxyConfiguration())
            {
                try
                {
                    AddUpdateRequestRoute(routes,
                        "Windows 系统代理",
                        WebRequest.GetSystemWebProxy(),
                        "system-default",
                        2);
                }
                catch { }
            }
        }

        void AddConfigProxyFallbackRoutes(List<UpdateRequestRoute> routes)
        {
            var endpoints = new List<string>();
            string content = null;
            try { content = ReadActiveConfigContent(); } catch { }

            AddLocalUpdateProxyEndpoint(endpoints, "127.0.0.1:" + ReadHttpPort());

            if (!string.IsNullOrEmpty(content))
            {
                var mixedMatch = MixedPortRegex.Match(content);
                if (mixedMatch.Success)
                {
                    int mixedPort;
                    if (int.TryParse(mixedMatch.Groups[1].Value, out mixedPort))
                        AddLocalUpdateProxyEndpoint(endpoints, "127.0.0.1:" + mixedPort);
                }

                var httpMatch = HttpPortRegex.Match(content);
                if (httpMatch.Success)
                {
                    int httpPort;
                    if (int.TryParse(httpMatch.Groups[1].Value, out httpPort))
                        AddLocalUpdateProxyEndpoint(endpoints, "127.0.0.1:" + httpPort);
                }
            }

            foreach (string endpoint in endpoints)
            {
                AddUpdateRequestRoute(routes,
                    "本程序代理 " + endpoint,
                    new WebProxy("http://" + endpoint, false),
                    "local:" + endpoint.ToLowerInvariant(),
                    2);
            }
        }

        void AddSystemProxyEndpoints(List<string> endpoints, string proxyServer)
        {
            if (string.IsNullOrWhiteSpace(proxyServer))
                return;

            if (proxyServer.IndexOf('=') < 0)
            {
                AddSystemProxyEndpoint(endpoints, proxyServer);
                return;
            }

            string[] entries = proxyServer.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawEntry in entries)
            {
                string entry = rawEntry.Trim();
                int equalIndex = entry.IndexOf('=');
                if (equalIndex < 0)
                    continue;

                string scheme = entry.Substring(0, equalIndex).Trim().ToLowerInvariant();
                if (scheme == "https")
                    AddSystemProxyEndpoint(endpoints, entry.Substring(equalIndex + 1));
            }

            foreach (string rawEntry in entries)
            {
                string entry = rawEntry.Trim();
                int equalIndex = entry.IndexOf('=');
                if (equalIndex < 0)
                    continue;

                string scheme = entry.Substring(0, equalIndex).Trim().ToLowerInvariant();
                if (scheme == "http")
                    AddSystemProxyEndpoint(endpoints, entry.Substring(equalIndex + 1));
            }
        }

        void AddSystemProxyEndpoint(List<string> endpoints, string endpoint)
        {
            string normalized = NormalizeProxyEndpoint(endpoint, false, false);
            if (IsUnavailableLocalProxyEndpoint(normalized))
                return;

            AddNormalizedUpdateProxyEndpoint(endpoints, normalized);
        }

        bool IsUnavailableLocalProxyEndpoint(string normalized)
        {
            string host;
            string portText;
            if (!TrySplitHostPort(normalized, out host, out portText))
                return false;

            host = host.Trim().Trim('[', ']');
            if (!IsLocalProxyHost(host))
                return false;

            int port;
            if (!int.TryParse(portText, out port))
                return false;

            return !IsLocalPortListening(host, port);
        }

        void AddLocalUpdateProxyEndpoint(List<string> endpoints, string endpoint)
        {
            string normalized = NormalizeProxyEndpoint(endpoint, true, true);
            AddNormalizedUpdateProxyEndpoint(endpoints, normalized);
        }

        void AddNormalizedUpdateProxyEndpoint(List<string> endpoints, string normalized)
        {
            if (string.IsNullOrEmpty(normalized))
                return;

            string key = normalized.ToLowerInvariant();
            foreach (string existing in endpoints)
            {
                if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            endpoints.Add(normalized);
        }

        bool HasWindowsAutoProxyConfiguration()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    if (key == null)
                        return false;

                    var autoConfig = key.GetValue("AutoConfigURL");
                    if (autoConfig != null && !string.IsNullOrWhiteSpace(Convert.ToString(autoConfig)))
                        return true;

                    var autoDetect = key.GetValue("AutoDetect");
                    int autoDetectValue = 0;
                    if (autoDetect is int)
                        autoDetectValue = (int)autoDetect;
                    else if (autoDetect != null)
                        int.TryParse(Convert.ToString(autoDetect), out autoDetectValue);

                    return autoDetectValue == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        void AddUpdateRequestRoute(List<UpdateRequestRoute> routes, string name, IWebProxy proxy, string key, int attempts)
        {
            foreach (UpdateRequestRoute route in routes)
            {
                if (string.Equals(route.Key, key, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            routes.Add(new UpdateRequestRoute
            {
                Name = name,
                Proxy = proxy,
                Key = key,
                Attempts = attempts
            });
        }

        string NormalizeProxyEndpoint(string endpoint, bool requireLocal, bool probe)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;

            endpoint = endpoint.Trim().Trim('"');

            int schemeIndex = endpoint.IndexOf("://", StringComparison.Ordinal);
            if (schemeIndex >= 0)
                endpoint = endpoint.Substring(schemeIndex + 3);

            int slashIndex = endpoint.IndexOfAny(new char[] { '/', '\\' });
            if (slashIndex >= 0)
                endpoint = endpoint.Substring(0, slashIndex);

            int atIndex = endpoint.LastIndexOf('@');
            if (atIndex >= 0)
                endpoint = endpoint.Substring(atIndex + 1);

            string host;
            string portText;
            if (!TrySplitHostPort(endpoint, out host, out portText))
                return null;

            int port;
            if (!int.TryParse(portText, out port) || port <= 0 || port > 65535)
                return null;

            host = host.Trim().Trim('[', ']');
            if (string.IsNullOrEmpty(host))
                return null;

            if (requireLocal && !IsLocalProxyHost(host))
                return null;

            if (probe && !IsLocalPortListening(host, port))
                return null;

            host = host.ToLowerInvariant();
            if (host == "::1" || host.IndexOf(':') >= 0)
                return "[" + host + "]:" + port;

            return host + ":" + port;
        }

        bool TrySplitHostPort(string endpoint, out string host, out string portText)
        {
            host = null;
            portText = null;

            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            endpoint = endpoint.Trim();
            if (endpoint.StartsWith("[", StringComparison.Ordinal))
            {
                int closeIndex = endpoint.IndexOf(']');
                if (closeIndex < 0 || endpoint.Length <= closeIndex + 2 || endpoint[closeIndex + 1] != ':')
                    return false;

                host = endpoint.Substring(1, closeIndex - 1).Trim();
                portText = endpoint.Substring(closeIndex + 2).Trim();
                return !string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(portText);
            }

            int colonIndex = endpoint.LastIndexOf(':');
            if (colonIndex <= 0 || colonIndex >= endpoint.Length - 1)
                return false;

            host = endpoint.Substring(0, colonIndex).Trim().Trim('[', ']');
            portText = endpoint.Substring(colonIndex + 1).Trim();
            return !string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(portText);
        }

        bool IsLocalProxyHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            host = host.Trim().Trim('[', ']').ToLowerInvariant();
            return host == "localhost" || host == "127.0.0.1" || host == "::1";
        }

        bool IsLocalPortListening(string host, int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    string connectHost = host;
                    if (string.Equals(connectHost, "localhost", StringComparison.OrdinalIgnoreCase))
                        connectHost = "127.0.0.1";

                    IAsyncResult ar = client.BeginConnect(connectHost, port, null, null);
                    bool connected = ar.AsyncWaitHandle.WaitOne(AssetProxyProbeTimeoutMilliseconds);
                    if (!connected)
                    {
                        try { client.Close(); } catch { }
                        return false;
                    }

                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        string DescribeUpdateWebException(WebException ex)
        {
            if (ex.Response != null)
            {
                using (var response = ex.Response)
                using (var stream = response.GetResponseStream())
                {
                    string body = null;
                    if (stream != null)
                    {
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            body = reader.ReadToEnd();
                        }
                    }

                    var http = response as HttpWebResponse;
                    if (http != null)
                    {
                        string prefix = "HTTP " + (int)http.StatusCode + " " + http.StatusDescription;
                        if (!string.IsNullOrWhiteSpace(body))
                            return prefix + ": " + TrimForMessage(body, 240);
                        return prefix;
                    }

                    if (!string.IsNullOrWhiteSpace(body))
                        return TrimForMessage(body, 240);
                }
            }

            if (!string.IsNullOrWhiteSpace(ex.Message))
                return ex.Message;

            return ex.Status.ToString();
        }

        string TrimForMessage(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
                return value;
            return value.Substring(0, maxLength) + "...";
        }

        string FindFileRecursive(string dir, string fileName)
        {
            string lower = fileName.ToLowerInvariant();
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(f).ToLowerInvariant() == lower)
                    return f;
            }
            return null;
        }

        string FindCoreInZip(string extractDir)
        {
            foreach (string f in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f).ToLowerInvariant();
                if (name.Contains("mihomo") && (name.EndsWith(".exe") || name == "mihomo" || !name.Contains(".")))
                    return f;
            }
            foreach (string f in Directory.GetFiles(extractDir, "*", SearchOption.TopDirectoryOnly))
            {
                if (!Path.GetFileName(f).Contains("."))
                    return f;
            }
            return null;
        }

        // ──── Network Helpers ────

        string DownloadUrl(string url)
        {
            var errors = new StringBuilder();

            try
            {
                return DownloadUrlWithRoute(url, null, SubscriptionDirectTimeoutMilliseconds);
            }
            catch (Exception ex)
            {
                errors.Append("直连: ").Append(ex.Message).Append("\r\n");
            }

            var proxyRoutes = new List<UpdateRequestRoute>();
            AddSystemProxyRoutes(proxyRoutes);
            if (proxyRoutes.Count == 0)
                throw new Exception("直连失败，且未检测到系统代理配置。\r\n" + errors.ToString().TrimEnd());

            foreach (UpdateRequestRoute route in proxyRoutes)
            {
                try
                {
                    return DownloadUrlWithRoute(url, route.Proxy, SubscriptionProxyTimeoutMilliseconds);
                }
                catch (Exception ex)
                {
                    errors.Append(route.Name).Append(": ").Append(ex.Message).Append("\r\n");
                }
            }

            throw new Exception("直连和系统代理均更新失败。\r\n" + errors.ToString().TrimEnd());
        }

        string DownloadUrlWithRoute(string url, IWebProxy proxy, int timeoutMilliseconds)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = proxy;
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            request.UserAgent = "clash-verge/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                throw new Exception(DescribeUpdateWebException(ex));
            }
        }

        bool IsBase64String(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = Regex.Replace(s, @"\s+", "");
            if (s.Length % 4 != 0) return false;
            return Regex.IsMatch(s, @"^[A-Za-z0-9+/]+=*$");
        }

        // ──── Icon ────

        Icon IconFromBase64(string base64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using (var ms = new MemoryStream(bytes))
                using (var bmp = new Bitmap(ms))
                using (var resized = new Bitmap(bmp, new Size(32, 32)))
                {
                    return IconFromBitmap(resized);
                }
            }
            catch
            {
                return CreateFallbackIcon(Color.FromArgb(140, 140, 140));
            }
        }

        Icon CreateFallbackIcon(Color color)
        {
            int size = 32;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(color))
                {
                    g.FillEllipse(brush, 4, 4, 24, 24);
                }
                using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0), 1.5f))
                {
                    g.DrawEllipse(pen, 4, 4, 24, 24);
                }
            }
            using (bmp)
            {
                return IconFromBitmap(bmp);
            }
        }

        Icon IconFromBitmap(Bitmap bitmap)
        {
            IntPtr hIcon = bitmap.GetHicon();
            try
            {
                using (var icon = Icon.FromHandle(hIcon))
                {
                    return (Icon)icon.Clone();
                }
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
    }

    class WebResponseStream : Stream
    {
        readonly WebResponse _response;
        readonly Stream _stream;

        public WebResponseStream(WebResponse response, Stream stream)
        {
            _response = response;
            _stream = stream;
        }

        public override bool CanRead { get { return _stream.CanRead; } }
        public override bool CanSeek { get { return _stream.CanSeek; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _stream.Length; } }
        public override long Position
        {
            get { return _stream.Position; }
            set { _stream.Position = value; }
        }

        public override void Flush()
        {
            _stream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _stream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _stream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            _stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _stream.Dispose(); } catch { }
                try { _response.Close(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    class SubscriptionInfo
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    class PanelConfig
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Path { get; set; }
    }

    class ConfigProfile
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    class AssetUpdateOption
    {
        public string DisplayName { get; set; }
        public string ApiUrl { get; set; }
        public string AssetName { get; set; }
        public string MatchPattern { get; set; }
        public string TargetPath { get; set; }
        public bool IsCore { get; set; }
        public bool Selected { get; set; }
        public bool IsGz { get; set; }
        public bool IsZip { get; set; }
    }

    static class UiStyles
    {
        const string MenuSurfaceTag = "telegram-menu-surface";
        const int AW_HIDE = 0x00010000;
        const int AW_ACTIVATE = 0x00020000;
        const int AW_BLEND = 0x00080000;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;

        static readonly Dictionary<string, Image> IconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll")]
        static extern bool AnimateWindow(IntPtr hWnd, int dwTime, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        public static readonly Color WindowBack = Color.FromArgb(244, 247, 250);
        public static readonly Color PanelBack = Color.White;
        public static readonly Color Border = Color.FromArgb(204, 204, 204);
        public static readonly Color Text = Color.FromArgb(0, 0, 0);
        public static readonly Color MutedText = Color.FromArgb(96, 96, 96);
        public static readonly Color IconText = Color.FromArgb(128, 143, 156);
        public static readonly Color Accent = Color.FromArgb(42, 171, 238);
        public static readonly Color AccentHover = Color.FromArgb(38, 152, 214);
        public static readonly Color HoverBack = Color.FromArgb(229, 243, 255);
        public static readonly Color Danger = Color.FromArgb(229, 57, 53);
        public static readonly Color ActiveGreen = Color.FromArgb(52, 199, 89);
        /// <summary>警示文字色（如"上游端口未监听"），比 MutedText 更强调但不至于像报错。</summary>
        public static readonly Color WarnText = Color.FromArgb(198, 120, 22);
        public static readonly Font BaseFont = SystemFonts.MenuFont;
        public static readonly Font TitleFont = new Font(SystemFonts.MenuFont, FontStyle.Bold);
        public static readonly Font CaptionFont = SystemFonts.MenuFont;

        public static void ApplyMenu(ContextMenuStrip menu)
        {
            menu.ShowCheckMargin = false;
            menu.ShowImageMargin = true;
            PrepareMenuSurface(menu);
        }

        public static void ApplyMenuItems(ToolStrip menu)
        {
            if (menu == null)
                return;

            PrepareMenuSurface(menu);

            foreach (ToolStripItem item in menu.Items)
            {
                item.Font = BaseFont;
                item.ForeColor = MenuTextColor(item);
                item.ImageScaling = ToolStripItemImageScaling.None;
                item.TextImageRelation = TextImageRelation.ImageBeforeText;

                if (item is ToolStripSeparator)
                {
                    item.Margin = new Padding(14, 5, 14, 5);
                    continue;
                }

                item.AutoSize = true;
                item.Margin = new Padding(5, 1, 5, 1);
                item.Padding = new Padding(6, 5, 18, 5);

                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.HasDropDownItems)
                {
                    menuItem.Padding = new Padding(6, 5, 26, 5);
                    PrepareMenuSurface(menuItem.DropDown);
                    ApplyMenuItems(menuItem.DropDown);
                    AttachTightSubMenu(menuItem);
                }

                if (IsCaption(item) || IsWarnItem(item))
                {
                    item.Padding = new Padding(6, 5, 18, 5);
                    item.Font = CaptionFont;
                    item.ForeColor = IsWarnItem(item) ? WarnText : MutedText;
                }
            }
        }

        /// <summary>Tag == "warn"：只读的强调提示行，配色走 WarnText。</summary>
        public static bool IsWarnItem(ToolStripItem item)
        {
            return string.Equals(item.Tag as string, "warn", StringComparison.OrdinalIgnoreCase);
        }

        static void PrepareMenuSurface(ToolStrip menu)
        {
            menu.Font = BaseFont;
            menu.BackColor = PanelBack;
            menu.ForeColor = Text;
            menu.Renderer = new TelegramMenuRenderer();
            menu.Padding = new Padding(4, 7, 4, 7);
            menu.ImageScalingSize = new Size(18, 18);

            ToolStripDropDown dropDown = menu as ToolStripDropDown;
            if (dropDown != null)
                dropDown.DropShadowEnabled = false;

            ContextMenuStrip context = menu as ContextMenuStrip;
            if (context != null)
                context.ShowItemToolTips = false;

            if (!string.Equals(menu.Tag as string, MenuSurfaceTag, StringComparison.Ordinal))
            {
                menu.Tag = MenuSurfaceTag;
                menu.SizeChanged += delegate { UpdateRoundedRegion(menu); };
                menu.VisibleChanged += delegate { UpdateRoundedRegion(menu); };

                ToolStripDropDown animatedDropDown = menu as ToolStripDropDown;
                if (animatedDropDown != null)
                {
                    animatedDropDown.Opened += delegate { AnimateMenu(animatedDropDown.Handle, true); };
                    animatedDropDown.Closing += delegate { AnimateMenu(animatedDropDown.Handle, false); };
                }
            }

            UpdateRoundedRegion(menu);
        }

        static void AttachTightSubMenu(ToolStripMenuItem item)
        {
            if (string.Equals(item.Tag as string, "submenu-positioned", StringComparison.Ordinal))
                return;

            item.Tag = "submenu-positioned";
            EventHandler reposition = delegate
            {
                PositionSubMenu(item);
            };
            item.DropDownOpening += reposition;
            item.DropDownOpened += delegate
            {
                PositionSubMenu(item);
                ToolStrip owner = item.Owner;
                if (owner != null && !owner.IsDisposed)
                {
                    try
                    {
                        owner.BeginInvoke((MethodInvoker)delegate { PositionSubMenu(item); });
                        ScheduleSubMenuPosition(item, 1);
                        ScheduleSubMenuPosition(item, 35);
                    }
                    catch { }
                }
            };
        }

        static void ScheduleSubMenuPosition(ToolStripMenuItem item, int delay)
        {
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = Math.Max(1, delay);
            timer.Tick += delegate
            {
                timer.Stop();
                timer.Dispose();
                PositionSubMenu(item);
            };
            timer.Start();
        }

        static void PositionSubMenu(ToolStripMenuItem item)
        {
            ToolStrip owner = item.Owner;
            ToolStripDropDown dropDown = item.DropDown;
            if (owner == null || dropDown == null)
                return;

            try
            {
                const int horizontalOverlap = 2;
                const int verticalLift = 6;

                int dropDownWidth = Math.Max(dropDown.Width, dropDown.GetPreferredSize(Size.Empty).Width);
                int dropDownHeight = Math.Max(dropDown.Height, dropDown.GetPreferredSize(Size.Empty).Height);
                Point ownerTopLeft = owner.PointToScreen(Point.Empty);
                Point itemTopRight = new Point(ownerTopLeft.X + owner.Width - horizontalOverlap, ownerTopLeft.Y + item.Bounds.Top - verticalLift);
                Point itemTopLeft = new Point(ownerTopLeft.X - dropDownWidth + horizontalOverlap, ownerTopLeft.Y + item.Bounds.Top - verticalLift);
                Rectangle screen = Screen.FromPoint(itemTopRight).WorkingArea;
                Point target = itemTopRight;

                if (target.X + dropDownWidth > screen.Right)
                    target = itemTopLeft;

                if (target.Y + dropDownHeight > screen.Bottom)
                    target.Y = Math.Max(screen.Top, screen.Bottom - dropDownHeight);
                if (target.Y < screen.Top)
                    target.Y = screen.Top;

                dropDown.Location = target;
                if (dropDown.Handle != IntPtr.Zero)
                    SetWindowPos(dropDown.Handle, IntPtr.Zero, target.X, target.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        static void UpdateRoundedRegion(ToolStrip menu)
        {
            if (menu.Width <= 0 || menu.Height <= 0)
                return;

            using (var path = RoundedRect(new Rectangle(0, 0, menu.Width, menu.Height), 6))
            {
                Region old = menu.Region;
                menu.Region = new Region(path);
                if (old != null)
                    old.Dispose();
            }
        }

        static void AnimateMenu(IntPtr handle, bool opening)
        {
            if (handle == IntPtr.Zero)
                return;

            try
            {
                AnimateWindow(handle, opening ? 90 : 70, opening ? (AW_BLEND | AW_ACTIVATE) : (AW_BLEND | AW_HIDE));
            }
            catch { }
        }

        public static void ApplyForm(Form form)
        {
            form.Font = BaseFont;
            form.BackColor = WindowBack;
            form.ForeColor = Text;
            form.StartPosition = FormStartPosition.CenterParent;
        }

        public static void ApplyControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.Font = BaseFont;
                control.ForeColor = Text;

                if (control is Button)
                {
                    Button button = (Button)control;
                    // 危险动作（删除）红色描边；其余 DialogResult=OK 的按钮作为主按钮
                    bool danger = IsDangerButton(button);
                    ApplyButton(button, !danger && button.DialogResult == DialogResult.OK);
                    if (danger)
                        ApplyDangerButton(button);
                }
                else if (control is TextBox)
                {
                    ApplyTextBox((TextBox)control);
                }
                else if (control is DataGridView)
                {
                    ApplyGrid((DataGridView)control);
                }
                else if (control is CheckedListBox)
                {
                    ApplyCheckedList((CheckedListBox)control);
                }
                else if (control is Label)
                {
                    control.BackColor = Color.Transparent;
                }

                if (control.HasChildren)
                    ApplyControls(control);
            }
        }

        static bool IsDangerButton(Button button)
        {
            if (button == null || button.Text == null)
                return false;
            string t = button.Text;
            return t.IndexOf("删除", StringComparison.Ordinal) >= 0
                || t.IndexOf("移除", StringComparison.Ordinal) >= 0
                || t.IndexOf("清空", StringComparison.Ordinal) >= 0;
        }

        public static void ApplyDangerButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Danger;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 235, 235);
            button.BackColor = PanelBack;
            button.ForeColor = Danger;
            button.Height = Math.Max(button.Height, 30);
            button.Cursor = Cursors.Hand;
        }

        public static void ApplyButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : HoverBack;
            button.BackColor = primary ? Accent : PanelBack;
            button.ForeColor = primary ? Color.White : Text;
            button.Height = Math.Max(button.Height, 30);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            // 主按钮加粗，视觉上明确「默认动作」
            button.Font = primary ? TitleFont : BaseFont;
        }

        public static void ApplyTextBox(TextBox box)
        {
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Color.White;
            box.ForeColor = Text;
        }

        public static void ApplyGrid(DataGridView grid)
        {
            grid.BorderStyle = BorderStyle.None;
            grid.BackgroundColor = PanelBack;
            grid.GridColor = Border;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(241, 245, 249);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
            grid.ColumnHeadersDefaultCellStyle.Font = TitleFont;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(241, 245, 249);
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.ColumnHeadersHeight = 34;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.BackColor = PanelBack;
            grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
            grid.DefaultCellStyle.SelectionForeColor = Text;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
            grid.RowTemplate.Height = 30;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AllowUserToResizeRows = false;
            grid.ShowCellToolTips = true;
            // 表头扁平化：去掉默认的 3D 凸起，与菜单/按钮的扁平风格保持一致
            grid.AdvancedColumnHeadersBorderStyle.All = DataGridViewAdvancedCellBorderStyle.None;
            grid.AdvancedCellBorderStyle.Top = DataGridViewAdvancedCellBorderStyle.None;
        }

        public static void ApplyCheckedList(CheckedListBox list)
        {
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = PanelBack;
            list.ForeColor = Text;
        }

        public static void StyleDialogButtons(params Button[] buttons)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                ApplyButton(buttons[i], i == 0);
            }
        }

        public static bool IsDanger(ToolStripItem item)
        {
            return string.Equals(item.Tag as string, "danger", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsCaption(ToolStripItem item)
        {
            return string.Equals(item.Tag as string, "caption", StringComparison.OrdinalIgnoreCase);
        }

        public static Color MenuTextColor(ToolStripItem item)
        {
            if (IsDanger(item))
                return Danger;
            // Tag == "warn"：强调但不报错的提示行（如上游端口未监听）
            if (IsWarnItem(item))
                return WarnText;
            if (!item.Enabled || IsCaption(item))
                return MutedText;
            return Text;
        }

        public static Image MenuIcon(string key, bool active)
        {
            string cacheKey = key + ":" + active.ToString();
            Image image;
            if (IconCache.TryGetValue(cacheKey, out image))
                return image;

            image = CreateMenuIcon(key, active);
            IconCache[cacheKey] = image;
            return image;
        }

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            if (diameter <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// 生成菜单图标。
        /// 原实现对每个 key 都画同一个灰色圆点，等于没有图标信息量——
        /// 这里为常用项绘制可区分的矢量图形，并统一 18x18 描边风格。
        /// </summary>
        static Image CreateMenuIcon(string key, bool active)
        {
            const int size = 18;
            var bmp = new Bitmap(size, size);
            Color fill = active ? ActiveGreen : IconText;
            Color accent = active ? ActiveGreen : Accent;

            if (string.Equals(key, "exit", StringComparison.OrdinalIgnoreCase))
                fill = Danger;
            if (string.Equals(key, "status-on", StringComparison.OrdinalIgnoreCase))
                fill = ActiveGreen;
            if (string.Equals(key, "status-off", StringComparison.OrdinalIgnoreCase))
                fill = MutedText;

            using (Graphics g = Graphics.FromImage(bmp))
            using (var pen = new Pen(fill, 1.6F))
            using (var brush = new SolidBrush(fill))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch ((key ?? "").ToLowerInvariant())
                {
                    case "play":                    // 三角形：启动
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(6F, 4F), new PointF(14F, 9F), new PointF(6F, 14F)
                        });
                        break;

                    case "stop":                    // 方块：停止
                        g.FillRectangle(brush, 5.5F, 5.5F, 7F, 7F);
                        break;

                    case "status-on":               // 实心圆：运行中
                        g.FillEllipse(brush, 5F, 5F, 8F, 8F);
                        break;

                    case "status-off":              // 空心圆：已停止
                        g.DrawEllipse(pen, 5F, 5F, 8F, 8F);
                        break;

                    case "proxy":                   // 双向箭头：流量转发
                        g.DrawLine(pen, 3.5F, 6.5F, 13F, 6.5F);
                        g.DrawLine(pen, 10.5F, 4F, 13F, 6.5F);
                        g.DrawLine(pen, 10.5F, 9F, 13F, 6.5F);
                        g.DrawLine(pen, 14.5F, 11.5F, 5F, 11.5F);
                        g.DrawLine(pen, 7.5F, 9F, 5F, 11.5F);
                        g.DrawLine(pen, 7.5F, 14F, 5F, 11.5F);
                        break;

                    case "tun":                     // 网卡板卡 + 引脚：TUN
                        g.DrawRectangle(pen, 3.5F, 4F, 11F, 8.5F);
                        g.DrawLine(pen, 6F, 12.5F, 6F, 15F);
                        g.DrawLine(pen, 9F, 12.5F, 9F, 15F);
                        g.DrawLine(pen, 12F, 12.5F, 12F, 15F);
                        break;

                    case "guard":                   // 盾牌：守护
                        g.DrawLines(pen, new[]
                        {
                            new PointF(9F, 3.5F), new PointF(14F, 5.5F), new PointF(14F, 9.5F),
                            new PointF(9F, 14.5F), new PointF(4F, 9.5F), new PointF(4F, 5.5F),
                            new PointF(9F, 3.5F)
                        });
                        break;

                    case "refresh":                 // 环形箭头：更新
                        g.DrawArc(pen, 4F, 4F, 10F, 10F, 60F, 250F);
                        g.FillPolygon(brush, new[]
                        {
                            new PointF(9.4F, 3.0F), new PointF(14.1F, 4.4F), new PointF(11.4F, 8.1F)
                        });
                        break;

                    case "download":                // 下箭头 + 底座：下载
                        g.DrawLine(pen, 9F, 3.5F, 9F, 10.5F);
                        g.DrawLine(pen, 6F, 7.5F, 9F, 10.5F);
                        g.DrawLine(pen, 12F, 7.5F, 9F, 10.5F);
                        g.DrawLine(pen, 4F, 13.5F, 14F, 13.5F);
                        break;

                    case "profile":                 // 文件夹：配置
                        g.DrawLines(pen, new[]
                        {
                            new PointF(3F, 13.5F), new PointF(3F, 5F), new PointF(7.5F, 5F),
                            new PointF(9F, 7F), new PointF(15F, 7F), new PointF(15F, 13.5F),
                            new PointF(3F, 13.5F)
                        });
                        break;

                    case "list":                    // 列表：三条线
                        g.DrawLine(pen, 6F, 5F, 14F, 5F);
                        g.DrawLine(pen, 6F, 9F, 14F, 9F);
                        g.DrawLine(pen, 6F, 13F, 14F, 13F);
                        g.FillEllipse(brush, 3.2F, 4.2F, 1.6F, 1.6F);
                        g.FillEllipse(brush, 3.2F, 8.2F, 1.6F, 1.6F);
                        g.FillEllipse(brush, 3.2F, 12.2F, 1.6F, 1.6F);
                        break;

                    case "plus":                    // 加号
                        g.DrawLine(pen, 9F, 4.5F, 9F, 13.5F);
                        g.DrawLine(pen, 4.5F, 9F, 13.5F, 9F);
                        break;

                    case "edit":                    // 铅笔
                        g.DrawLine(pen, 5F, 13F, 12F, 6F);
                        g.DrawLine(pen, 10.6F, 4.6F, 13.4F, 7.4F);
                        g.DrawLine(pen, 4.2F, 13.8F, 5.2F, 12.4F);
                        break;

                    case "settings":                // 齿轮（简化）：中心圆 + 四点
                        g.DrawEllipse(pen, 6.5F, 6.5F, 5F, 5F);
                        g.FillEllipse(brush, 8.2F, 3F, 1.6F, 1.6F);
                        g.FillEllipse(brush, 8.2F, 13.4F, 1.6F, 1.6F);
                        g.FillEllipse(brush, 3F, 8.2F, 1.6F, 1.6F);
                        g.FillEllipse(brush, 13.4F, 8.2F, 1.6F, 1.6F);
                        break;

                    case "panel":                   // 窗口
                        g.DrawRectangle(pen, 3.5F, 4.5F, 11F, 9F);
                        g.DrawLine(pen, 3.5F, 7.5F, 14.5F, 7.5F);
                        break;

                    case "power":                   // 电源
                        g.DrawArc(pen, 4F, 4.5F, 10F, 10F, -60F, 300F);
                        g.DrawLine(pen, 9F, 3F, 9F, 8F);
                        break;

                    case "on":                      // 实心圆 + 外圈：打开
                        g.FillEllipse(brush, 6.5F, 6.5F, 5F, 5F);
                        g.DrawEllipse(pen, 4F, 4F, 10F, 10F);
                        break;

                    case "off":                     // 空心圆 + 斜杠：关闭
                        g.DrawEllipse(pen, 4.5F, 4.5F, 9F, 9F);
                        g.DrawLine(pen, 5.5F, 12.5F, 12.5F, 5.5F);
                        break;

                    case "startup":                 // 火箭/上箭头：启动项
                        g.DrawLine(pen, 9F, 14F, 9F, 6F);
                        g.DrawLine(pen, 6F, 9F, 9F, 6F);
                        g.DrawLine(pen, 12F, 9F, 9F, 6F);
                        g.DrawLine(pen, 5F, 15F, 13F, 15F);
                        break;

                    case "mode":                    // 分叉：规则分流
                        g.DrawLine(pen, 4.5F, 9F, 8F, 9F);
                        g.DrawLine(pen, 8F, 9F, 11F, 4.8F);
                        g.DrawLine(pen, 8F, 9F, 11F, 13.2F);
                        g.FillEllipse(brush, 3F, 7.8F, 2.4F, 2.4F);
                        g.FillEllipse(brush, 11.6F, 3.6F, 2.4F, 2.4F);
                        g.FillEllipse(brush, 11.6F, 12F, 2.4F, 2.4F);
                        break;

                    case "appproxy":                // 应用方块 + 箭头
                        g.DrawRectangle(pen, 3.5F, 3.5F, 6F, 6F);
                        g.DrawLine(pen, 11F, 12.5F, 14.5F, 12.5F);
                        g.DrawLine(pen, 12.6F, 10.6F, 14.5F, 12.5F);
                        g.DrawLine(pen, 12.6F, 14.4F, 14.5F, 12.5F);
                        break;

                    case "app":                     // 应用方块
                        g.DrawRectangle(pen, 4F, 4F, 10F, 10F);
                        break;

                    case "empty":                   // 空集
                        g.DrawEllipse(pen, 4F, 4F, 10F, 10F);
                        g.DrawLine(pen, 5.2F, 12.8F, 12.8F, 5.2F);
                        break;

                    case "exit":                    // 门 + 出箭头
                        g.DrawLine(pen, 11.5F, 3.5F, 11.5F, 14.5F);
                        g.DrawLine(pen, 4F, 9F, 10F, 9F);
                        g.DrawLine(pen, 7.5F, 6.5F, 10F, 9F);
                        g.DrawLine(pen, 7.5F, 11.5F, 10F, 9F);
                        break;

                    default:                        // 未定义：保持圆点，避免出现空白
                        g.FillEllipse(brush, 5.5F, 5.5F, 7.5F, 7.5F);
                        break;
                }
            }

            return bmp;
        }
    }

    class TelegramMenuRenderer : ToolStripProfessionalRenderer
    {
        const int IconLeft = 17;
        const int IconSize = 18;

        public TelegramMenuRenderer() : base(new TelegramMenuColorTable()) { }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var brush = new SolidBrush(UiStyles.PanelBack))
            using (var path = UiStyles.RoundedRect(rect, 6))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(Point.Empty, e.ToolStrip.Size);
            using (var brush = new SolidBrush(UiStyles.PanelBack))
                e.Graphics.FillRectangle(brush, rect);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(4, 1, Math.Max(1, e.Item.Width - 8), Math.Max(1, e.Item.Height - 2));
            Color fill = e.Item.Selected && e.Item.Enabled ? UiStyles.HoverBack : UiStyles.PanelBack;
            using (var brush = new SolidBrush(fill))
            using (var path = UiStyles.RoundedRect(rect, 3))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null)
                return;

            Rectangle rect = IconRectangle(e.Item);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawImage(e.Image, rect);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = UiStyles.MenuTextColor(e.Item);
            e.TextRectangle = new Rectangle(e.TextRectangle.X, e.TextRectangle.Y + 3, e.TextRectangle.Width, e.TextRectangle.Height);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? UiStyles.IconText : UiStyles.Border;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Image image = e.Item.Image;
            if (image != null)
            {
                Rectangle rect = IconRectangle(e.Item);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawImage(image, rect);
                return;
            }

            Rectangle markRect = e.ImageRectangle;
            int midY = markRect.Top + markRect.Height / 2;
            Point[] points = new Point[]
            {
                new Point(markRect.Left + 3, midY),
                new Point(markRect.Left + 7, midY + 4),
                new Point(markRect.Right - 3, markRect.Top + 4)
            };
            using (var pen = new Pen(UiStyles.Accent, 2F))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, points);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(UiStyles.Border))
                e.Graphics.DrawLine(pen, 36, y, e.Item.Width - 14, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var pen = new Pen(UiStyles.Border))
            using (var path = UiStyles.RoundedRect(rect, 6))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        static Rectangle IconRectangle(ToolStripItem item)
        {
            int top = Math.Max(0, (item.Height - IconSize) / 2);
            return new Rectangle(IconLeft, top, IconSize, IconSize);
        }
    }

    class TelegramMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return UiStyles.PanelBack; } }
        public override Color ImageMarginGradientBegin { get { return UiStyles.PanelBack; } }
        public override Color ImageMarginGradientMiddle { get { return UiStyles.PanelBack; } }
        public override Color ImageMarginGradientEnd { get { return UiStyles.PanelBack; } }
        public override Color MenuItemSelected { get { return UiStyles.HoverBack; } }
        public override Color MenuBorder { get { return UiStyles.Border; } }
        public override Color MenuItemBorder { get { return UiStyles.HoverBack; } }
    }

    /// <summary>
    /// 管理按应用代理的进程列表（对应 ProxiFyre 的 appNames）。
    /// </summary>
    class AppProxyManagerForm : Form
    {
        CheckedListBox _list;
        TextBox _input;
        List<string> _apps;

        public AppProxyManagerForm(List<string> apps, string endpoint)
        {
            Text = "按应用代理";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 460;
            Height = 460;

            _apps = apps == null ? new List<string>() : new List<string>(apps);

            var hint = new Label
            {
                Left = 14,
                Top = 12,
                Width = 420,
                Height = 34,
                Text = string.IsNullOrEmpty(endpoint)
                    ? "勾选的进程将被代理（进程名需含 .exe）\r\n"
                      + "上游流量由 mihomo 规则 PROCESS-NAME,ProxiFyre.exe 决定出口"
                    : "勾选的进程将被代理，上游 " + endpoint + "\r\n"
                      + "上游流量由 mihomo 规则 PROCESS-NAME,ProxiFyre.exe 决定出口"
            };
            Controls.Add(hint);

            _list = new CheckedListBox();
            _list.Left = 14;
            _list.Top = 54;
            _list.Width = 420;
            _list.Height = 302;
            _list.CheckOnClick = true;
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_list);

            _input = new TextBox { Left = 14, Top = 366, Width = 330 };
            _input.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(_input);

            var addBtn = new Button { Text = "添加", Left = 352, Top = 364, Width = 82 };
            addBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            addBtn.Click += delegate
            {
                string name = _input.Text.Trim();
                if (name.Length == 0)
                    return;
                if (!_apps.Contains(name))
                {
                    _apps.Add(name);
                    _list.Items.Add(name, true);
                    _list.SelectedIndex = _list.Items.Count - 1;
                }
                _input.Clear();
            };
            Controls.Add(addBtn);

            var removeBtn = new Button { Text = "移除选中", Left = 14, Top = 398, Width = 100 };
            removeBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            removeBtn.Click += delegate
            {
                int idx = _list.SelectedIndex;
                if (idx < 0)
                    return;
                _apps.RemoveAt(idx);
                _list.Items.RemoveAt(idx);
            };
            Controls.Add(removeBtn);

            var okBtn = new Button { Text = "确定", Left = 280, Top = 398, Width = 72, DialogResult = DialogResult.OK };
            okBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            var cancelBtn = new Button { Text = "取消", Left = 362, Top = 398, Width = 72, DialogResult = DialogResult.Cancel };
            cancelBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            foreach (var a in _apps)
                _list.Items.Add(a, true);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
        }

        /// <summary>返回当前勾选的应用列表。</summary>
        public List<string> GetAppNames()
        {
            var result = new List<string>();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.GetItemChecked(i))
                    result.Add(_list.Items[i].ToString());
            }
            return result;
        }
    }

    /// <summary>单行文本输入对话框，用于添加应用名。</summary>
    class AppProxyEditForm : Form
    {
        TextBox _box;

        AppProxyEditForm(string title, string value)
        {
            Text = title;
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 420;
            Height = 150;

            var label = new Label { Left = 14, Top = 16, Width = 380, Text = "进程名（例如 game.exe）" };
            _box = new TextBox { Left = 14, Top = 40, Width = 380, Text = value };

            var okBtn = new Button { Text = "确定", Left = 240, Top = 76, Width = 72, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 322, Top = 76, Width = 72, DialogResult = DialogResult.Cancel };

            Controls.Add(label);
            Controls.Add(_box);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
        }

        public static string Prompt(IWin32Window owner, string title, string value)
        {
            using (var dlg = new AppProxyEditForm(title, value))
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK)
                    return null;
                return dlg._box.Text.Trim();
            }
        }
    }

    class PanelSettingsForm : Form
    {
        TextBox _hostBox;
        NumericUpDown _portBox;
        TextBox _pathBox;

        public string PanelHost { get { return _hostBox.Text.Trim(); } }
        public int PanelPort { get { return (int)_portBox.Value; } }
        public string PanelPath { get { return _pathBox.Text.Trim(); } }

        public PanelSettingsForm(string host, int port, string path)
        {
            Text = "面板设置";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 360;
            Height = 190;

            var hostLabel = new Label { Left = 16, Top = 18, Width = 80, Text = "主机" };
            _hostBox = new TextBox { Left = 100, Top = 14, Width = 220, Text = host };

            var portLabel = new Label { Left = 16, Top = 52, Width = 80, Text = "端口" };
            _portBox = new NumericUpDown { Left = 100, Top = 48, Width = 220, Minimum = 1, Maximum = 65535, Value = port };

            var pathLabel = new Label { Left = 16, Top = 86, Width = 80, Text = "路径" };
            _pathBox = new TextBox { Left = 100, Top = 82, Width = 220, Text = path };

            var okButton = new Button { Text = "确定", Left = 164, Top = 118, Width = 72, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "取消", Left = 248, Top = 118, Width = 72, DialogResult = DialogResult.Cancel };

            Controls.Add(hostLabel);
            Controls.Add(_hostBox);
            Controls.Add(portLabel);
            Controls.Add(_portBox);
            Controls.Add(pathLabel);
            Controls.Add(_pathBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);

            AcceptButton = okButton;
            CancelButton = cancelButton;
            UiStyles.ApplyControls(this);
        }
    }

    /// <summary>
    /// 订阅管理。
    /// 相比原始实现补齐了：重复名称/链接校验、删除确认、主机名与链接脱敏展示、
    /// 双击编辑、空列表提示。原实现只做增删改移，缺少任何输入校验，
    /// 容易保存出两条同名订阅或重复链接而无法区分。
    /// </summary>
    class SubscriptionManagerForm : Form
    {
        DataGridView _grid;
        BindingSource _source;
        List<SubscriptionInfo> _items;
        Label _hint;

        public SubscriptionManagerForm(List<SubscriptionInfo> items)
        {
            Text = "订阅管理";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(720, 452);

            _items = items == null ? new List<SubscriptionInfo>() : new List<SubscriptionInfo>(items);
            _source = new BindingSource();
            _source.DataSource = _items;

            _hint = new Label();
            _hint.Left = 14;
            _hint.Top = 12;
            _hint.Width = 692;
            _hint.Height = 18;
            _hint.Text = "按「更新此订阅」拉取节点并写入当前配置；双击可编辑名称与链接。";
            _hint.ForeColor = UiStyles.MutedText;
            Controls.Add(_hint);

            _grid = new DataGridView();
            _grid.Left = 14;
            _grid.Top = 36;
            _grid.Width = 692;
            _grid.Height = 348;
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.ReadOnly = true;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.RowHeadersVisible = false;
            _grid.DataSource = _source;

            var nameCol = new DataGridViewTextBoxColumn();
            nameCol.DataPropertyName = "Name";
            nameCol.HeaderText = "名称";
            nameCol.Width = 200;

            var hostCol = new DataGridViewTextBoxColumn();
            hostCol.Name = "HostColumn";
            hostCol.HeaderText = "服务器";
            hostCol.Width = 200;

            var urlCol = new DataGridViewTextBoxColumn();
            urlCol.Name = "UrlColumn";
            urlCol.HeaderText = "订阅链接（已脱敏）";
            urlCol.Width = 260;

            _grid.Columns.Add(nameCol);
            _grid.Columns.Add(hostCol);
            _grid.Columns.Add(urlCol);

            // 派生列用手工填值：主键仍是 Name/Url，避免引入会随序列化泄漏的额外字段
            _grid.CellFormatting += delegate(object s, DataGridViewCellFormattingEventArgs ev)
            {
                if (ev.RowIndex < 0 || ev.RowIndex >= _items.Count)
                    return;
                SubscriptionInfo item = _items[ev.RowIndex];
                if (_grid.Columns[ev.ColumnIndex].Name == "HostColumn")
                {
                    string host = ExtractHostStatic(item.Url);
                    ev.Value = host.Length == 0 ? "—" : host;
                    ev.FormattingApplied = true;
                }
                else if (_grid.Columns[ev.ColumnIndex].Name == "UrlColumn")
                {
                    ev.Value = MaskSubscriptionUrl(item.Url);
                    ev.FormattingApplied = true;
                }
            };

            _grid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs ev)
            {
                if (ev.RowIndex >= 0)
                    EditAt(ev.RowIndex);
            };

            var addBtn = new Button { Text = "新增", Left = 14, Top = 396, Width = 76 };
            var editBtn = new Button { Text = "编辑", Left = 98, Top = 396, Width = 76 };
            var delBtn = new Button { Text = "删除", Left = 182, Top = 396, Width = 76 };
            var upBtn = new Button { Text = "上移", Left = 266, Top = 396, Width = 76 };
            var downBtn = new Button { Text = "下移", Left = 350, Top = 396, Width = 76 };
            var okBtn = new Button { Text = "保存", Left = 546, Top = 396, Width = 76, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 630, Top = 396, Width = 76, DialogResult = DialogResult.Cancel };

            addBtn.Click += delegate
            {
                using (var dlg = new SubscriptionEditForm(new SubscriptionInfo { Name = "", Url = "" }, true))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;

                    string reason;
                    if (!ValidateCandidate(dlg.Value, -1, out reason))
                    {
                        MessageBox.Show(this, reason, "订阅无效",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    _items.Add(dlg.Value);
                    _source.ResetBindings(false);
                    SelectRow(_items.Count - 1);
                }
            };

            editBtn.Click += delegate
            {
                int idx = CurrentIndex();
                if (idx >= 0)
                    EditAt(idx);
            };

            delBtn.Click += delegate
            {
                int idx = CurrentIndex();
                if (idx < 0)
                    return;

                SubscriptionInfo item = _items[idx];
                if (MessageBox.Show(this,
                    "确定删除订阅 \"" + (string.IsNullOrEmpty(item.Name) ? "(未命名)" : item.Name) + "\" 吗？\r\n"
                    + "仅从列表移除，不会改动已合并进配置的节点。",
                    "删除订阅",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                _items.RemoveAt(idx);
                _source.ResetBindings(false);
                if (_items.Count > 0)
                    SelectRow(Math.Min(idx, _items.Count - 1));
            };

            upBtn.Click += delegate { MoveSelected(-1); };
            downBtn.Click += delegate { MoveSelected(1); };

            okBtn.Click += delegate
            {
                if (!ValidateAll())
                    DialogResult = DialogResult.None;
            };

            Controls.Add(_grid);
            Controls.Add(addBtn);
            Controls.Add(editBtn);
            Controls.Add(delBtn);
            Controls.Add(upBtn);
            Controls.Add(downBtn);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
            if (_items.Count > 0)
                SelectRow(0);
        }

        void EditAt(int idx)
        {
            if (idx < 0 || idx >= _items.Count)
                return;

            SubscriptionInfo current = _items[idx];
            using (var dlg = new SubscriptionEditForm(
                new SubscriptionInfo { Name = current.Name, Url = current.Url }, false))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;

                string reason;
                if (!ValidateCandidate(dlg.Value, idx, out reason))
                {
                    MessageBox.Show(this, reason, "订阅无效",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                _items[idx] = dlg.Value;
                _source.ResetBindings(false);
                SelectRow(idx);
            }
        }

        /// <summary>校验单条订阅；skipIndex 用于编辑时排除自身。返回 false 时 reason 给出原因。</summary>
        bool ValidateCandidate(SubscriptionInfo candidate, int skipIndex, out string reason)
        {
            reason = null;

            if (candidate == null)
            {
                reason = "订阅内容为空。";
                return false;
            }

            candidate.Name = (candidate.Name ?? "").Trim();
            candidate.Url = (candidate.Url ?? "").Trim();

            if (candidate.Name.Length == 0)
            {
                reason = "订阅名称不能为空。";
                return false;
            }
            if (candidate.Url.Length == 0)
            {
                reason = "订阅链接不能为空。";
                return false;
            }
            if (!Regex.IsMatch(candidate.Url, @"^https?://", RegexOptions.IgnoreCase))
            {
                reason = "订阅链接必须以 http:// 或 https:// 开头。";
                return false;
            }
            if (candidate.Url.IndexOf(' ') >= 0)
            {
                reason = "订阅链接不能包含空格。";
                return false;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                if (i == skipIndex)
                    continue;

                if (string.Equals(_items[i].Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "已存在同名订阅：\r\n" + candidate.Name;
                    return false;
                }
                if (string.Equals((_items[i].Url ?? "").Trim(), candidate.Url, StringComparison.Ordinal))
                {
                    reason = "该订阅链接已存在：\r\n" + candidate.Name;
                    return false;
                }
            }

            return true;
        }

        bool ValidateAll()
        {
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < _items.Count; i++)
            {
                SubscriptionInfo item = _items[i];
                item.Name = (item.Name ?? "").Trim();
                item.Url = (item.Url ?? "").Trim();

                if (item.Name.Length == 0 || item.Url.Length == 0)
                {
                    MessageBox.Show(this, "第 " + (i + 1) + " 条订阅的名称或链接为空。",
                        "订阅无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (item.Url.Length > 0 && !Regex.IsMatch(item.Url, @"^https?://", RegexOptions.IgnoreCase))
                {
                    MessageBox.Show(this,
                        "第 " + (i + 1) + " 条订阅的链接不是有效地址：\r\n" + item.Url,
                        "订阅无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (!seenNames.Add(item.Name))
                {
                    MessageBox.Show(this, "订阅名称重复：\r\n" + item.Name,
                        "订阅重复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (!seenUrls.Add(item.Url))
                {
                    MessageBox.Show(this, "订阅链接重复：\r\n" + item.Name,
                        "订阅重复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }

            _source.ResetBindings(false);
            return true;
        }

        int CurrentIndex()
        {
            if (_grid.CurrentRow == null) return -1;
            int idx = _grid.CurrentRow.Index;
            if (idx < 0 || idx >= _items.Count) return -1;
            return idx;
        }

        void SelectRow(int idx)
        {
            try
            {
                if (idx >= 0 && idx < _grid.Rows.Count)
                    _grid.CurrentCell = _grid.Rows[idx].Cells[0];
            }
            catch { }
        }

        void MoveSelected(int delta)
        {
            int idx = CurrentIndex();
            if (idx < 0) return;
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _items.Count) return;

            var temp = _items[idx];
            _items[idx] = _items[newIdx];
            _items[newIdx] = temp;
            _source.ResetBindings(false);
            SelectRow(newIdx);
        }

        /// <summary>订阅链接脱敏：只保留协议与主机，token 用省略号替代。</summary>
        internal static string MaskSubscriptionUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "";

            try
            {
                var m = Regex.Match(url, @"^(https?://[^/?#]+)(.*)$", RegexOptions.IgnoreCase);
                if (!m.Success)
                    return url.Length > 48 ? url.Substring(0, 45) + "…" : url;

                string basePart = m.Groups[1].Value;
                string rest = m.Groups[2].Value;
                if (rest.Length == 0)
                    return basePart;

                // 查询串往往承载 token，整体折叠为固定占位
                if (rest.IndexOf('?') >= 0)
                    return basePart + "/…?…";

                if (rest.Length > 24)
                    return basePart + "/…" + rest.Substring(rest.Length - 6);
                return basePart + rest;
            }
            catch
            {
                return url;
            }
        }

        internal static string ExtractHostStatic(string url)
        {
            if (string.IsNullOrEmpty(url))
                return "";
            try
            {
                var m = Regex.Match(url, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://([^/?#]+)");
                if (!m.Success)
                    return "";
                string hostPort = m.Groups[1].Value;
                int at = hostPort.LastIndexOf('@');
                if (at >= 0)
                    hostPort = hostPort.Substring(at + 1);
                return hostPort;
            }
            catch
            {
                return "";
            }
        }

        public List<SubscriptionInfo> GetSubscriptions()
        {
            return new List<SubscriptionInfo>(_items);
        }
    }

    class SubscriptionEditForm : Form
    {
        TextBox _nameBox;
        TextBox _urlBox;
        Label _hint;
        public SubscriptionInfo Value { get; private set; }

        public SubscriptionEditForm(SubscriptionInfo item, bool isNew)
        {
            Text = isNew ? "新增订阅" : "编辑订阅";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(636, 208);

            var nameLabel = new Label { Left = 16, Top = 22, Width = 68, Text = "名称" };
            _nameBox = new TextBox { Left = 90, Top = 18, Width = 528, Text = item.Name };
            _nameBox.MaxLength = 120;

            var urlLabel = new Label { Left = 16, Top = 60, Width = 68, Text = "链接" };
            _urlBox = new TextBox { Left = 90, Top = 56, Width = 528, Text = item.Url };
            _urlBox.MaxLength = 2048;

            _hint = new Label();
            _hint.Left = 90;
            _hint.Top = 84;
            _hint.Width = 528;
            _hint.Height = 34;
            _hint.ForeColor = UiStyles.MutedText;
            _hint.Text = "链接需以 http:// 或 https:// 开头，通常是机场提供的订阅地址。";

            var okBtn = new Button { Text = "确定", Left = 456, Top = 150, Width = 76, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 542, Top = 150, Width = 76, DialogResult = DialogResult.Cancel };

            okBtn.Click += delegate
            {
                string name = (_nameBox.Text ?? "").Trim();
                string url = (_urlBox.Text ?? "").Trim();

                if (name.Length == 0 || url.Length == 0)
                {
                    MessageBox.Show(this, "名称和链接不能为空。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
                if (!Regex.IsMatch(url, @"^https?://", RegexOptions.IgnoreCase))
                {
                    MessageBox.Show(this, "链接需以 http:// 或 https:// 开头。", "链接格式不正确",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }

                Value = new SubscriptionInfo { Name = name, Url = url };
            };

            Controls.Add(nameLabel);
            Controls.Add(_nameBox);
            Controls.Add(urlLabel);
            Controls.Add(_urlBox);
            Controls.Add(_hint);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
            _nameBox.Select();
        }
    }

    class ConfigProfileManagerForm : Form
    {
        DataGridView _grid;
        BindingSource _source;
        List<ConfigProfile> _items;
        string _activeConfigPath;
        string _basePath;
        Label _activeLabel;

        public ConfigProfileManagerForm(List<ConfigProfile> items, string activeConfigPath, string basePath)
        {
            Text = "配置切换";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 700;
            Height = 430;

            _items = items == null ? new List<ConfigProfile>() : new List<ConfigProfile>(items);
            if (_items.Count == 0)
            {
                _items.Add(new ConfigProfile { Name = "默认配置", Path = "config.yaml" });
            }
            _activeConfigPath = activeConfigPath;
            _basePath = basePath;
            EnsureActiveProfile();

            _source = new BindingSource();
            _source.DataSource = _items;

            _activeLabel = new Label { Left = 12, Top = 12, Width = 660, Text = "当前活动配置: " + GetActiveName() };
            Controls.Add(_activeLabel);

            _grid = new DataGridView();
            _grid.Left = 12;
            _grid.Top = 36;
            _grid.Width = 660;
            _grid.Height = 300;
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.ReadOnly = true;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.DataSource = _source;

            var nameCol = new DataGridViewTextBoxColumn();
            nameCol.DataPropertyName = "Name";
            nameCol.HeaderText = "名称";
            nameCol.Width = 180;

            var pathCol = new DataGridViewTextBoxColumn();
            pathCol.DataPropertyName = "Path";
            pathCol.HeaderText = "配置路径";
            pathCol.Width = 430;

            _grid.Columns.Add(nameCol);
            _grid.Columns.Add(pathCol);
            Controls.Add(_grid);

            var addBtn = new Button { Text = "新增", Left = 12, Top = 346, Width = 72 };
            var editBtn = new Button { Text = "编辑", Left = 92, Top = 346, Width = 72 };
            var delBtn = new Button { Text = "删除", Left = 172, Top = 346, Width = 72 };
            var upBtn = new Button { Text = "上移", Left = 252, Top = 346, Width = 72 };
            var downBtn = new Button { Text = "下移", Left = 332, Top = 346, Width = 72 };
            var setActiveBtn = new Button { Text = "设为当前", Left = 412, Top = 346, Width = 88 };
            var okBtn = new Button { Text = "确定", Left = 520, Top = 346, Width = 72, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 600, Top = 346, Width = 72, DialogResult = DialogResult.Cancel };

            addBtn.Click += delegate
            {
                using (var dlg = new ConfigProfileEditForm(new ConfigProfile { Name = "", Path = "config.yaml" }, _basePath, true))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _items.Add(dlg.Value);
                        _source.ResetBindings(false);
                    }
                }
            };

            editBtn.Click += delegate
            {
                int idx = CurrentIndex();
                if (idx < 0) return;
                var current = _items[idx];
                using (var dlg = new ConfigProfileEditForm(new ConfigProfile { Name = current.Name, Path = current.Path }, _basePath, false))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        bool wasActive = IsActiveProfile(current.Path);
                        _items[idx] = dlg.Value;
                        if (wasActive)
                            _activeConfigPath = ResolvePath(dlg.Value.Path);
                        _source.ResetBindings(false);
                        UpdateActiveLabel();
                    }
                }
            };

            delBtn.Click += delegate
            {
                int idx = CurrentIndex();
                if (idx < 0) return;
                bool deletingActive = IsActiveProfile(_items[idx].Path);
                if (MessageBox.Show(this,
                    "确定删除配置项 \"" + _items[idx].Name + "\" 吗？\r\n不会删除实际配置文件。",
                    "删除配置项",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    return;
                }

                _items.RemoveAt(idx);
                if (_items.Count == 0)
                {
                    _items.Add(new ConfigProfile { Name = "默认配置", Path = "config.yaml" });
                    deletingActive = true;
                }
                if (deletingActive)
                    _activeConfigPath = ResolvePath(_items[0].Path);
                _source.ResetBindings(false);
                UpdateActiveLabel();
            };

            upBtn.Click += delegate { MoveSelected(-1); };
            downBtn.Click += delegate { MoveSelected(1); };

            setActiveBtn.Click += delegate
            {
                int idx = CurrentIndex();
                if (idx < 0) return;
                string selectedPath = ResolvePath(_items[idx].Path);
                if (!File.Exists(selectedPath))
                {
                    MessageBox.Show(this,
                        "配置文件不存在，不能设为当前配置:\r\n" + selectedPath,
                        "配置文件不存在",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
                _activeConfigPath = ResolvePath(_items[idx].Path);
                UpdateActiveLabel();
            };

            okBtn.Click += delegate
            {
                if (!ValidateBeforeClose())
                {
                    DialogResult = DialogResult.None;
                    return;
                }
            };

            Controls.Add(addBtn);
            Controls.Add(editBtn);
            Controls.Add(delBtn);
            Controls.Add(upBtn);
            Controls.Add(downBtn);
            Controls.Add(setActiveBtn);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            SelectActiveProfile();
            UiStyles.ApplyControls(this);
        }

        int CurrentIndex()
        {
            if (_grid.CurrentRow == null) return -1;
            int idx = _grid.CurrentRow.Index;
            if (idx < 0 || idx >= _items.Count) return -1;
            return idx;
        }

        void MoveSelected(int delta)
        {
            int idx = CurrentIndex();
            if (idx < 0) return;
            int newIdx = idx + delta;
            if (newIdx < 0 || newIdx >= _items.Count) return;

            var temp = _items[idx];
            _items[idx] = _items[newIdx];
            _items[newIdx] = temp;
            _source.ResetBindings(false);
            _grid.CurrentCell = _grid.Rows[newIdx].Cells[0];
        }

        void EnsureActiveProfile()
        {
            if (_items.Count == 0)
            {
                _items.Add(new ConfigProfile { Name = "默认配置", Path = "config.yaml" });
            }

            string active = ResolvePath(_activeConfigPath);
            for (int i = 0; i < _items.Count; i++)
            {
                if (PathsEqual(ResolvePath(_items[i].Path), active))
                    return;
            }

            _activeConfigPath = ResolvePath(_items[0].Path);
        }

        void SelectActiveProfile()
        {
            try
            {
                string active = ResolvePath(_activeConfigPath);
                for (int i = 0; i < _items.Count; i++)
                {
                    if (PathsEqual(ResolvePath(_items[i].Path), active))
                    {
                        if (_grid.Rows.Count > i)
                            _grid.CurrentCell = _grid.Rows[i].Cells[0];
                        return;
                    }
                }
            }
            catch { }
        }

        void UpdateActiveLabel()
        {
            if (_activeLabel != null)
                _activeLabel.Text = "当前活动配置: " + GetActiveName();
        }

        bool IsActiveProfile(string path)
        {
            return PathsEqual(ResolvePath(path), ResolvePath(_activeConfigPath));
        }

        bool ValidateBeforeClose()
        {
            if (_items.Count == 0)
                _items.Add(new ConfigProfile { Name = "默认配置", Path = "config.yaml" });

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            bool activeFound = false;

            for (int i = 0; i < _items.Count; i++)
            {
                ConfigProfile item = _items[i];
                item.Name = item.Name == null ? "" : item.Name.Trim();
                item.Path = NormalizeStoredPath(item.Path);

                if (item.Name.Length == 0 || item.Path.Length == 0)
                {
                    MessageBox.Show(this, "配置名称和路径不能为空。", "配置无效",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (seenNames.Contains(item.Name))
                {
                    MessageBox.Show(this, "配置名称重复:\r\n" + item.Name, "配置重复",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                seenNames.Add(item.Name);

                string resolvedPath = ResolvePath(item.Path);
                string normalizedPath = NormalizeForComparison(resolvedPath);
                if (seenPaths.Contains(normalizedPath))
                {
                    MessageBox.Show(this, "配置路径重复:\r\n" + item.Path, "配置重复",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                seenPaths.Add(normalizedPath);

                if (IsActiveProfile(item.Path))
                {
                    activeFound = true;
                    _activeConfigPath = resolvedPath;
                }

                if (!File.Exists(resolvedPath))
                    missing.Add(item.Name + " -> " + item.Path);
            }

            if (!activeFound)
            {
                _activeConfigPath = ResolvePath(_items[0].Path);
                activeFound = true;
            }

            if (!File.Exists(ResolvePath(_activeConfigPath)))
            {
                MessageBox.Show(this,
                    "当前活动配置文件不存在，无法切换:\r\n" + ResolvePath(_activeConfigPath),
                    "配置文件不存在",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            if (missing.Count > 0)
            {
                var sb = new StringBuilder();
                int count = Math.Min(missing.Count, 8);
                for (int i = 0; i < count; i++)
                    sb.Append(" - ").Append(missing[i]).Append("\r\n");
                if (missing.Count > count)
                    sb.Append(" - ... 还有 ").Append(missing.Count - count).Append(" 项\r\n");

                if (MessageBox.Show(this,
                    "以下备用配置文件不存在，仍然保存列表吗？\r\n\r\n" + sb,
                    "备用配置不存在",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return false;
                }
            }

            _source.ResetBindings(false);
            UpdateActiveLabel();
            return true;
        }

        string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return Path.Combine(_basePath, "config.yaml");
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(_basePath, path);
        }

        string NormalizeStoredPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            path = path.Trim();
            try
            {
                string fullPath = Path.GetFullPath(ResolvePath(path));
                string fullBase = Path.GetFullPath(_basePath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
                    return fullPath.Substring(fullBase.Length).Replace('\\', '/');
            }
            catch { }
            return path.Replace('\\', '/');
        }

        string NormalizeForComparison(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path == null ? "" : path.TrimEnd('\\', '/');
            }
        }

        bool PathsEqual(string left, string right)
        {
            return string.Equals(NormalizeForComparison(left), NormalizeForComparison(right), StringComparison.OrdinalIgnoreCase);
        }

        string GetActiveName()
        {
            string active = ResolvePath(_activeConfigPath);
            for (int i = 0; i < _items.Count; i++)
            {
                if (PathsEqual(ResolvePath(_items[i].Path), active))
                {
                    return _items[i].Name + " (" + _items[i].Path + ")";
                }
            }
            return active;
        }

        public List<ConfigProfile> GetProfiles()
        {
            return new List<ConfigProfile>(_items);
        }

        public string GetActiveConfigPath()
        {
            return _activeConfigPath;
        }
    }

    class ConfigProfileEditForm : Form
    {
        TextBox _nameBox;
        TextBox _pathBox;
        string _basePath;
        public ConfigProfile Value { get; private set; }

        public ConfigProfileEditForm(ConfigProfile item, string basePath, bool isNew)
        {
            Text = isNew ? "新增配置" : "编辑配置";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 640;
            Height = 220;

            _basePath = basePath;

            var nameLabel = new Label { Left = 12, Top = 18, Width = 80, Text = "名称" };
            _nameBox = new TextBox { Left = 92, Top = 14, Width = 520, Text = item.Name };

            var pathLabel = new Label { Left = 12, Top = 54, Width = 80, Text = "配置文件" };
            _pathBox = new TextBox { Left = 92, Top = 50, Width = 440, Text = item.Path };

            var browseBtn = new Button { Text = "浏览", Left = 540, Top = 48, Width = 72 };
            browseBtn.Click += delegate
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Filter = "YAML 配置 (*.yaml;*.yml)|*.yaml;*.yml|所有文件|*.*";
                    ofd.InitialDirectory = Directory.Exists(_basePath) ? _basePath : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        _pathBox.Text = MakeRelativeOrAbsolute(ofd.FileName);
                    }
                }
            };

            var okBtn = new Button { Text = "确定", Left = 460, Top = 98, Width = 72, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 540, Top = 98, Width = 72, DialogResult = DialogResult.Cancel };

            okBtn.Click += delegate
            {
                Value = new ConfigProfile { Name = _nameBox.Text.Trim(), Path = _pathBox.Text.Trim() };
                if (Value.Name.Length == 0 || Value.Path.Length == 0)
                {
                    MessageBox.Show(this, "名称和配置文件不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
            };

            Controls.Add(nameLabel);
            Controls.Add(_nameBox);
            Controls.Add(pathLabel);
            Controls.Add(_pathBox);
            Controls.Add(browseBtn);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
        }

        string MakeRelativeOrAbsolute(string file)
        {
            try
            {
                string basePath = _basePath;
                if (file.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                {
                    return file.Substring(basePath.Length).TrimStart('\\', '/');
                }
            }
            catch { }
            return file;
        }
    }

    class AssetUpdateForm : Form
    {
        CheckedListBox _list;
        List<AssetUpdateOption> _items;

        public AssetUpdateForm()
        {
            Text = "一键更新组件";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 720;
            Height = 360;

            _items = new List<AssetUpdateOption>();
            _items.Add(new AssetUpdateOption
            {
                DisplayName = "mihomo 内核",
                ApiUrl = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest",
                AssetName = "mihomo.exe",
                MatchPattern = @"mihomo-windows-amd64-compatible-.*\.zip$",
                TargetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mihomo.exe"),
                IsCore = true,
                IsZip = true,
                Selected = true
            });
            _items.Add(new AssetUpdateOption
            {
                DisplayName = "GeoLite2-Country.mmdb",
                ApiUrl = "https://api.github.com/repos/P3TERX/GeoLite.mmdb/releases/latest",
                AssetName = "GeoLite2-Country.mmdb",
                TargetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeoLite2-Country.mmdb"),
                Selected = true
            });
            _items.Add(new AssetUpdateOption
            {
                DisplayName = "GeoLite2-ASN.mmdb",
                ApiUrl = "https://api.github.com/repos/P3TERX/GeoLite.mmdb/releases/latest",
                AssetName = "GeoLite2-ASN.mmdb",
                TargetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeoLite2-ASN.mmdb"),
                Selected = true
            });
            _items.Add(new AssetUpdateOption
            {
                DisplayName = "geosite.dat",
                ApiUrl = "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest",
                AssetName = "geosite.dat",
                TargetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "geosite.dat"),
                Selected = true
            });
            _items.Add(new AssetUpdateOption
            {
                DisplayName = "geoip.dat",
                ApiUrl = "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest",
                AssetName = "geoip.dat",
                TargetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "geoip.dat"),
                Selected = true
            });
            _items.Add(new AssetUpdateOption
            {
                DisplayName = "country.mmdb",
                ApiUrl = "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest",
                AssetName = "country.mmdb",
                TargetPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "country.mmdb"),
                Selected = true
            });

            _list = new CheckedListBox();
            _list.Left = 12;
            _list.Top = 12;
            _list.Width = 670;
            _list.Height = 250;
            _list.CheckOnClick = true;
            _list.DisplayMember = "DisplayName";

            foreach (var item in _items)
            {
                _list.Items.Add(item, item.Selected);
            }

            var allBtn = new Button { Text = "全选", Left = 12, Top = 272, Width = 72 };
            var noneBtn = new Button { Text = "全不选", Left = 92, Top = 272, Width = 72 };
            var okBtn = new Button { Text = "开始更新", Left = 520, Top = 272, Width = 72, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 610, Top = 272, Width = 72, DialogResult = DialogResult.Cancel };

            allBtn.Click += delegate
            {
                for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true);
            };
            noneBtn.Click += delegate
            {
                for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false);
            };

            Controls.Add(_list);
            Controls.Add(allBtn);
            Controls.Add(noneBtn);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
        }

        public List<AssetUpdateOption> GetOptions()
        {
            var result = new List<AssetUpdateOption>();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.GetItemChecked(i))
                {
                    result.Add((AssetUpdateOption)_list.Items[i]);
                }
            }
            return result;
        }
    }
}
