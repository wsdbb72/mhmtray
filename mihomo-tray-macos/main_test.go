package main

import (
	"os"
	"testing"
)

func TestParseHTTPPort(t *testing.T) {
	tests := []struct {
		name string
		in   string
		want int
	}{
		{name: "top level", in: "mixed-port: 7893\nport: 7891\n", want: 7891},
		{name: "not first line", in: "allow-lan: true\nport: 8888\n", want: 8888},
		{name: "invalid port", in: "port: 70000\n", want: 7890},
		{name: "missing", in: "socks-port: 7892\n", want: 7890},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := parseHTTPPort([]byte(tt.in), 7890); got != tt.want {
				t.Fatalf("parseHTTPPort() = %d, want %d", got, tt.want)
			}
		})
	}
}

func TestParseNetworksetupProxyState(t *testing.T) {
	enabled, target := parseNetworksetupProxyState("Enabled: Yes\nServer: localhost\nPort: 7891\n")
	if !enabled {
		t.Fatal("expected proxy to be enabled")
	}
	if target != "localhost:7891" {
		t.Fatalf("target = %q, want localhost:7891", target)
	}

	enabled, target = parseNetworksetupProxyState("Enabled: No\nServer: example.com\nPort: 8080\n")
	if enabled {
		t.Fatal("expected proxy to be disabled")
	}
	if target != "example.com:8080" {
		t.Fatalf("target = %q, want example.com:8080", target)
	}
}

func TestParseTunConflicts(t *testing.T) {
	processes := `
101 /Applications/Tailscale.app/Contents/MacOS/Tailscale tailscale-ipn
102 /usr/local/bin/zerotier-one zerotier-one
103 /Applications/Safari.app/Contents/MacOS/Safari Safari
104 /Applications/MihomoTray.app/Contents/MacOS/mihomo-tray mihomo-tray
`
	conflicts := parseTunConflicts(processes, tunConflictTargets)
	if len(conflicts) != 2 {
		t.Fatalf("len(conflicts) = %d, want 2: %#v", len(conflicts), conflicts)
	}
	if conflicts[0].DisplayName != "Tailscale" || conflicts[1].DisplayName != "ZeroTier" {
		t.Fatalf("unexpected conflicts: %#v", conflicts)
	}
}

func TestTunStatusRegexAllowsNestedSettings(t *testing.T) {
	cfg := []byte("port: 7890\ntun:\n  stack: mixed\n  device: utun\n  enable: true\n")
	m := tunStatusRe.FindSubmatch(cfg)
	if len(m) < 3 {
		t.Fatal("expected tun.enable match")
	}
	if string(m[2]) != "true" {
		t.Fatalf("enable = %q, want true", m[2])
	}
	updated := tunStatusRe.ReplaceAll(cfg, []byte("${1}false"))
	if string(updated) != "port: 7890\ntun:\n  stack: mixed\n  device: utun\n  enable: false\n" {
		t.Fatalf("unexpected updated config:\n%s", updated)
	}
}

func TestIsBase64(t *testing.T) {
	if !isBase64("cHJveGllczoKICAtIG5hbWU6IGE=") {
		t.Fatal("expected valid base64")
	}
	if !isBase64("cHJveGll\nczoKICAtIG5hbWU6IGE=") {
		t.Fatal("expected multiline base64")
	}
	if isBase64("proxies:\n  - name: a") {
		t.Fatal("expected yaml text not to be treated as base64")
	}
}

func TestFindAssetUrl(t *testing.T) {
	body := `{
		"assets": [
			{"browser_download_url": "https://example.com/mihomo-darwin-amd64-alpha-test.gz"},
			{"browser_download_url": "https://example.com/mihomo-darwin-arm64-alpha-test.gz"},
			{"browser_download_url": "https://example.com/geosite.dat"}
		]
	}`
	got := findAssetUrl(body, `mihomo-darwin-arm64-alpha-.*\.gz$`, "mihomo")
	if got != "https://example.com/mihomo-darwin-arm64-alpha-test.gz" {
		t.Fatalf("findAssetUrl() = %q", got)
	}
	got = findAssetUrl(body, "", "geosite.dat")
	if got != "https://example.com/geosite.dat" {
		t.Fatalf("findAssetUrl() = %q", got)
	}
}

func TestParseControllerEndpoint(t *testing.T) {
	tests := []struct {
		name       string
		in         string
		wantURL    string
		wantSecret string
	}{
		{
			name:    "port only shorthand",
			in:      "port: 7890\nexternal-controller: :9090\n",
			wantURL: "http://127.0.0.1:9090",
		},
		{
			name:    "explicit loopback",
			in:      "external-controller: 127.0.0.1:9090\n",
			wantURL: "http://127.0.0.1:9090",
		},
		{
			name:    "wildcard listener rewritten to loopback",
			in:      "external-controller: 0.0.0.0:9091\n",
			wantURL: "http://127.0.0.1:9091",
		},
		{
			name:       "with secret",
			in:         "external-controller: :9090\nsecret: my-token\n",
			wantURL:    "http://127.0.0.1:9090",
			wantSecret: "my-token",
		},
		{
			name:       "quoted values",
			in:         "external-controller: \":9092\"\nsecret: \"s3cret\"\n",
			wantURL:    "http://127.0.0.1:9092",
			wantSecret: "s3cret",
		},
		{
			name:    "trailing comment ignored",
			in:      "external-controller: :9090 # panel\n",
			wantURL: "http://127.0.0.1:9090",
		},
		{
			name:    "missing",
			in:      "port: 7890\n",
			wantURL: "",
		},
		{
			name:    "no port",
			in:      "external-controller: localhost\n",
			wantURL: "",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			gotURL, gotSecret := parseControllerEndpoint(tt.in)
			if gotURL != tt.wantURL {
				t.Fatalf("baseURL = %q, want %q", gotURL, tt.wantURL)
			}
			if gotSecret != tt.wantSecret {
				t.Fatalf("secret = %q, want %q", gotSecret, tt.wantSecret)
			}
		})
	}
}

