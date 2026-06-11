package main

import (
	"archive/zip"
	"bytes"
	"compress/gzip"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"syscall"
	"time"

	_ "embed"

	"github.com/getlantern/systray"
)

//go:embed on.png
var onPng []byte

//go:embed off.png
var offPng []byte

//go:embed warn.png
var warnPng []byte

const (
	statusRefreshInterval  = 2 * time.Second
	proxyGuardInterval     = 30 * time.Second
	runningCacheTTL        = 5 * time.Second
	proxyStateCacheTTL     = 5 * time.Second
	networkServiceCacheTTL = 5 * time.Minute
)

var (
	baseDir       string
	configPath    string
	mihomoPath    string
	trayCfgPath   string
	exePath       string
	activeCfgPath string
	panelHost     = "127.0.0.1"
	panelPort     = 9097
	panelPath     = "ui"
	runOnStart    = false
	lastTunOn     = false

	systemProxyDesired      = false
	systemProxyGuardEnabled = true
	trayConfigHasTunState   = false

	subs     []SubInfo
	profiles []ConfigProfile

	mihomoCmd *exec.Cmd

	httpClient = &http.Client{Timeout: 30 * time.Second}

	activeConfigCache configFileCache

	runningCacheValue bool
	runningCacheAt    time.Time

	proxyStateCacheEnabled bool
	proxyStateCacheTarget  string
	proxyStateCacheAt      time.Time

	networkServiceCacheValue string
	networkServiceCacheAt    time.Time

	mStatus     *systray.MenuItem
	mStartStop  *systray.MenuItem
	mTun        *systray.MenuItem
	mProxy      *systray.MenuItem
	mProxyGuard *systray.MenuItem
	mAutoStart  *systray.MenuItem
	mRunOnStart *systray.MenuItem
)

var mihomoProcessNames = []string{"mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta"}

var (
	tunStatusRe          = regexp.MustCompile(`(?m)(^tun:\s*\n(?:[ \t]+[^\n]*\n)*?[ \t]+enable:\s*)(true|false)`)
	httpPortRe           = regexp.MustCompile(`(?m)^port:\s*(\d+)`)
	base64CandidateRe    = regexp.MustCompile(`^[A-Za-z0-9+/]+=*$`)
	apiMessageRe         = regexp.MustCompile(`"message"\s*:\s*"([^"]+)"`)
	browserDownloadURLRe = regexp.MustCompile(`"browser_download_url"\s*:\s*"([^"]+)"`)
)

var tunConflictTargets = []tunConflictTarget{
	{DisplayName: "ZeroTier", Tokens: []string{"zerotier", "zero tier"}, CanStop: true},
	{DisplayName: "Tailscale", Tokens: []string{"tailscale", "tailscaled"}, CanStop: true},
	{DisplayName: "NetEase UU Booster", Tokens: []string{"uu booster", "uubooster", "netease uu"}, CanStop: true},
	{DisplayName: "Leigod Accelerator", Tokens: []string{"leigod", "leishen"}, CanStop: true},
	{DisplayName: "XunYou Accelerator", Tokens: []string{"xunyou"}, CanStop: true},
	{DisplayName: "QiYou Accelerator", Tokens: []string{"qiyou"}, CanStop: true},
	{DisplayName: "Netpas Accelerator", Tokens: []string{"netpas"}, CanStop: true},
	{DisplayName: "Generic Game Booster", Tokens: []string{"booster", "accelerator"}, CanStop: true},
}

type SubInfo struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

type ConfigProfile struct {
	Name string `json:"name"`
	Path string `json:"path"`
}

type TrayConfig struct {
	Panel                   panelConfig     `json:"panel"`
	Profiles                []ConfigProfile `json:"profiles"`
	ActiveConfigPath        string          `json:"activeConfigPath"`
	RunMihomoOnStartup      bool            `json:"runMihomoOnStartup"`
	LastTunEnabled          bool            `json:"lastTunEnabled"`
	SystemProxyDesired      bool            `json:"systemProxyDesired"`
	SystemProxyGuardEnabled bool            `json:"systemProxyGuardEnabled"`
	Subscriptions           []SubInfo       `json:"subscriptions"`
}

type panelConfig struct {
	Host string `json:"host"`
	Port int    `json:"port"`
	Path string `json:"path"`
}

type configFileCache struct {
	path    string
	modTime time.Time
	size    int64
	data    []byte
}

type tunConflictTarget struct {
	DisplayName string
	Tokens      []string
	CanStop     bool
}

type tunConflictInfo struct {
	DisplayName string
	ProcessID   string
	Command     string
	CanStop     bool
}

