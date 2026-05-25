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

// ─── State ───

var (
	baseDir      string
	configPath   string
	mihomoPath   string
	trayCfgPath  string
	exePath      string
	activeCfgPath string
	panelHost    = "127.0.0.1"
	panelPort    = 9097
	panelPath    = "ui"
	runOnStart   = false

	subs     []SubInfo
	profiles []ConfigProfile

	mihomoCmd *exec.Cmd

	// Menu items
	mStartStop  *systray.MenuItem
	mTun        *systray.MenuItem
	mProxy      *systray.MenuItem
	mAutoStart  *systray.MenuItem
	mRunOnStart *systray.MenuItem
)

type SubInfo struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

type ConfigProfile struct {
	Name string `json:"name"`
	Path string `json:"path"`
}

type TrayConfig struct {
	Panel             panelConfig     `json:"panel"`
	Profiles          []ConfigProfile `json:"profiles"`
	ActiveConfigPath  string          `json:"activeConfigPath"`
	RunMihomoOnStartup bool           `json:"runMihomoOnStartup"`
	Subscriptions     []SubInfo       `json:"subscriptions"`
}

type panelConfig struct {
	Host string `json:"host"`
	Port int    `json:"port"`
	Path string `json:"path"`
}

// ─── Main ───

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

	systray.Run(onReady, onExit)
}

func onReady() {
	systray.SetIcon(offPng)
	systray.SetTitle("")
	systray.SetTooltip("Mihomo - Stopped")

	// Start / Stop
	mStartStop = systray.AddMenuItem("Start Mihomo", "")
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

	// TUN toggle
	mTun = systray.AddMenuItemCheckbox("TUN Mode", "", readTunStatus())
	go func() {
		for range mTun.ClickedCh {
			toggleTun()
		}
	}()

	// System Proxy toggle
	mProxy = systray.AddMenuItemCheckbox("System Proxy", "", isProxyEnabled())
	go func() {
		for range mProxy.ClickedCh {
			toggleSystemProxy()
		}
	}()

	// Open Dashboard
	mDash := systray.AddMenuItem("Open Dashboard", "")
	go func() {
		for range mDash.ClickedCh {
			exec.Command("open", buildPanelUrl()).Start()
		}
	}()

	// Panel Settings
	mPanelSet := systray.AddMenuItem("Panel Settings", "")
	go func() {
		for range mPanelSet.ClickedCh {
			openPanelSettings()
		}
	}()

	systray.AddSeparator()

	// Subscription Update submenu
	mSubMenu := systray.AddMenuItem("Update Subscription", "")
	mSubAll := mSubMenu.AddSubMenuItem("Update All", "")
	go func() {
		for range mSubAll.ClickedCh {
			for _, s := range subs {
				updateSubscription(s)
			}
		}
	}()
	_ = mSubAll

	rebuildSubMenu(mSubMenu)
	mSubEdit := systray.AddMenuItem("Edit Subscriptions", "")
	go func() {
		for range mSubEdit.ClickedCh {
			exec.Command("open", "-a", "TextEdit", trayCfgPath).Start()
		}
	}()

	systray.AddSeparator()

	// Config Profiles
	mProfile := systray.AddMenuItem("Config Profiles", "")
	go rebuildProfileMenu(mProfile)

	mProfEdit := systray.AddMenuItem("Edit Config Profiles", "")
	go func() {
		for range mProfEdit.ClickedCh {
			exec.Command("open", "-a", "TextEdit", trayCfgPath).Start()
		}
	}()

	systray.AddSeparator()

	// One-click update
	mUpdate := systray.AddMenuItem("Update Components", "")
	go func() {
		for range mUpdate.ClickedCh {
			updateAllComponents()
		}
	}()

	systray.AddSeparator()

	// Run mihomo on startup
	mRunOnStart = systray.AddMenuItemCheckbox("Run Mihomo on Startup", "", runOnStart)
	go func() {
		for range mRunOnStart.ClickedCh {
			toggleRunOnStartup()
		}
	}()

	// Auto-start (launchd)
	mAutoStart = systray.AddMenuItemCheckbox("Auto Start", "", isAutoStartEnabled())
	go func() {
		for range mAutoStart.ClickedCh {
			toggleAutoStart()
		}
	}()

	systray.AddSeparator()

	// Quit
	mQuit := systray.AddMenuItem("Quit", "")
	go func() {
		<-mQuit.ClickedCh
		systray.Quit()
	}()

	// Auto-start mihomo if was running
	if runOnStart {
		go func() {
			time.Sleep(500 * time.Millisecond)
			startMihomo()
			refreshUI()
		}()
	}

	// Status ticker
	go func() {
		ticker := time.NewTicker(2 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			refreshUI()
		}
	}()

	refreshUI()
}