func TestNormalizeMode(t *testing.T) {
	tests := []struct {
		in   string
		want string
	}{
		{in: "rule", want: modeRule},
		{in: "Global", want: modeGlobal},
		{in: " DIRECT ", want: modeDirect},
		{in: "DIRECT", want: modeDirect},
		{in: "", want: modeRule},
		{in: "unexpected", want: modeRule},
	}
	for _, tt := range tests {
		if got := normalizeMode(tt.in); got != tt.want {
			t.Fatalf("normalizeMode(%q) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

func TestConfigModeRegex(t *testing.T) {
	body := `{"port":7890,"mode":"global","log-level":"info"}`
	m := configModeRe.FindSubmatch([]byte(body))
	if len(m) < 2 {
		t.Fatal("expected mode match")
	}
	if got := normalizeMode(string(m[1])); got != modeGlobal {
		t.Fatalf("mode = %q, want global", got)
	}
}

func TestModeScalarReplace(t *testing.T) {
	cfg := []byte("port: 7890\nmode: rule\nlog-level: info\n")
	if !modeScalarRe.Match(cfg) {
		t.Fatal("expected mode match")
	}
	updated := modeScalarRe.ReplaceAll(cfg, []byte("mode: "+modeGlobal))
	want := "port: 7890\nmode: global\nlog-level: info\n"
	if string(updated) != want {
		t.Fatalf("updated =\n%s\nwant\n%s", updated, want)
	}
}

func TestInsertTopLevelScalar(t *testing.T) {
	// 无 mode 字段时插入到注释之后
	in := "# my config\n\nport: 7890\n"
	got := insertTopLevelScalar(in, "mode", modeDirect)
	want := "# my config\n\nmode: direct\nport: 7890\n"
	if got != want {
		t.Fatalf("insertTopLevelScalar() =\n%q\nwant\n%q", got, want)
	}

	// 空内容
	if got := insertTopLevelScalar("", "mode", "rule"); got != "mode: rule\n" {
		t.Fatalf("empty input = %q", got)
	}
}

func TestControllerRequestRejectsMissingEndpoint(t *testing.T) {
	// 未配置 external-controller 时不应发起请求，直接返回错误
	prev := activeCfgPath
	defer func() { activeCfgPath = prev; activeConfigCache = configFileCache{} }()

	dir := t.TempDir()
	path := dir + "/config.yaml"
	if err := os.WriteFile(path, []byte("port: 7890\n"), 0644); err != nil {
		t.Fatal(err)
	}
	activeCfgPath = path
	activeConfigCache = configFileCache{}

	if _, err := controllerRequest("GET", "/configs", ""); err == nil {
		t.Fatal("expected error when external-controller is absent")
	}
}

func TestParseSocksPort(t *testing.T) {
	tests := []struct {
		name string
		in   string
		want int
	}{
		{
			name: "socks-port 优先于 mixed-port 与 port",
			in:   "port: 7890\nsocks-port: 7891\nmixed-port: 7893\n",
			want: 7891,
		},
		{
			name: "socks-port 为 0 表示禁用，回退到 mixed-port",
			in:   "port: 7890\nsocks-port: 0\nmixed-port: 7893\n",
			want: 7893,
		},
		{
			name: "socks-port 与 mixed-port 都为 0，回退到 port",
			in:   "port: 7890\nsocks-port: 0\nmixed-port: 0\n",
			want: 7890,
		},
		{
			name: "只有 mixed-port",
			in:   "mixed-port: 7897\n",
			want: 7897,
		},
		{
			name: "缩进的 socks-port 不算顶层，忽略",
			in:   "tun:/n  socks-port: 7891\nport: 7890\n",
			want: 7890,
		},
		{
			name: "端口超范围，回退默认值",
			in:   "socks-port: 70000\n",
			want: 7890,
		},
		{
			name: "都没有则用 fallback",
			in:   "allow-lan: true\n",
			want: 7890,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := parseSocksPort([]byte(tt.in), 7890); got != tt.want {
				t.Fatalf("parseSocksPort(%q) = %d, want %d", tt.in, got, tt.want)
			}
		})
	}
}

func TestIsLocalPortListeningRejectsInvalid(t *testing.T) {
	for _, p := range []int{0, -1, 70000} {
		if isLocalPortListening(p) {
			t.Fatalf("isLocalPortListening(%d) = true, want false", p)
		}
	}
}

func TestIsLocalPortListeningClosedPort(t *testing.T) {
	// 选一个几乎不可能被占用的高位端口
	if isLocalPortListening(59173) {
		t.Skip("port 59173 unexpectedly in use")
	}
}