func main() {
	exe, _ := os.Executable()
	exePath, _ = filepath.EvalSymlinks(exe)
	baseDir = filepath.Dir(exePath)
	configPath = filepath.Join(baseDir, "config.yaml")
	trayCfgPath = filepath.Join(baseDir, "tray-config.json")
	activeCfgPath = configPath

	for _, n := range []string{"mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta"} {
		p := filepath.Join(baseDir, n)
		if _, err := os.Stat(p); err == nil {
			mihomoPath = p
			break
		}
	}
	if mihomoPath == "" {
		mihomoPath = filepath.Join(baseDir, "mihomo")
	}

	loadTrayConfig()
	resolveActiveConfig()
	applySavedTunStatus()

	systray.Run(onReady, onExit)
}

func onReady() {
	systray.SetIcon(offPng)
	systray.SetTitle("")
	systray.SetTooltip("Mihomo - Stopped")

	mStatus = systray.AddMenuItem("Mihomo - Stopped", "")
	mStatus.Disable()
	systray.AddSeparator()

	mStartStop = systray.AddMenuItem("Start Mihomo", "Start or stop the mihomo core")
	go func() {
		for range mStartStop.ClickedCh {
			if isRunning() {
				stopMihomo()
			} else {
				startMihomo()
			}
			refreshUI()
		}
	}()

	systray.AddSeparator()

	mTun = systray.AddMenuItemCheckbox("TUN Mode", "Route traffic through mihomo TUN mode", readTunStatus())
	go func() {
		for range mTun.ClickedCh {
			toggleTun()
		}
	}()

	mProxy = systray.AddMenuItemCheckbox("System Proxy", "Set macOS HTTP and HTTPS proxy to mihomo", systemProxyDesired)
	go func() {
		for range mProxy.ClickedCh {
			toggleSystemProxy()
		}
	}()

	mProxyGuard = systray.AddMenuItemCheckbox("Proxy Guard", "Every 30 seconds, restore the system proxy if another app changes it", systemProxyGuardEnabled)
	go func() {
		for range mProxyGuard.ClickedCh {
			toggleProxyGuard()
		}
	}()

	mDash := systray.AddMenuItem("Open Dashboard", "Open the Mihomo dashboard in your browser")
	go func() {
		for range mDash.ClickedCh {
			exec.Command("open", buildPanelUrl()).Start()
		}
	}()

	mPanelSet := systray.AddMenuItem("Panel Settings", "Change dashboard host, port, and path")
	go func() {
		for range mPanelSet.ClickedCh {
			openPanelSettings()
		}
	}()

	systray.AddSeparator()

	mSubMenu := systray.AddMenuItem("Subscriptions", "Update configured subscriptions")
	mSubAll := mSubMenu.AddSubMenuItem("Update All", "Download and merge all subscriptions")
	go func() {
		for range mSubAll.ClickedCh {
			for _, s := range subs {
				updateSubscription(s)
			}
		}
	}()
	rebuildSubMenu(mSubMenu)

	mSubEdit := systray.AddMenuItem("Edit Subscriptions", "Edit tray-config.json in TextEdit")
	go func() {
		for range mSubEdit.ClickedCh {
			exec.Command("open", "-a", "TextEdit", trayCfgPath).Start()
		}
	}()

	systray.AddSeparator()

	mUpdate := systray.AddMenuItem("Update Components", "Update mihomo core and rule data")
	go func() {
		for range mUpdate.ClickedCh {
			updateAllComponents()
		}
	}()

	systray.AddSeparator()

	mRunOnStart = systray.AddMenuItemCheckbox("Run Mihomo on Startup", "Start mihomo when MihomoTray launches", runOnStart)
	go func() {
		for range mRunOnStart.ClickedCh {
			toggleRunOnStartup()
		}
	}()

	mAutoStart = systray.AddMenuItemCheckbox("Open MihomoTray at Login", "Register MihomoTray in LaunchAgents", isAutoStartEnabled())
	go func() {
		for range mAutoStart.ClickedCh {
			toggleAutoStart()
		}
	}()

	systray.AddSeparator()

	mQuit := systray.AddMenuItem("Quit", "Stop mihomo and quit MihomoTray")
	go func() {
		<-mQuit.ClickedCh
		systray.Quit()
	}()

	if runOnStart {
		go func() {
			time.Sleep(500 * time.Millisecond)
			startMihomo()
			refreshUI()
		}()
	}
	if systemProxyDesired {
		go ensureSystemProxy()
	}

	go func() {
		ticker := time.NewTicker(statusRefreshInterval)
		defer ticker.Stop()
		for range ticker.C {
			refreshUI()
		}
	}()

	go func() {
		ticker := time.NewTicker(proxyGuardInterval)
		defer ticker.Stop()
		for range ticker.C {
			ensureSystemProxy()
		}
	}()

	refreshUI()
}