func onExit() {
	runOnStart = isRunning()
	saveTrayConfig()
	if isProxyEnabled() {
		setSystemProxy(false)
	}
	killMihomo()
}

// ─── UI ───

func refreshUI() {
	running := isRunning()
	tunOn := readTunStatus()
	proxyOn := isProxyEnabled()

	if running {
		mStartStop.SetTitle("Stop Mihomo")
		systray.SetIcon(onPng)
		tip := "Mihomo - Running"
		if tunOn {
			tip += " (TUN:ON)"
		}
		if proxyOn {
			tip += " (Proxy:ON)"
		}
		systray.SetTooltip(tip)
	} else {
		mStartStop.SetTitle("Start Mihomo")
		systray.SetIcon(offPng)
		systray.SetTooltip("Mihomo - Stopped")
	}

	if tunOn {
		mTun.Check()
		mTun.SetTitle("TUN Mode (ON)")
	} else {
		mTun.Uncheck()
		mTun.SetTitle("TUN Mode (OFF)")
	}

	if proxyOn {
		mProxy.Check()
		mProxy.SetTitle("System Proxy (ON)")
	} else {
		mProxy.Uncheck()
		mProxy.SetTitle("System Proxy (OFF)")
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

func rebuildProfileMenu(parent *systray.MenuItem) {
	loadTrayConfig()
	for i := range profiles {
		idx := i
		name := profiles[idx].Name
		if profiles[idx].Path == activeCfgPath || resolvePath(profiles[idx].Path) == resolvePath(activeCfgPath) {
			name = "✓ " + name
		}
		item := parent.AddSubMenuItem(name, "")
		go func(p ConfigProfile) {
			for range item.ClickedCh {
				switchProfile(p)
			}
		}(profiles[idx])
	}
}

// ─── Process ───

func startMihomo() {
	if _, err := os.Stat(mihomoPath); os.IsNotExist(err) {
		notify("mihomo not found: " + mihomoPath)
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
	go func() {
		mihomoCmd.Wait()
		mihomoCmd = nil
	}()
	notify("Mihomo started")
}

func stopMihomo() {
	killMihomo()
	notify("Mihomo stopped")
}

func killMihomo() {
	if mihomoCmd != nil && mihomoCmd.Process != nil {
		mihomoCmd.Process.Signal(syscall.SIGTERM)
		time.Sleep(300 * time.Millisecond)
		mihomoCmd.Process.Kill()
		mihomoCmd = nil
	}
	for _, n := range []string{"mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta"} {
		exec.Command("pkill", "-9", "-f", n).Run()
	}
	time.Sleep(200 * time.Millisecond)
}

func isRunning() bool {
	if mihomoCmd != nil && mihomoCmd.Process != nil {
		return mihomoCmd.Process.Signal(syscall.Signal(0)) == nil
	}
	for _, n := range []string{"mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta"} {
		out, _ := exec.Command("pgrep", "-l", "-f", n).Output()
		lines := strings.Split(strings.TrimSpace(string(out)), "\n")
		for _, l := range lines {
			if l != "" && !strings.Contains(l, "mihomo-tray") {
				return true
			}
		}
	}
	return false
}

// ─── TUN ───

func readTunStatus() bool {
	path := resolvePath(activeCfgPath)
	data, err := os.ReadFile(path)
	if err != nil {
		return false
	}
	re := regexp.MustCompile(`tun:\s*\n\s+enable:\s*(true|false)`)
	m := re.FindStringSubmatch(string(data))
	return len(m) > 1 && m[1] == "true"
}

func toggleTun() {
	cur := readTunStatus()
	newVal := !cur
	path := resolvePath(activeCfgPath)

	data, err := os.ReadFile(path)
	if err != nil {
		notify("Cannot read config: " + err.Error())
		return
	}

	re := regexp.MustCompile(`(tun:\s*\n\s+enable:\s*)(true|false)`)
	valStr := "false"
	if newVal {
		valStr = "true"
	}
	newData := re.ReplaceAll(data, []byte("${1}"+valStr))
	os.WriteFile(path, newData, 0644)

	msg := "TUN disabled"
	if newVal {
		msg = "TUN enabled"
	}
	notify(msg + ". Restarting...")

	if isRunning() {
		stopMihomo()
		time.Sleep(500 * time.Millisecond)
		startMihomo()
	}
	refreshUI()
}

// ─── System Proxy ───

func isProxyEnabled() bool {
	out, err := exec.Command("networksetup", "-getwebproxy", getNetworkService()).Output()
	return err == nil && strings.Contains(string(out), "Enabled: Yes")
}

func toggleSystemProxy() {
	setSystemProxy(!isProxyEnabled())
}

func setSystemProxy(enable bool) {
	svc := getNetworkService()
	if enable {
		port := readHTTPPort()
		addr := fmt.Sprintf("localhost:%d", port)
		exec.Command("networksetup", "-setwebproxy", svc, addr).Run()
		exec.Command("networksetup", "-setsecurewebproxy", svc, addr).Run()
		notify("System proxy enabled: " + addr)
	} else {
		exec.Command("networksetup", "-setwebproxystate", svc, "off").Run()
		exec.Command("networksetup", "-setsecurewebproxystate", svc, "off").Run()
		notify("System proxy disabled")
	}
	refreshUI()
}

func getNetworkService() string {
	out, _ := exec.Command("networksetup", "-listallnetworkservices").Output()
	for _, l := range strings.Split(string(out), "\n") {
		l = strings.TrimSpace(l)
		if l == "Wi-Fi" || l == "Ethernet" || (l != "" && !strings.HasPrefix(l, "An asterisk")) {
			return l
		}
	}
	return "Wi-Fi"
}

func readHTTPPort() int {
	path := resolvePath(activeCfgPath)
	data, _ := os.ReadFile(path)
	re := regexp.MustCompile(`^port:\s*(\d+)`)
	m := re.FindStringSubmatch(string(data))
	if len(m) > 1 {
		var p int
		fmt.Sscanf(m[1], "%d", &p)
		return p
	}
	return 7890
}

// ─── Auto Start ───

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
		notify("Auto start disabled")
	} else {
		dir := filepath.Dir(plistPath())
		os.MkdirAll(dir, 0755)
		plist := fmt.Sprintf(`<?xml version="1.0" encoding="UTF-8"?>
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
</plist>`, exePath)
		os.WriteFile(plistPath(), []byte(plist), 0644)
		notify("Auto start enabled")
	}
	refreshUI()
}

func toggleRunOnStartup() {
	runOnStart = !runOnStart
	saveTrayConfig()
	msg := "Run Mihomo on startup: OFF"
	if runOnStart {
		msg = "Run Mihomo on startup: ON"
	}
	notify(msg)
	refreshUI()
}

// ─── Panel ───

func buildPanelUrl() string {
	p := panelPath
	p = strings.Trim(p, "/")
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
		notify("Panel URL: " + buildPanelUrl())
	}
}

