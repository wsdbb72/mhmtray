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
        bool _cachedSystemProxyReadOk;
        bool _cachedSystemProxyEnabled;
        string _cachedSystemProxyServer;
        DateTime _lastSystemProxyReadUtc;

        bool _isAdmin;

        ToolStripMenuItem _statusItem;
        ToolStripMenuItem _startStopItem;
        ToolStripMenuItem _tunItem;
        ToolStripMenuItem _proxyItem;
        ToolStripMenuItem _proxyGuardItem;
        ToolStripMenuItem _subMenu;
        ToolStripMenuItem _subMgrItem;
        ToolStripMenuItem _profileItem;
        ToolStripMenuItem _autoStartItem;
        ToolStripMenuItem _runMihomoItem;
        ToolStripMenuItem _panelSettingsItem;
        ToolStripMenuItem _updateAssetsItem;

        Icon _iconRunning;
        Icon _iconStopped;
        Icon _iconWarn;

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

        // ──── P/Invoke for system proxy refresh ────

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        const int INTERNET_OPTION_REFRESH = 37;
        const int SystemProxyCacheMilliseconds = 1500;

        static readonly Regex TunEnableRegex = new Regex(
            @"(?m)(^tun:\s*\r?\n(?:[ \t]+[^\r\n]*(?:\r?\n))*?[ \t]+enable:\s*)(true|false)",
            RegexOptions.IgnoreCase);
        static readonly Regex HttpPortRegex = new Regex(@"(?m)^port:\s*(\d+)");

        // ──── Constructor ────

        public MainForm()
        {
            _basePath = AppDomain.CurrentDomain.BaseDirectory;
            _configPath = Path.Combine(_basePath, "config.yaml");
            _mihomoExePath = Path.Combine(_basePath, "mihomo.exe");
            _trayConfigPath = Path.Combine(_basePath, "tray-config.json");
            _exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
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

            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Load += delegate { this.Hide(); };

            _trayIcon = new NotifyIcon();
            _trayIcon.Visible = true;

            _iconRunning = CreateStatusLightIcon(Color.FromArgb(52, 199, 89), Color.FromArgb(36, 146, 68));
            _iconStopped = CreateStatusLightIcon(Color.FromArgb(151, 160, 168), Color.FromArgb(112, 121, 128));
            _iconWarn = CreateStatusLightIcon(Color.FromArgb(52, 199, 89), Color.FromArgb(36, 146, 68));

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
            if (_systemProxyGuardTimer != null) { _systemProxyGuardTimer.Stop(); _systemProxyGuardTimer.Dispose(); _systemProxyGuardTimer = null; }
            if (IsMihomoSystemProxyActive())
            {
                try { SetSystemProxy(false); } catch { }
            }
            KillMihomo();
            if (_iconRunning != null) { _iconRunning.Dispose(); _iconRunning = null; }
            if (_iconWarn != null) { _iconWarn.Dispose(); _iconWarn = null; }
            if (_iconStopped != null) { _iconStopped.Dispose(); _iconStopped = null; }
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
            _lastMihomoProcessLookupUtc = DateTime.MinValue;
            RefreshUI();
            RefreshSubscriptions();
        }

        // ──── Refresh UI ────

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
            }
            else
            {
                foreach (var sub in subs)
                {
                    var subRef = sub;
                    var item = new ToolStripMenuItem(sub.Name, null,
                        delegate { OnUpdateSubscription(subRef); });
                    item.Image = UiStyles.MenuIcon("refresh", false);
                    _subMenu.DropDownItems.Add(item);
                }
                _subMenu.DropDownItems.Add(new ToolStripSeparator());
                var updateAll = new ToolStripMenuItem("更新全部", null, OnUpdateAllSubscriptions);
                updateAll.Image = UiStyles.MenuIcon("download", false);
                _subMenu.DropDownItems.Add(updateAll);
            }

            UiStyles.ApplyMenuItems(_subMenu.DropDown);
        }

        void RefreshUI()
        {
            bool running = IsMihomoRunning();
            bool tunOn = ReadTunStatus();
            bool proxyOn = IsMihomoSystemProxyActive();
            int httpPort = ReadHttpPort();
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
                if (!tunOn && !proxyOn)
                    _trayIcon.Icon = _iconWarn;
                else
                    _trayIcon.Icon = _iconRunning;
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

            _tunItem.Checked = tunOn;
            _tunItem.Text = tunOn ? "TUN 模式已开启" : "TUN 模式";
            _tunItem.Image = UiStyles.MenuIcon("tun", tunOn);

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
                _autoStartItem.Checked = IsAutoStartEnabled();
                _autoStartItem.Image = UiStyles.MenuIcon("power", _autoStartItem.Checked);
            }
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
                return true;

            TunConflictAction action = ShowTunConflictDialog(conflicts);
            if (action == TunConflictAction.Cancel)
                return false;

            if (action == TunConflictAction.Continue)
                return true;

            return StopTunConflictProcesses(conflicts);
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

        // ──── Process Checks ────

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
            string[] names = { "mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta" };
            foreach (string name in names)
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    if (IsManagedMihomoProcess(p))
                        return p;

                    try { p.Dispose(); } catch { }
                }
            }
            return null;
        }

        void KillMihomo()
        {
            try
            {
                if (_mihomoProcess != null)
                {
                    try
                    {
                        if (!_mihomoProcess.HasExited && IsManagedMihomoProcess(_mihomoProcess))
                            _mihomoProcess.Kill();
                    }
                    catch { }
                }
            }
            catch { }

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

                string processPath = process.MainModule.FileName;
                return PathsEqual(processPath, _mihomoExePath) || IsPathInsideBasePath(processPath);
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
                bool coreSelected = HasCoreUpdate(options);
                bool coreStopped = false;
                bool wasRunning = false;
                var errors = new StringBuilder();

                try
                {
                    foreach (var opt in options)
                    {
                        try
                        {
                            string apiJson = DownloadString(opt.ApiUrl);
                            if (string.IsNullOrEmpty(apiJson))
                                throw new Exception("API 返回空");

                            if (apiJson.Contains("\"message\""))
                                throw new Exception(ExtractApiError(apiJson));

                            string downloadUrl = FindAssetUrl(apiJson, opt);
                            if (string.IsNullOrEmpty(downloadUrl))
                                throw new Exception("未找到匹配的下载地址");

                            Action beforeReplace = null;
                            if (opt.IsCore)
                            {
                                beforeReplace = delegate
                                {
                                    if (coreStopped)
                                        return;

                                    bool stoppedForReplace = true;
                                    self.Invoke(new Action(delegate
                                    {
                                        _lastMihomoProcessLookupUtc = DateTime.MinValue;
                                        wasRunning = IsMihomoRunning();
                                        if (wasRunning)
                                        {
                                            StopMihomo();
                                            _lastMihomoProcessLookupUtc = DateTime.MinValue;
                                            stoppedForReplace = !IsMihomoRunning();
                                        }
                                    }));

                                    if (!stoppedForReplace)
                                        throw new Exception("mihomo.exe 仍在运行，无法替换核心");

                                    coreStopped = wasRunning;
                                    if (wasRunning)
                                        System.Threading.Thread.Sleep(800);
                                };
                            }

                            DownloadFile(downloadUrl, opt.IsZip || opt.IsGz, opt.IsGz, opt.TargetPath, beforeReplace);
                            if (opt.IsCore) changedCore = true;
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
                    FinishAssetUpdate(coreSelected, coreStopped, wasRunning, changedCore, errors);
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

        void FinishAssetUpdate(bool coreSelected, bool coreStopped, bool wasRunning, bool changedCore, StringBuilder errors)
        {
            try
            {
                if (coreSelected && coreStopped && wasRunning)
                    StartMihomo();

                ApplySavedSystemProxyMode();

                if (errors.Length > 0)
                    MessageBox.Show(errors.ToString(), "部分更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        changedCore ? "核心和组件更新完成" : "组件更新完成",
                        ToolTipIcon.Info);

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

        bool HasCoreUpdate(List<AssetUpdateOption> options)
        {
            foreach (var option in options)
            {
                if (option.IsCore)
                    return true;
            }
            return false;
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

        void OnUpdateSubscription(SubscriptionInfo sub)
        {
            var subRef = sub;
            var self = this;
            new System.Threading.Thread((System.Threading.ThreadStart)delegate
            {
                try
                {
                    self.BeginInvoke(new Action(delegate
                    {
                        _trayIcon.ShowBalloonTip(1000, "Mihomo",
                            "正在更新订阅: " + subRef.Name + " ...",
                            ToolTipIcon.Info);
                    }));

                    bool success = DownloadAndMergeSubscription(subRef.Url, subRef.Name);

                    self.BeginInvoke(new Action(delegate
                    {
                        if (success)
                        {
                            _trayIcon.ShowBalloonTip(2000, "Mihomo",
                                "订阅 [" + subRef.Name + "] 更新成功，正在重启...",
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
                            _trayIcon.ShowBalloonTip(3000, "Mihomo",
                                "订阅 [" + subRef.Name + "] 更新失败",
                                ToolTipIcon.Error);
                        }
                        RefreshUI();
                    }));
                }
                catch (Exception ex)
                {
                    self.BeginInvoke(new Action(delegate
                    {
                        _trayIcon.ShowBalloonTip(3000, "Mihomo",
                            "订阅更新出错: " + ex.Message,
                            ToolTipIcon.Error);
                    }));
                }
            }).Start();
        }

        void OnUpdateAllSubscriptions(object sender, EventArgs e)
        {
            var subs = LoadSubscriptions();
            foreach (var sub in subs)
            {
                OnUpdateSubscription(sub);
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
                KillMihomo();
                _trayIcon.ShowBalloonTip(2000, "Mihomo", "已停止", ToolTipIcon.Info);
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

        bool DownloadAndMergeSubscription(string url, string name)
        {
            string downloadedYaml;
            try
            {
                downloadedYaml = DownloadUrl(url);
            }
            catch (Exception ex)
            {
                this.BeginInvoke(new Action(delegate
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "下载订阅 [" + name + "] 失败: " + ex.Message,
                        ToolTipIcon.Error);
                }));
                return false;
            }

            if (IsBase64String(downloadedYaml))
            {
                try
                {
                    downloadedYaml = Encoding.UTF8.GetString(
                        Convert.FromBase64String(downloadedYaml));
                }
                catch
                {
                    try
                    {
                        downloadedYaml = Encoding.UTF8.GetString(
                            Convert.FromBase64String(
                                downloadedYaml.Trim().Replace(" ", "+")));
                    }
                    catch { }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadedYaml))
                return false;

            return MergeConfig(downloadedYaml, name);
        }

        bool MergeConfig(string downloadedYaml, string name)
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        _trayIcon.ShowBalloonTip(3000, "Mihomo",
                            "config.yaml 不存在",
                            ToolTipIcon.Error);
                    }));
                    return false;
                }

                string activePath = _activeConfigPath;
                if (!File.Exists(activePath)) activePath = _configPath;
                if (!File.Exists(activePath))
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        _trayIcon.ShowBalloonTip(3000, "Mihomo",
                            "当前活动配置不存在",
                            ToolTipIcon.Error);
                    }));
                    return false;
                }

                string originalConfig = File.ReadAllText(activePath, Encoding.UTF8);

                string subscriptionProxiesPart = ExtractSectionFromYaml(downloadedYaml, "proxies:");

                if (string.IsNullOrWhiteSpace(subscriptionProxiesPart))
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        _trayIcon.ShowBalloonTip(3000, "Mihomo",
                            "订阅 [" + name + "] 中未找到 proxies 配置",
                            ToolTipIcon.Error);
                    }));
                    return false;
                }

                int proxiesIndex = FindTopLevelKeyIndex(originalConfig, "proxies:");
                if (proxiesIndex < 0)
                {
                    this.BeginInvoke(new Action(delegate
                    {
                        _trayIcon.ShowBalloonTip(3000, "Mihomo",
                            "config.yaml 中未找到 proxies 配置",
                            ToolTipIcon.Error);
                    }));
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
                this.BeginInvoke(new Action(delegate
                {
                    _trayIcon.ShowBalloonTip(3000, "Mihomo",
                        "合并配置失败: " + ex.Message,
                        ToolTipIcon.Error);
                }));
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

		List<SubscriptionInfo> LoadSubscriptions()
        {
            var list = new List<SubscriptionInfo>();
            try
            {
                if (!File.Exists(_trayConfigPath))
                {
                    SaveDefaultSubscriptions();
                }

                string json = File.ReadAllText(_trayConfigPath, Encoding.UTF8);

                var subMatches = Regex.Matches(json,
                    @"""subscriptions""\s*:\s*\[(.*?)\]",
                    RegexOptions.Singleline);

                if (subMatches.Count > 0)
                {
                    string inner = subMatches[0].Groups[1].Value;
                    var entries = Regex.Matches(inner,
                        @"{[^}]*""name""\s*:\s*""([^""]+)""[^}]*""url""\s*:\s*""([^""]+)""[^}]*}");

                    foreach (Match entry in entries)
                    {
                        list.Add(new SubscriptionInfo
                        {
                            Name = entry.Groups[1].Value,
                            Url = entry.Groups[2].Value
                        });
                    }
                }
            }
            catch { }
            return list;
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

				var subsMatch = Regex.Matches(json,
					@"""subscriptions""\s*:\s*\[(.*?)\]",
					RegexOptions.Singleline);

				var loaded = new List<SubscriptionInfo>();

				if (subsMatch.Count > 0)
				{
					string inner = subsMatch[0].Groups[1].Value;
					var entries = Regex.Matches(inner,
						@"{[^}]*""name""\s*:\s*""([^""]+)""[^}]*""url""\s*:\s*""([^""]+)""[^}]*}");

					foreach (Match entry in entries)
					{
						loaded.Add(new SubscriptionInfo
						{
							Name = entry.Groups[1].Value,
							Url = entry.Groups[2].Value
						});
					}
				}

				subs = loaded;
			}
			catch { }
		}

        void SaveTrayConfig()
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"panel\": {\r\n");
            sb.Append("    \"host\": \"").Append(EscapeJson(_panelHost)).Append("\",\r\n");
            sb.Append("    \"port\": ").Append(_panelPort).Append(",\r\n");
            sb.Append("    \"path\": \"").Append(EscapeJson(_panelPath)).Append("\"\r\n");
            sb.Append("  },\r\n");
            sb.Append("  \"profiles\": [\r\n");
            for (int i = 0; i < _profiles.Count; i++)
            {
                var p = _profiles[i];
                sb.Append("    {\r\n");
                sb.Append("      \"name\": \"").Append(EscapeJson(p.Name)).Append("\",\r\n");
                sb.Append("      \"path\": \"").Append(EscapeJson(NormalizeRelativePath(p.Path))).Append("\"\r\n");
                sb.Append("    }");
                if (i < _profiles.Count - 1) sb.Append(",");
                sb.Append("\r\n");
            }
            sb.Append("  ],\r\n");
            sb.Append("  \"activeConfigPath\": \"").Append(EscapeJson(NormalizeRelativePath(_activeConfigPath))).Append("\",\r\n");
            sb.Append("  \"runMihomoOnStartup\": ").Append(_runMihomoOnStartup ? "true" : "false").Append(",\r\n");
            sb.Append("  \"lastTunEnabled\": ").Append(_lastTunEnabled ? "true" : "false").Append(",\r\n");
            sb.Append("  \"lastSystemProxyEnabled\": ").Append(_systemProxyDesired ? "true" : "false").Append(",\r\n");
            sb.Append("  \"systemProxyGuardEnabled\": ").Append(_systemProxyGuardEnabled ? "true" : "false").Append(",\r\n");
            sb.Append("  \"pendingEnableTunAfterAdmin\": ").Append(_pendingEnableTunAfterAdmin ? "true" : "false").Append(",\r\n");
            sb.Append("  \"subscriptions\": [\r\n");
            for (int i = 0; i < subs.Count; i++)
            {
                var s = subs[i];
                sb.Append("    {\r\n");
                sb.Append("      \"name\": \"").Append(EscapeJson(s.Name)).Append("\",\r\n");
                sb.Append("      \"url\": \"").Append(EscapeJson(s.Url)).Append("\"\r\n");
                sb.Append("    }");
                if (i < subs.Count - 1) sb.Append(",");
                sb.Append("\r\n");
            }
            sb.Append("  ]\r\n");
            sb.Append("}\r\n");
            WriteUtf8FileAtomic(_trayConfigPath, sb.ToString());
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

        string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
            if (string.IsNullOrEmpty(apiJson))
                throw new Exception("GitHub API 返回为空");

            var matches = Regex.Matches(apiJson,
                @"""browser_download_url""\s*:\s*""([^""]+)""");

            foreach (Match m in matches)
            {
                string candidate = m.Groups[1].Value;
                string fileName = Path.GetFileName(candidate).ToLowerInvariant();

                if (opt.MatchPattern != null)
                {
                    if (Regex.IsMatch(fileName, opt.MatchPattern, RegexOptions.IgnoreCase))
                        return candidate;
                }
                else if (fileName == opt.AssetName.ToLowerInvariant())
                {
                    return candidate;
                }
            }

            foreach (Match m in matches)
            {
                string candidate = m.Groups[1].Value;
                if (candidate.ToLowerInvariant().Contains(opt.AssetName.ToLowerInvariant()))
                    return candidate;
            }

            throw new Exception("未在发布页中找到匹配资源: " + opt.AssetName);
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
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Proxy = null;
                request.Timeout = 60000;
                request.ReadWriteTimeout = 60000;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 3;
                request.KeepAlive = false;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[8192];
                    int read;
                    while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                        fs.Write(buf, 0, read);
                }

                if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                    throw new Exception("下载文件为空");

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
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null;
            request.Timeout = 15000;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 3;
            request.ProtocolVersion = HttpVersion.Version11;
            request.KeepAlive = false;
            request.Accept = "application/vnd.github.v3+json";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                if (we.Response != null)
                {
                    using (var stream = we.Response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        throw new Exception("HTTP error: " + body);
                    }
                }
                throw;
            }
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
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 30000;
            request.UserAgent = "clash-verge/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
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
            return CreateStatusLightIcon(color, Color.FromArgb(105, 105, 105));
        }

        Icon CreateStatusLightIcon(Color fill, Color border)
        {
            int size = 32;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(fill))
                {
                    g.FillEllipse(brush, 5, 5, 22, 22);
                }
                using (var pen = new Pen(border, 1.4f))
                {
                    g.DrawEllipse(pen, 5, 5, 22, 22);
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

                if (IsCaption(item))
                {
                    item.Padding = new Padding(6, 5, 18, 5);
                    item.Font = CaptionFont;
                    item.ForeColor = MutedText;
                }
            }
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
                        owner.BeginInvoke((MethodInvoker)delegate { PositionSubMenu(item); });
                    }
                    catch { }
                }
            };
        }

        static void PositionSubMenu(ToolStripMenuItem item)
        {
            ToolStrip owner = item.Owner;
            ToolStripDropDown dropDown = item.DropDown;
            if (owner == null || dropDown == null)
                return;

            try
            {
                const int horizontalOverlap = 6;
                const int verticalLift = 6;

                Point itemTopRight = owner.PointToScreen(new Point(item.Bounds.Right - horizontalOverlap, item.Bounds.Top - verticalLift));
                Point itemTopLeft = owner.PointToScreen(new Point(item.Bounds.Left - dropDown.Width + horizontalOverlap, item.Bounds.Top - verticalLift));
                Rectangle screen = Screen.FromPoint(itemTopRight).WorkingArea;
                Point target = itemTopRight;

                if (target.X + dropDown.Width > screen.Right)
                    target = itemTopLeft;

                if (target.Y + dropDown.Height > screen.Bottom)
                    target.Y = Math.Max(screen.Top, screen.Bottom - dropDown.Height);
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
                    ApplyButton(button, button.DialogResult == DialogResult.OK || button.Text.IndexOf("开始", StringComparison.OrdinalIgnoreCase) >= 0);
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

        public static void ApplyButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? Accent : Border;
            button.BackColor = primary ? Accent : PanelBack;
            button.ForeColor = primary ? Color.White : Text;
            button.Height = Math.Max(button.Height, 30);
            button.Cursor = Cursors.Hand;
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
            grid.ColumnHeadersHeight = 32;
            grid.DefaultCellStyle.BackColor = PanelBack;
            grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
            grid.DefaultCellStyle.SelectionForeColor = Text;
            grid.RowTemplate.Height = 28;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
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

        static Image CreateMenuIcon(string key, bool active)
        {
            var bmp = new Bitmap(18, 18);
            Color color = active ? Accent : IconText;
            if (string.Equals(key, "exit", StringComparison.OrdinalIgnoreCase))
                color = Danger;
            if (string.Equals(key, "status-on", StringComparison.OrdinalIgnoreCase))
                color = ActiveGreen;
            if (string.Equals(key, "status-off", StringComparison.OrdinalIgnoreCase))
                color = MutedText;

            using (Graphics g = Graphics.FromImage(bmp))
            using (Pen pen = new Pen(color, 1.65F))
            using (SolidBrush brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (key == "status-on" || key == "status-off" || key == "empty")
                {
                    g.FillEllipse(brush, 6, 6, 6, 6);
                }
                else if (key == "play")
                {
                    Point[] pts = new Point[] { new Point(7, 4), new Point(14, 9), new Point(7, 14) };
                    g.FillPolygon(brush, pts);
                }
                else if (key == "stop")
                {
                    g.FillRectangle(brush, 5, 5, 8, 8);
                }
                else if (key == "proxy")
                {
                    g.DrawEllipse(pen, 3, 3, 12, 12);
                    g.DrawLine(pen, 3, 9, 15, 9);
                    g.DrawArc(pen, 5, 3, 8, 12, 90, 180);
                    g.DrawArc(pen, 5, 3, 8, 12, 270, 180);
                }
                else if (key == "tun")
                {
                    Point[] pts = new Point[] { new Point(9, 3), new Point(14, 5), new Point(13, 11), new Point(9, 15), new Point(5, 11), new Point(4, 5) };
                    g.DrawPolygon(pen, pts);
                    g.DrawLine(pen, 7, 9, 9, 11);
                    g.DrawLine(pen, 9, 11, 12, 7);
                }
                else if (key == "guard")
                {
                    Point[] pts = new Point[] { new Point(9, 3), new Point(14, 5), new Point(13, 11), new Point(9, 15), new Point(5, 11), new Point(4, 5) };
                    g.DrawPolygon(pen, pts);
                    g.DrawLine(pen, 7, 9, 9, 11);
                    g.DrawLine(pen, 9, 11, 12, 7);
                }
                else if (key == "panel")
                {
                    g.DrawRectangle(pen, 3, 4, 12, 10);
                    g.DrawLine(pen, 3, 7, 15, 7);
                    g.DrawLine(pen, 6, 10, 12, 10);
                }
                else if (key == "settings")
                {
                    g.DrawEllipse(pen, 6, 6, 6, 6);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = Math.PI * i / 4D;
                        float x1 = 9 + (float)Math.Cos(a) * 5F;
                        float y1 = 9 + (float)Math.Sin(a) * 5F;
                        float x2 = 9 + (float)Math.Cos(a) * 7F;
                        float y2 = 9 + (float)Math.Sin(a) * 7F;
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                }
                else if (key == "profile")
                {
                    g.DrawRectangle(pen, 4, 3, 10, 12);
                    g.DrawLine(pen, 6, 7, 12, 7);
                    g.DrawLine(pen, 6, 10, 12, 10);
                }
                else if (key == "refresh")
                {
                    g.DrawArc(pen, 4, 4, 10, 10, 35, 260);
                    g.DrawLine(pen, 12, 4, 14, 4);
                    g.DrawLine(pen, 14, 4, 14, 6);
                }
                else if (key == "list")
                {
                    g.FillEllipse(brush, 4, 5, 2, 2);
                    g.FillEllipse(brush, 4, 8, 2, 2);
                    g.FillEllipse(brush, 4, 11, 2, 2);
                    g.DrawLine(pen, 8, 6, 14, 6);
                    g.DrawLine(pen, 8, 9, 14, 9);
                    g.DrawLine(pen, 8, 12, 14, 12);
                }
                else if (key == "edit")
                {
                    g.DrawLine(pen, 5, 13, 12, 6);
                    g.DrawLine(pen, 11, 5, 13, 7);
                    g.DrawLine(pen, 4, 14, 7, 13);
                }
                else if (key == "download")
                {
                    g.DrawLine(pen, 9, 4, 9, 11);
                    g.DrawLine(pen, 6, 8, 9, 11);
                    g.DrawLine(pen, 12, 8, 9, 11);
                    g.DrawLine(pen, 5, 14, 13, 14);
                }
                else if (key == "startup")
                {
                    g.DrawEllipse(pen, 4, 4, 10, 10);
                    g.DrawLine(pen, 9, 9, 9, 5);
                    g.DrawLine(pen, 9, 9, 12, 10);
                }
                else if (key == "power")
                {
                    g.DrawArc(pen, 4, 5, 10, 10, 130, 280);
                    g.DrawLine(pen, 9, 3, 9, 9);
                }
                else if (key == "exit")
                {
                    g.DrawRectangle(pen, 4, 4, 7, 10);
                    g.DrawLine(pen, 9, 9, 15, 9);
                    g.DrawLine(pen, 12, 6, 15, 9);
                    g.DrawLine(pen, 12, 12, 15, 9);
                }
                else
                {
                    g.FillEllipse(brush, 6, 6, 6, 6);
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
            e.TextRectangle = new Rectangle(e.TextRectangle.X, e.TextRectangle.Y + 1, e.TextRectangle.Width, e.TextRectangle.Height);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? UiStyles.IconText : UiStyles.Border;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image != null)
            {
                Rectangle rect = IconRectangle(e.Item);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawImage(e.Image, rect);
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

    class SubscriptionManagerForm : Form
    {
        DataGridView _grid;
        BindingSource _source;
        List<SubscriptionInfo> _items;

        public SubscriptionManagerForm(List<SubscriptionInfo> items)
        {
            Text = "订阅管理";
            UiStyles.ApplyForm(this);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Width = 620;
            Height = 420;

            _items = items;
            _source = new BindingSource();
            _source.DataSource = _items;

            _grid = new DataGridView();
            _grid.Left = 12;
            _grid.Top = 12;
            _grid.Width = 580;
            _grid.Height = 310;
            _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _grid.AutoGenerateColumns = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.DataSource = _source;

            var nameCol = new DataGridViewTextBoxColumn();
            nameCol.DataPropertyName = "Name";
            nameCol.HeaderText = "名称";
            nameCol.Width = 180;

            var urlCol = new DataGridViewTextBoxColumn();
            urlCol.DataPropertyName = "Url";
            urlCol.HeaderText = "订阅链接";
            urlCol.Width = 380;

            _grid.Columns.Add(nameCol);
            _grid.Columns.Add(urlCol);

            var addBtn = new Button { Text = "新增", Left = 12, Top = 332, Width = 72 };
            var editBtn = new Button { Text = "编辑", Left = 92, Top = 332, Width = 72 };
            var delBtn = new Button { Text = "删除", Left = 172, Top = 332, Width = 72 };
            var upBtn = new Button { Text = "上移", Left = 252, Top = 332, Width = 72 };
            var downBtn = new Button { Text = "下移", Left = 332, Top = 332, Width = 72 };
            var okBtn = new Button { Text = "确定", Left = 440, Top = 332, Width = 72, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 520, Top = 332, Width = 72, DialogResult = DialogResult.Cancel };

            addBtn.Click += delegate
            {
                using (var dlg = new SubscriptionEditForm(new SubscriptionInfo { Name = "", Url = "" }, true))
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
                if (_grid.CurrentRow == null) return;
                int idx = _grid.CurrentRow.Index;
                if (idx < 0 || idx >= _items.Count) return;
                var current = _items[idx];
                using (var dlg = new SubscriptionEditForm(new SubscriptionInfo { Name = current.Name, Url = current.Url }, false))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _items[idx] = dlg.Value;
                        _source.ResetBindings(false);
                    }
                }
            };

            delBtn.Click += delegate
            {
                if (_grid.CurrentRow == null) return;
                int idx = _grid.CurrentRow.Index;
                if (idx < 0 || idx >= _items.Count) return;
                _items.RemoveAt(idx);
                _source.ResetBindings(false);
            };

            upBtn.Click += delegate
            {
                MoveSelected(-1);
            };

            downBtn.Click += delegate
            {
                MoveSelected(1);
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
        }

        void MoveSelected(int delta)
        {
            if (_grid.CurrentRow == null) return;
            int idx = _grid.CurrentRow.Index;
            int newIdx = idx + delta;
            if (idx < 0 || idx >= _items.Count) return;
            if (newIdx < 0 || newIdx >= _items.Count) return;

            var temp = _items[idx];
            _items[idx] = _items[newIdx];
            _items[newIdx] = temp;
            _source.ResetBindings(false);
            _grid.CurrentCell = _grid.Rows[newIdx].Cells[0];
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
            Width = 640;
            Height = 220;

            var nameLabel = new Label { Left = 12, Top = 18, Width = 80, Text = "名称" };
            _nameBox = new TextBox { Left = 92, Top = 14, Width = 520, Text = item.Name };

            var urlLabel = new Label { Left = 12, Top = 54, Width = 80, Text = "链接" };
            _urlBox = new TextBox { Left = 92, Top = 50, Width = 520, Text = item.Url };

            var okBtn = new Button { Text = "确定", Left = 460, Top = 98, Width = 72, DialogResult = DialogResult.OK };
            var cancelBtn = new Button { Text = "取消", Left = 540, Top = 98, Width = 72, DialogResult = DialogResult.Cancel };

            okBtn.Click += delegate
            {
                Value = new SubscriptionInfo { Name = _nameBox.Text.Trim(), Url = _urlBox.Text.Trim() };
                if (Value.Name.Length == 0 || Value.Url.Length == 0)
                {
                    MessageBox.Show(this, "名称和链接不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
            };

            Controls.Add(nameLabel);
            Controls.Add(_nameBox);
            Controls.Add(urlLabel);
            Controls.Add(_urlBox);
            Controls.Add(okBtn);
            Controls.Add(cancelBtn);

            AcceptButton = okBtn;
            CancelButton = cancelBtn;
            UiStyles.ApplyControls(this);
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