func onExit() {
	runOnStart = isRunning()
	lastTunOn = readTunStatus()
	saveTrayConfig()
	if isMihomoSystemProxyActiveFresh() {
		setSystemProxy(false)
	}
	killMihomo()
}

func refreshUI() {
	running := isRunning()
	tunOn := readTunStatus()
	proxyOn := isProxyEnabled()
	httpPort := readHTTPPort()

	if running {
		mStartStop.SetTitle("Stop Mihomo")
		tip := "Mihomo - Running"
		if tunOn {
			tip += " (TUN:ON)"
		}
		if proxyOn {
			tip += " (Proxy:ON)"
		}
		systray.SetTooltip(tip)
		if !tunOn && !proxyOn {
			systray.SetIcon(warnPng)
		} else {
			systray.SetIcon(onPng)
		}
		mStatus.SetTitle(tip)
	} else {
		mStartStop.SetTitle("Start Mihomo")
		systray.SetIcon(offPng)
		systray.SetTooltip("Mihomo - Stopped")
		mStatus.SetTitle("Mihomo - Stopped")
	}

	if tunOn {
		mTun.Check()
		mTun.SetTitle("TUN Mode: On")
	} else {
		mTun.Uncheck()
		mTun.SetTitle("TUN Mode: Off")
	}

	if systemProxyDesired {
		mProxy.Check()
		if proxyOn {
			mProxy.SetTitle(fmt.Sprintf("System Proxy: On (localhost:%d)", httpPort))
		} else {
			mProxy.SetTitle(fmt.Sprintf("System Proxy: Restoring (localhost:%d)", httpPort))
		}
	} else {
		mProxy.Uncheck()
		mProxy.SetTitle("System Proxy: Off")
	}

	if systemProxyGuardEnabled {
		mProxyGuard.Check()
		mProxyGuard.SetTitle("Proxy Guard: On")
	} else {
		mProxyGuard.Uncheck()
		mProxyGuard.SetTitle("Proxy Guard: Off")
	}

	if isAutoStartEnabled() {
		mAutoStart.Check()
	} else {
		mAutoStart.Uncheck()
	}

	if runOnStart {
		mRunOnStart.Check()
	} else {
		mRunOnStart.Uncheck()
	}
}

func rebuildSubMenu(parent *systray.MenuItem) {
	loadTrayConfig()
	for i := range subs {
		idx := i
		item := parent.AddSubMenuItem(subs[idx].Name, "")
		go func(s SubInfo) {
			for range item.ClickedCh {
				updateSubscription(s)
			}
		}(subs[idx])
	}
}

func startMihomo() {
	if _, err := os.Stat(mihomoPath); os.IsNotExist(err) {
		notify("mihomo not found")
		return
	}
	if isRunning() {
		return
	}
	workDir := filepath.Dir(resolvePath(activeCfgPath))
	mihomoCmd = exec.Command(mihomoPath, "-d", workDir)
	mihomoCmd.Dir = baseDir
	if err := mihomoCmd.Start(); err != nil {
		notify("Start failed: " + err.Error())
		return
	}
	invalidateRunningCache()
	go func() {
		mihomoCmd.Wait()
		mihomoCmd = nil
		invalidateRunningCache()
	}()
}

func stopMihomo() { killMihomo() }

func killMihomo() {
	if mihomoCmd != nil && mihomoCmd.Process != nil {
		mihomoCmd.Process.Signal(syscall.SIGTERM)
		time.Sleep(300 * time.Millisecond)
		mihomoCmd.Process.Kill()
		mihomoCmd = nil
	}
	for _, n := range mihomoProcessNames {
		exec.Command("pkill", "-TERM", "-x", n).Run()
	}
	time.Sleep(300 * time.Millisecond)
	for _, n := range mihomoProcessNames {
		exec.Command("pkill", "-KILL", "-x", n).Run()
	}
	time.Sleep(200 * time.Millisecond)
	invalidateRunningCache()
}

func isRunning() bool {
	if time.Since(runningCacheAt) < runningCacheTTL {
		return runningCacheValue
	}
	runningCacheValue = isRunningFresh()
	runningCacheAt = time.Now()
	return runningCacheValue
}