// ─── Subscription ───

func updateSubscription(s SubInfo) {
	notify("Updating: " + s.Name + " ...")

	resp, err := httpGet(s.URL)
	if err != nil {
		notify("Download failed: " + err.Error())
		return
	}

	content := string(resp)
	if isBase64(content) {
		dec, err := base64.StdEncoding.DecodeString(strings.TrimSpace(content))
		if err == nil {
			content = string(dec)
		}
	}

	err = mergeConfig(content)
	if err != nil {
		notify("Merge failed: " + err.Error())
		return
	}

	notify("[" + s.Name + "] updated. Restarting...")
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
		return fmt.Errorf("proxies: not found in config")
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
	s = strings.TrimSpace(s)
	if len(s) < 4 || len(s)%4 != 0 {
		return false
	}
	return regexp.MustCompile(`^[A-Za-z0-9+/]+=*$`).MatchString(strings.ReplaceAll(s, " ", ""))
}

// ─── Config Profiles ───

func switchProfile(p ConfigProfile) {
	activeCfgPath = resolvePath(p.Path)
	saveTrayConfig()
	resolveActiveConfig()

	msg := "Switched to: " + p.Name
	notify(msg)

	if isRunning() {
		stopMihomo()
		time.Sleep(500 * time.Millisecond)
		startMihomo()
	}
	refreshUI()
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

// ─── Component Updater ───

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
		{
			name:   "GeoLite2-Country.mmdb",
			apiUrl: "https://api.github.com/repos/P3TERX/GeoLite.mmdb/releases/latest",
			target: filepath.Join(baseDir, "GeoLite2-Country.mmdb"),
		},
		{
			name:   "GeoLite2-ASN.mmdb",
			apiUrl: "https://api.github.com/repos/P3TERX/GeoLite.mmdb/releases/latest",
			target: filepath.Join(baseDir, "GeoLite2-ASN.mmdb"),
		},
		{
			name:   "geosite.dat",
			apiUrl: "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest",
			target: filepath.Join(baseDir, "geosite.dat"),
		},
		{
			name:   "geoip.dat",
			apiUrl: "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest",
			target: filepath.Join(baseDir, "geoip.dat"),
		},
		{
			name:   "country.mmdb",
			apiUrl: "https://api.github.com/repos/MetaCubeX/meta-rules-dat/releases/tags/latest",
			target: filepath.Join(baseDir, "country.mmdb"),
		},
	}

	changedCore := false
	for _, a := range assets {
		notify("Downloading: " + a.name + " ...")
		err := downloadAsset(a)
		if err != nil {
			notify(a.name + " failed: " + err.Error())
		} else {
			notify(a.name + " updated")
			if a.name == "mihomo core" {
				changedCore = true
			}
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
		re := regexp.MustCompile(`"message"\s*:\s*"([^"]+)"`)
		m := re.FindStringSubmatch(string(jsonBody))
		if len(m) > 1 {
			return fmt.Errorf("API: %s", m[1])
		}
		return fmt.Errorf("API error: %s", string(jsonBody))
	}

	url := findAssetUrl(string(jsonBody), a.matchRe, filepath.Base(a.target))
	if url == "" {
		return fmt.Errorf("asset not found in release")
	}

	resp, err := httpGet(url)
	if err != nil {
		return fmt.Errorf("download: %v", err)
	}

	if a.isGz && strings.HasSuffix(strings.ToLower(url), ".gz") {
		return extractFromGz(resp, a.target)
	}
	if a.isZip && strings.HasSuffix(strings.ToLower(url), ".zip") {
		return extractFromZip(resp, a.target)
	}

	return os.WriteFile(a.target, resp, 0755)
}

