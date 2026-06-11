package main

import "testing"

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