func isRunningFresh() bool {
	if mihomoCmd != nil && mihomoCmd.Process != nil {
		return mihomoCmd.Process.Signal(syscall.Signal(0)) == nil
	}
	out, _ := exec.Command("pgrep", "-lf", strings.Join(mihomoProcessNames, "|")).Output()
	for _, l := range strings.Split(strings.TrimSpace(string(out)), "\n") {
		if l == "" {
			continue
		}
		lower := strings.ToLower(l)
		if strings.Contains(lower, "mihomo-tray") {
			continue
		}
		for _, n := range mihomoProcessNames {
			if strings.Contains(lower, strings.ToLower(n)) {
				return true
			}
		}
	}
	return false
}

func invalidateRunningCache() {
	runningCacheAt = time.Time{}
}

func readTunStatus() bool {
	data, err := readActiveConfig()
	if err != nil {
		return false
	}
	m := tunStatusRe.FindSubmatch(data)
	return len(m) > 2 && string(m[2]) == "true"
}

func toggleTun() {
	cur := readTunStatus()
	newVal := !cur
	if newVal && !prepareTunEnable() {
		refreshUI()
		return
	}
	path := resolvePath(activeCfgPath)
	data, err := os.ReadFile(path)
	if err != nil {
		return
	}
	valStr := "false"
	if newVal {
		valStr = "true"
	}
	if !tunStatusRe.Match(data) {
		notify("tun.enable not found in config")
		return
	}
	os.WriteFile(path, tunStatusRe.ReplaceAll(data, []byte("${1}"+valStr)), 0644)
	activeConfigCache = configFileCache{}
	lastTunOn = newVal
	saveTrayConfig()
	if isRunning() {
		stopMihomo()
		time.Sleep(500 * time.Millisecond)
		startMihomo()
	}
	refreshUI()
}

func isProxyEnabled() bool {
	expected := fmt.Sprintf("localhost:%d", readHTTPPort())
	return isSystemProxyActiveCached(expected)
}

func isProxyEnabledFresh() bool {
	enabled, _ := readSystemProxyState()
	return enabled
}

func isMihomoSystemProxyActiveFresh() bool {
	expected := fmt.Sprintf("localhost:%d", readHTTPPort())
	webEnabled, webTarget := readSystemProxyState()
	secureEnabled, secureTarget := readSecureSystemProxyState()
	return (webEnabled && webTarget == expected) || (secureEnabled && secureTarget == expected)
}

func toggleSystemProxy() {
	systemProxyDesired = !systemProxyDesired
	saveTrayConfig()
	setSystemProxy(systemProxyDesired)
}

func toggleProxyGuard() {
	systemProxyGuardEnabled = !systemProxyGuardEnabled
	saveTrayConfig()
	if systemProxyGuardEnabled {
		ensureSystemProxy()
	}
	refreshUI()
}

func ensureSystemProxy() {
	if !systemProxyGuardEnabled || !systemProxyDesired {
		return
	}
	expected := fmt.Sprintf("localhost:%d", readHTTPPort())
	if !systemProxyMatches(expected) {
		setSystemProxy(true)
	}
}

func setSystemProxy(enable bool) {
	svc := getNetworkService()
	if enable {
		port := readHTTPPort()
		exec.Command("networksetup", "-setwebproxy", svc, "localhost", strconv.Itoa(port)).Run()
		exec.Command("networksetup", "-setsecurewebproxy", svc, "localhost", strconv.Itoa(port)).Run()
	} else {
		exec.Command("networksetup", "-setwebproxystate", svc, "off").Run()
		exec.Command("networksetup", "-setsecurewebproxystate", svc, "off").Run()
	}
	invalidateProxyStateCache()
	refreshUI()
}

func getNetworkService() string {
	if networkServiceCacheValue != "" && time.Since(networkServiceCacheAt) < networkServiceCacheTTL {
		return networkServiceCacheValue
	}
	out, _ := exec.Command("networksetup", "-listallnetworkservices").Output()
	for _, l := range strings.Split(string(out), "\n") {
		l = strings.TrimSpace(l)
		if l != "" && !strings.HasPrefix(l, "An asterisk") {
			networkServiceCacheValue = l
			networkServiceCacheAt = time.Now()
			return l
		}
	}
	networkServiceCacheValue = "Wi-Fi"
	networkServiceCacheAt = time.Now()
	return "Wi-Fi"
}

func readHTTPPort() int {
	data, _ := readActiveConfig()
	return parseHTTPPort(data, 7890)
}

func readActiveConfig() ([]byte, error) {
	path := resolvePath(activeCfgPath)
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}
	if activeConfigCache.path == path &&
		activeConfigCache.size == info.Size() &&
		activeConfigCache.modTime.Equal(info.ModTime()) {
		return activeConfigCache.data, nil
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	activeConfigCache = configFileCache{
		path:    path,
		modTime: info.ModTime(),
		size:    info.Size(),
		data:    data,
	}
	return data, nil
}