func extractFromGz(data []byte, target string) error {
	reader, err := gzip.NewReader(bytes.NewReader(data))
	if err != nil {
		return err
	}
	defer reader.Close()

	out, err := os.Create(target)
	if err != nil {
		return err
	}
	defer out.Close()

	_, err = io.Copy(out, reader)
	if err == nil {
		os.Chmod(target, 0755)
	}
	return err
}

func extractFromZip(data []byte, target string) error {
	reader, err := zip.NewReader(bytes.NewReader(data), int64(len(data)))
	if err != nil {
		return err
	}

	targetName := strings.ToLower(filepath.Base(target))
	for _, f := range reader.File {
		if strings.ToLower(filepath.Base(f.Name)) == targetName {
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

			_, err = io.Copy(out, rc)
			if err == nil {
				os.Chmod(target, 0755)
			}
			return err
		}
	}
	return fmt.Errorf("%s not found in zip", targetName)
}

func findAssetUrl(jsonBody, matchRe, name string) string {
	re := regexp.MustCompile(`"browser_download_url"\s*:\s*"([^"]+)"`)
	matches := re.FindAllStringSubmatch(jsonBody, -1)

	if matchRe != "" {
		mre := regexp.MustCompile(matchRe)
		for _, m := range matches {
			fn := strings.ToLower(filepath.Base(m[1]))
			if mre.MatchString(fn) {
				return m[1]
			}
		}
	}

	nameLower := strings.ToLower(name)
	for _, m := range matches {
		if strings.ToLower(filepath.Base(m[1])) == nameLower {
			return m[1]
		}
	}
	for _, m := range matches {
		if strings.Contains(strings.ToLower(m[1]), nameLower) {
			return m[1]
		}
	}
	return ""
}

// ─── Config Persistence ───

func loadTrayConfig() {
	if _, err := os.Stat(trayCfgPath); os.IsNotExist(err) {
		saveDefaultConfig()
	}
	data, err := os.ReadFile(trayCfgPath)
	if err != nil {
		return
	}
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
	subs = cfg.Subscriptions
}

func saveTrayConfig() {
	cfg := TrayConfig{
		Panel: panelConfig{
			Host: panelHost,
			Port: panelPort,
			Path: panelPath,
		},
		Profiles:           profiles,
		ActiveConfigPath:   makeRelative(activeCfgPath),
		RunMihomoOnStartup: runOnStart,
		Subscriptions:      subs,
	}
	data, _ := json.MarshalIndent(cfg, "", "  ")
	os.WriteFile(trayCfgPath, data, 0644)
}

func saveDefaultConfig() {
	saveTrayConfig()
}

func makeRelative(p string) string {
	abs := resolvePath(p)
	base, _ := filepath.Abs(baseDir)
	rel, err := filepath.Rel(base, abs)
	if err == nil && !strings.HasPrefix(rel, "..") {
		return filepath.ToSlash(rel)
	}
	return p
}

// ─── HTTP Helpers ───

func httpGet(url string) ([]byte, error) {
	client := &http.Client{Timeout: 30 * time.Second}
	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0")
	req.Header.Set("Accept", "application/vnd.github.v3+json")

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	return io.ReadAll(resp.Body)
}

// ─── Helpers ───

func notify(msg string) {
	log.Println("[Mihomo]", msg)
	escaped := strings.ReplaceAll(strings.ReplaceAll(msg, `\`, `\\`), `"`, `\"`)
	exec.Command("osascript", "-e",
		fmt.Sprintf(`display notification "%s" with title "Mihomo"`, escaped)).Run()
}