func parseHTTPPort(data []byte, fallback int) int {
	m := httpPortRe.FindSubmatch(data)
	if len(m) < 2 {
		return fallback
	}
	p, err := strconv.Atoi(string(m[1]))
	if err != nil || p <= 0 || p > 65535 {
		return fallback
	}
	return p
}

func readSystemProxyState() (bool, string) {
	return readProxyState("-getwebproxy")
}

func readSecureSystemProxyState() (bool, string) {
	return readProxyState("-getsecurewebproxy")
}

func readProxyState(flag string) (bool, string) {
	out, err := exec.Command("networksetup", flag, getNetworkService()).Output()
	if err != nil {
		return false, ""
	}
	return parseNetworksetupProxyState(string(out))
}

func systemProxyMatches(expected string) bool {
	webEnabled, webTarget := readSystemProxyState()
	secureEnabled, secureTarget := readSecureSystemProxyState()
	return webEnabled && secureEnabled && webTarget == expected && secureTarget == expected
}

func readSystemProxyStateCached() (bool, string) {
	if time.Since(proxyStateCacheAt) < proxyStateCacheTTL {
		return proxyStateCacheEnabled, proxyStateCacheTarget
	}
	enabled, target := readSystemProxyState()
	proxyStateCacheEnabled = enabled
	proxyStateCacheTarget = target
	proxyStateCacheAt = time.Now()
	return enabled, target
}

func invalidateProxyStateCache() {
	proxyStateCacheAt = time.Time{}
}

func isSystemProxyActiveCached(expected string) bool {
	if time.Since(proxyStateCacheAt) < proxyStateCacheTTL {
		return proxyStateCacheEnabled && proxyStateCacheTarget == expected
	}
	active := systemProxyMatches(expected)
	proxyStateCacheEnabled = active
	proxyStateCacheTarget = expected
	proxyStateCacheAt = time.Now()
	return active
}

func parseNetworksetupProxyState(out string) (bool, string) {
	enabled := false
	server := ""
	port := ""
	for _, line := range strings.Split(out, "\n") {
		parts := strings.SplitN(strings.TrimSpace(line), ":", 2)
		if len(parts) != 2 {
			continue
		}
		key := strings.TrimSpace(parts[0])
		value := strings.TrimSpace(parts[1])
		switch key {
		case "Enabled":
			enabled = strings.EqualFold(value, "Yes")
		case "Server":
			server = value
		case "Port":
			port = value
		}
	}
	if server == "" || port == "" {
		return enabled, ""
	}
	return enabled, server + ":" + port
}

func applySavedTunStatus() {
	if !trayConfigHasTunState {
		lastTunOn = readTunStatus()
		saveTrayConfig()
		return
	}
	if lastTunOn == readTunStatus() {
		return
	}
	if lastTunOn && !prepareTunEnable() {
		return
	}
	if writeTunStatus(lastTunOn) {
		activeConfigCache = configFileCache{}
	}
}

func writeTunStatus(enabled bool) bool {
	path := resolvePath(activeCfgPath)
	data, err := os.ReadFile(path)
	if err != nil {
		return false
	}
	if !tunStatusRe.Match(data) {
		return false
	}
	val := "false"
	if enabled {
		val = "true"
	}
	return os.WriteFile(path, tunStatusRe.ReplaceAll(data, []byte("${1}"+val)), 0644) == nil
}

func prepareTunEnable() bool {
	conflicts := findTunConflicts()
	if len(conflicts) == 0 {
		return true
	}

	action := askTunConflictAction(conflicts)
	switch action {
	case "stop":
		stopTunConflicts(conflicts)
		return true
	case "continue":
		return true
	default:
		return false
	}
}

func findTunConflicts() []tunConflictInfo {
	out, err := exec.Command("ps", "-axo", "pid=,comm=,args=").Output()
	if err != nil {
		return nil
	}
	return parseTunConflicts(string(out), tunConflictTargets)
}

func parseTunConflicts(processList string, targets []tunConflictTarget) []tunConflictInfo {
	seen := make(map[string]bool)
	var conflicts []tunConflictInfo
	for _, line := range strings.Split(processList, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			continue
		}
		pid := fields[0]
		lower := strings.ToLower(line)
		if strings.Contains(lower, "mihomo-tray") || strings.Contains(lower, "mihomo ") {
			continue
		}
		for _, target := range targets {
			if !lineMatchesTokens(lower, target.Tokens) {
				continue
			}
			key := target.DisplayName + ":" + pid
			if seen[key] {
				continue
			}
			seen[key] = true
			conflicts = append(conflicts, tunConflictInfo{
				DisplayName: target.DisplayName,
				ProcessID:   pid,
				Command:     compactProcessLine(line),
				CanStop:     target.CanStop,
			})
		}
	}
	return conflicts
}

func lineMatchesTokens(line string, tokens []string) bool {
	for _, token := range tokens {
		if strings.Contains(line, strings.ToLower(token)) {
			return true
		}
	}
	return false
}

func compactProcessLine(line string) string {
	if len(line) <= 120 {
		return line
	}
	return line[:117] + "..."
}

func askTunConflictAction(conflicts []tunConflictInfo) string {
	var detail strings.Builder
	for _, c := range conflicts {
		detail.WriteString("- ")
		detail.WriteString(c.DisplayName)
		detail.WriteString(" (PID ")
		detail.WriteString(c.ProcessID)
		detail.WriteString(") ")
		detail.WriteString(strings.ReplaceAll(c.Command, `"`, `'`))
		detail.WriteString("\n")
	}

	script := fmt.Sprintf(`
set conflictText to %s
set promptText to "TUN mode may conflict with these VPN or game accelerator processes:" & return & return & conflictText & return & "To avoid disrupting them, cancel and close them manually, or continue without stopping them."
set dlg to display dialog promptText with title "TUN Conflict Check" buttons {"Cancel", "Continue", "Stop Conflicts"} default button "Cancel" cancel button "Cancel"
return button returned of dlg
`, appleScriptString(detail.String()))
	out, err := exec.Command("osascript", "-e", script).Output()
	if err != nil {
		return "cancel"
	}
	switch strings.TrimSpace(string(out)) {
	case "Stop Conflicts":
		return "stop"
	case "Continue":
		return "continue"
	default:
		return "cancel"
	}
}

func stopTunConflicts(conflicts []tunConflictInfo) {
	for _, c := range conflicts {
		if !c.CanStop || c.ProcessID == "" {
			continue
		}
		exec.Command("kill", c.ProcessID).Run()
	}
	time.Sleep(800 * time.Millisecond)
}

func plistPath() string {
	home, _ := os.UserHomeDir()
	return filepath.Join(home, "Library", "LaunchAgents", "com.mihomo.tray.plist")
}

func isAutoStartEnabled() bool {
	_, err := os.Stat(plistPath())
	return err == nil
}

func toggleAutoStart() {
	if isAutoStartEnabled() {
		os.Remove(plistPath())
	} else {
		os.MkdirAll(filepath.Dir(plistPath()), 0755)
		os.WriteFile(plistPath(), []byte(fmt.Sprintf(
			`<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.mihomo.tray</string>
    <key>ProgramArguments</key>
    <array>
        <string>%s</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <false/>
</dict>
</plist>`, exePath)), 0644)
	}
	refreshUI()
}

func toggleRunOnStartup() {
	runOnStart = !runOnStart
	saveTrayConfig()
	refreshUI()
}

func buildPanelUrl() string {
	p := strings.Trim(panelPath, "/")
	if p == "" {
		p = "ui"
	}
	return fmt.Sprintf("http://%s:%d/%s/", panelHost, panelPort, p)
}

func openPanelSettings() {
	script := fmt.Sprintf(`
set host to text returned of (display dialog "Host:" default answer "%s" with title "Panel Settings")
set port to text returned of (display dialog "Port:" default answer "%d" with title "Panel Settings")
set path to text returned of (display dialog "Path:" default answer "%s" with title "Panel Settings")
return host & "|" & port & "|" & path
`, panelHost, panelPort, panelPath)
	out, err := exec.Command("osascript", "-e", script).Output()
	if err != nil {
		return
	}
	parts := strings.SplitN(strings.TrimSpace(string(out)), "|", 3)
	if len(parts) == 3 {
		panelHost = parts[0]
		fmt.Sscanf(parts[1], "%d", &panelPort)
		panelPath = parts[2]
		saveTrayConfig()
	}
}

func appleScriptString(s string) string {
	escaped := strings.ReplaceAll(s, `\`, `\\`)
	escaped = strings.ReplaceAll(escaped, `"`, `\"`)
	return `"` + escaped + `"`
}

func updateSubscription(s SubInfo) {
	resp, err := httpGet(s.URL)
	if err != nil {
		notify("Download failed: " + err.Error())
		return
	}
	content := string(resp)
	if isBase64(content) {
		dec, _ := base64.StdEncoding.DecodeString(strings.TrimSpace(content))
		if dec != nil {
			content = string(dec)
		}
	}
	if err := mergeConfig(content); err != nil {
		notify("Merge failed: " + err.Error())
		return
	}
	if isRunning() {
		stopMihomo()
		time.Sleep(500 * time.Millisecond)
		startMihomo()
	}
	refreshUI()
}

func mergeConfig(newYaml string) error {
	path := resolvePath(activeCfgPath)
	oldData, err := os.ReadFile(path)
	if err != nil {
		return fmt.Errorf("config not found")
	}
	idx := findTopLevelKey(string(oldData), "proxies:")
	if idx < 0 {
		return fmt.Errorf("proxies: not found")
	}
	subIdx := findTopLevelKey(newYaml, "proxies:")
	if subIdx < 0 {
		return fmt.Errorf("proxies: not found in subscription")
	}
	header := strings.TrimRight(string(oldData)[:idx], "\r\n\t ")
	merged := header + "\n" + strings.TrimSpace(newYaml[subIdx:]) + "\n"
	return os.WriteFile(path, []byte(merged), 0644)
}

func findTopLevelKey(content, key string) int {
	re := regexp.MustCompile(`(?m)^` + regexp.QuoteMeta(key))
	loc := re.FindStringIndex(content)
	if loc == nil {
		return -1
	}
	return loc[0]
}

func isBase64(s string) bool {
	s = strings.Join(strings.Fields(s), "")
	if len(s) < 4 || len(s)%4 != 0 {
		return false
	}
	return base64CandidateRe.MatchString(s)
}

func resolveActiveConfig() {
	activeCfgPath = resolvePath(activeCfgPath)
	if _, err := os.Stat(activeCfgPath); os.IsNotExist(err) {
		activeCfgPath = configPath
	}
}

func resolvePath(p string) string {
	if p == "" {
		return configPath
	}
	if filepath.IsAbs(p) {
		return p
	}
	return filepath.Join(baseDir, p)
}

type updateAsset struct {
	name    string
	apiUrl  string
	matchRe string
	target  string
	isGz    bool
	isZip   bool
}

func updateAllComponents() {
	assets := []updateAsset{
		{
			name:    "mihomo core",
			apiUrl:  "https://api.github.com/repos/MetaCubeX/mihomo/releases/tags/Prerelease-Alpha",
			matchRe: `mihomo-darwin-arm64-alpha-.*\.gz$`,
			target:  filepath.Join(baseDir, "mihomo"),
			isGz:    true,
		},
		{name: "GeoLite2-Country.mmdb", apiUrl: "https://api.github.com/repos/P3TERX/GeoLite.mmdb/releases/latest", target: filepath.Join(baseDir, "GeoLite2-Country.mmdb")},
		{name: "GeoLite2-ASN.mmdb", apiUrl: "https://api.github.com/repos/P3TERX/GeoLite.mmdb/releases/latest", target: filepath.Join(baseDir, "GeoLite2-ASN.mmdb")},
		{name: "geosite.dat", apiUrl: "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest", target: filepath.Join(baseDir, "geosite.dat")},
		{name: "geoip.dat", apiUrl: "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest", target: filepath.Join(baseDir, "geoip.dat")},
		{name: "country.mmdb", apiUrl: "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest", target: filepath.Join(baseDir, "country.mmdb")},
	}
	changedCore := false
	for _, a := range assets {
		if err := downloadAsset(a); err != nil {
			notify(a.name + " failed: " + err.Error())
		} else if a.name == "mihomo core" {
			changedCore = true
		}
	}
	if changedCore && isRunning() {
		stopMihomo()
		time.Sleep(500 * time.Millisecond)
		startMihomo()
	}
	refreshUI()
}

func downloadAsset(a updateAsset) error {
	jsonBody, err := httpGet(a.apiUrl)
	if err != nil {
		return fmt.Errorf("API: %v", err)
	}
	if strings.Contains(string(jsonBody), `"message"`) {
		if m := apiMessageRe.FindStringSubmatch(string(jsonBody)); len(m) > 1 {
			return fmt.Errorf("API: %s", m[1])
		}
		return fmt.Errorf("API error")
	}
	url := findAssetUrl(string(jsonBody), a.matchRe, filepath.Base(a.target))
	if url == "" {
		return fmt.Errorf("asset not found")
	}
	resp, err := httpGet(url)
	if err != nil {
		return fmt.Errorf("download: %v", err)
	}
	if a.isGz {
		return extractFromGz(resp, a.target)
	}
	if a.isZip {
		return extractFromZip(resp, a.target)
	}
	return os.WriteFile(a.target, resp, 0644)
}

func extractFromGz(data []byte, target string) error {
	r, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		return err
	}
	defer r.Close()
	out, err := os.Create(target)
	if err != nil {
		return err
	}
	defer out.Close()
	if _, err = io.Copy(out, r); err == nil {
		os.Chmod(target, 0755)
	}
	return err
}

func extractFromZip(data []byte, target string) error {
	zr, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}
	targetName := strings.ToLower(filepath.Base(target))
	for _, f := range zr.File {
		if strings.ToLower(filepath.Base(f.Name)) == targetName || strings.Contains(strings.ToLower(f.Name), "mihomo") {
			rc, err := f.Open()
			if err != nil {
				return err
			}
			defer rc.Close()
			out, err := os.Create(target)
			if err != nil {
				return err
			}
			defer out.Close()
			if _, err = io.Copy(out, rc); err == nil {
				os.Chmod(target, 0755)
			}
			return err
		}
	}
	return fmt.Errorf("target not found")
}

func findAssetUrl(jsonBody, matchRe, name string) string {
	matches := browserDownloadURLRe.FindAllStringSubmatch(jsonBody, -1)
	if matchRe != "" {
		mre := regexp.MustCompile(matchRe)
		for _, m := range matches {
			if mre.MatchString(strings.ToLower(filepath.Base(m[1]))) {
				return m[1]
			}
		}
	}
	nl := strings.ToLower(name)
	for _, m := range matches {
		if strings.ToLower(filepath.Base(m[1])) == nl {
			return m[1]
		}
	}
	for _, m := range matches {
		if strings.Contains(strings.ToLower(m[1]), nl) {
			return m[1]
		}
	}
	return ""
}

func loadTrayConfig() {
	systemProxyGuardEnabled = true
	if _, err := os.Stat(trayCfgPath); os.IsNotExist(err) {
		saveDefaultConfig()
	}
	data, _ := os.ReadFile(trayCfgPath)
	var cfg TrayConfig
	if json.Unmarshal(data, &cfg) != nil {
		return
	}
	if cfg.Panel.Host != "" {
		panelHost = cfg.Panel.Host
	}
	if cfg.Panel.Port > 0 {
		panelPort = cfg.Panel.Port
	}
	if cfg.Panel.Path != "" {
		panelPath = cfg.Panel.Path
	}
	profiles = cfg.Profiles
	if len(profiles) == 0 {
		profiles = []ConfigProfile{{Name: "Default", Path: "config.yaml"}}
	}
	if cfg.ActiveConfigPath != "" {
		activeCfgPath = resolvePath(cfg.ActiveConfigPath)
	}
	runOnStart = cfg.RunMihomoOnStartup
	lastTunOn = cfg.LastTunEnabled
	trayConfigHasTunState = strings.Contains(string(data), "lastTunEnabled")
	systemProxyDesired = cfg.SystemProxyDesired
	systemProxyGuardEnabled = cfg.SystemProxyGuardEnabled
	if !cfg.SystemProxyGuardEnabled && !strings.Contains(string(data), "systemProxyGuardEnabled") {
		systemProxyGuardEnabled = true
	}
	subs = cfg.Subscriptions
}

func saveTrayConfig() {
	data, _ := json.MarshalIndent(TrayConfig{
		Panel:                   panelConfig{Host: panelHost, Port: panelPort, Path: panelPath},
		Profiles:                profiles,
		ActiveConfigPath:        makeRelative(activeCfgPath),
		RunMihomoOnStartup:      runOnStart,
		LastTunEnabled:          lastTunOn,
		SystemProxyDesired:      systemProxyDesired,
		SystemProxyGuardEnabled: systemProxyGuardEnabled,
		Subscriptions:           subs,
	}, "", "  ")
	os.WriteFile(trayCfgPath, data, 0644)
}

func saveDefaultConfig() { saveTrayConfig() }

func makeRelative(p string) string {
	abs := resolvePath(p)
	base, _ := filepath.Abs(baseDir)
	if rel, err := filepath.Rel(base, abs); err == nil && !strings.HasPrefix(rel, "..") {
		return filepath.ToSlash(rel)
	}
	return p
}

func httpGet(url string) ([]byte, error) {
	req, _ := http.NewRequest("GET", url, nil)
	req.Header.Set("User-Agent", "Mozilla/5.0")
	resp, err := httpClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	return io.ReadAll(resp.Body)
}

func notify(msg string) {
	log.Println("[Mihomo]", msg)
	escaped := strings.ReplaceAll(strings.ReplaceAll(msg, `\`, `\\`), `"`, `\"`)
	exec.Command("osascript", "-e", fmt.Sprintf(`display notification "%s" with title "Mihomo"`, escaped)).Run()
}
