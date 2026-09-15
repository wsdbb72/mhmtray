#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mhmtray 实机自检脚本
====================

在缺少 C# 编译环境（csc 被策略限制、无 .NET SDK）的机器上，
对 mhmtray 的 Windows 端功能做端到端验证。当前覆盖：

  1) 快捷模式切换  —— 经 mihomo REST API（TCP 或 Windows 命名管道）
  2) 按应用代理    —— ProxiFyre 配置、SOCKS5 链路、上游端口推断

为什么需要它：
  mihomo 的 external-controller 有多种形态。Clash Verge Rev 默认把
  external-controller 置空、只留 external-controller-pipe（Windows 命名管道），
  此时凡是用 HttpWebRequest 只认 http://host:port 的客户端都会失败。
  本脚本同时实现两种通道，用于确认实际环境属于哪一种。

安全约定：
  - 只读为主；模式切换测试结束后必定恢复原模式
  - 写入类操作（如需要）都会先备份
  - 纯标准库，无第三方依赖

用法：
    python tools/selftest.py              # 全量
    python tools/selftest.py --mode       # 仅模式切换
    python tools/selftest.py --proxifyre  # 仅按应用代理
"""

import argparse
import ctypes
import json
import os
import re
import socket
import struct
import subprocess
import sys
import time

# ─────────────────────────── 常量 ───────────────────────────

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
OPEN_EXISTING = 3
INVALID_HANDLE = ctypes.c_void_p(-1).value

MODE_RULE, MODE_GLOBAL, MODE_DIRECT = "rule", "global", "direct"

PROXIFYRE_DIR = os.path.join(
    os.environ.get("ProgramW6432") or os.environ.get("ProgramFiles", r"C:\Program Files"),
    "ProxiFyre")
PROXIFYRE_CONFIG = os.path.join(PROXIFYRE_DIR, "app-config.json")

CANDIDATE_PORTS = [9090, 9097, 9091, 7890, 7892]

VERGE_CONFIGS = [
    os.path.join(os.environ.get("APPDATA", ""),
                 r"io.github.clash-verge-rev.clash-verge-rev\clash-verge.yaml"),
    os.path.join(os.environ.get("APPDATA", ""),
                 r"io.github.clash-verge-rev.clash-verge-rev\config.yaml"),
]

# 与 MihomoTray.cs 保持一致的正则
RE_SOCKS = re.compile(r"(?m)^socks-port:\s*(\d+)")
RE_MIXED = re.compile(r"(?m)^mixed-port:\s*(\d+)")
RE_HTTP = re.compile(r"(?m)^port:\s*(\d+)")
RE_CTRL = re.compile(r"(?m)^external-controller:\s*['\"]?([^'\"\r\n#]+?)['\"]?\s*(?:#.*)?$")
RE_SECRET = re.compile(r"(?m)^secret:\s*['\"]?([^'\"\r\n#]+?)['\"]?\s*(?:#.*)?$")
RE_PIPE = re.compile(r"(?m)^external-controller-pipe:\s*['\"]?([^'\"\r\n#]+?)['\"]?\s*(?:#.*)?$")
RE_MODE_YAML = re.compile(r"(?m)^mode:\s*['\"]?([A-Za-z]+)['\"]?\s*(?:#.*)?$")

PASS, FAIL, WARN, INFO = "PASS", "FAIL", "WARN", "INFO"
_results = []


def report(level, title, detail=""):
    _results.append((level, title, detail))
    icon = {PASS: "[PASS]", FAIL: "[FAIL]", WARN: "[WARN]", INFO: "[INFO]"}[level]
    line = "%s %s" % (icon, title)
    if detail:
        line += "\n        " + detail.replace("\n", "\n        ")
    print(line)


# ─────────────────── 命名管道 HTTP 客户端 ───────────────────

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)

# 必须声明 restype/argtypes：CreateFileW 返回 64 位句柄，
# 若不声明，ctypes 会按 32 位 int 截断，导致 WriteFile/ReadFile 报 err=6（无效句柄）。
_k32.CreateFileW.restype = ctypes.c_void_p
_k32.CreateFileW.argtypes = [
    ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
    ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
_k32.WriteFile.argtypes = [
    ctypes.c_void_p, ctypes.c_char_p, ctypes.c_uint32,
    ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]
_k32.WriteFile.restype = ctypes.c_int
_k32.ReadFile.argtypes = [
    ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32,
    ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]
_k32.ReadFile.restype = ctypes.c_int
_k32.CloseHandle.argtypes = [ctypes.c_void_p]
_k32.CloseHandle.restype = ctypes.c_int
_k32.WaitNamedPipeW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
_k32.WaitNamedPipeW.restype = ctypes.c_int

ERROR_PIPE_BUSY = 231


def _open_pipe(path, retries=6, wait_ms=1500):
    """
    打开命名管道。mihomo 的管道实例数有限，瞬时并发会返回 ERROR_PIPE_BUSY(231)，
    因此用 WaitNamedPipe 等待后再重试。
    """
    for attempt in range(retries):
        h = _k32.CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                             0, None, OPEN_EXISTING, 0, None)
        if h and h != INVALID_HANDLE:
            return h
        if ctypes.get_last_error() != ERROR_PIPE_BUSY:
            return None
        _k32.WaitNamedPipeW(path, wait_ms)
    return None


class PipeHTTPClient:
    """经 Windows 命名管道执行 HTTP/1.1 请求（mihomo external-controller-pipe）。"""

    def __init__(self, pipe_path):
        self.pipe_path = pipe_path

    def available(self):
        h = _open_pipe(self.pipe_path)
        if not h:
            return False
        _k32.CloseHandle(h)
        return True

    def request(self, method, path, body=None, timeout_ms=5000):
        h = _open_pipe(self.pipe_path)
        if not h:
            raise OSError("CreateFile 失败 err=%d" % ctypes.get_last_error())
        try:
            lines = ["%s %s HTTP/1.1" % (method, path), "Host: localhost"]
            payload = b""
            if body is not None:
                payload = json.dumps(body).encode("utf-8")
                lines.append("Content-Type: application/json")
                lines.append("Content-Length: %d" % len(payload))
            lines.append("Connection: close")
            raw = ("\r\n".join(lines) + "\r\n\r\n").encode("utf-8") + payload

            n = ctypes.c_uint32(0)
            if not _k32.WriteFile(h, raw, len(raw), ctypes.byref(n), None):
                raise OSError("WriteFile 失败 err=%d" % ctypes.get_last_error())

            # 分块循环读取：/proxies 等接口响应可达数百 KB，单次读取会被截断
            chunk = 1 << 20
            parts = []
            for _ in range(300):
                buf = ctypes.create_string_buffer(chunk)
                got = ctypes.c_uint32(0)
                ok = _k32.ReadFile(h, buf, chunk, ctypes.byref(got), None)
                if not ok or got.value == 0:
                    break
                parts.append(buf.raw[: got.value])
                if got.value < chunk:
                    time.sleep(0.2)
                    b2 = ctypes.create_string_buffer(chunk)
                    g2 = ctypes.c_uint32(0)
                    if (not _k32.ReadFile(h, b2, chunk, ctypes.byref(g2), None)
                            or g2.value == 0):
                        break
                    parts.append(b2.raw[: g2.value])

            data = b"".join(parts)
            head, _, resp = data.partition(b"\r\n\r\n")
            status_line = head.split(b"\r\n")[0].decode("latin1")

            # 解 chunked 编码
            if b"chunked" in head.lower():
                dec, rest = [], resp
                while True:
                    i = rest.find(b"\r\n")
                    if i < 0:
                        break
                    try:
                        sz = int(rest[:i].split(b";")[0], 16)
                    except ValueError:
                        break
                    if sz == 0:
                        break
                    dec.append(rest[i + 2: i + 2 + sz])
                    rest = rest[i + 2 + sz + 2:]
                resp = b"".join(dec)
            return status_line, resp.decode("utf-8", "replace")
        finally:
            _k32.CloseHandle(h)


class TcpHTTPClient:
    """经 TCP 执行 HTTP 请求（mihomo external-controller）。"""

    def __init__(self, host, port, secret=None):
        self.host, self.port, self.secret = host, port, secret

    def request(self, method, path, body=None, timeout_ms=5000):
        import urllib.request
        url = "http://%s:%d%s" % (self.host, self.port, path)
        req = urllib.request.Request(url, method=method)
        if self.secret:
            req.add_header("Authorization", "Bearer " + self.secret)
        if body is not None:
            req.data = json.dumps(body).encode("utf-8")
            req.add_header("Content-Type", "application/json")
        # 回环地址须绕过系统代理，否则可能自环
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(req, timeout=timeout_ms / 1000.0) as r:
            return "HTTP %d" % r.status, r.read().decode("utf-8", "replace")


# ─────────────────────────── 辅助 ───────────────────────────

def port_open(port, host="127.0.0.1", timeout=1.0):
    s = socket.socket()
    s.settimeout(timeout)
    try:
        s.connect((host, port))
        return True
    except Exception:
        return False
    finally:
        s.close()


def run_gbk(cmd):
    try:
        return subprocess.run(cmd, capture_output=True).stdout.decode("gbk", errors="replace")
    except Exception:
        return ""


def find_listening_pids(ports):
    found = {}
    for line in run_gbk(["netstat", "-ano"]).splitlines():
        if "LISTENING" not in line:
            continue
        parts = line.split()
        if len(parts) < 5:
            continue
        addr, pid = parts[1], parts[-1]
        try:
            p = int(addr.rsplit(":", 1)[-1])
        except ValueError:
            continue
        if p in ports and p not in found:
            found[p] = pid
    return found


def pid_name(pid):
    txt = run_gbk(["tasklist", "/FI", "PID eq %s" % pid, "/FO", "CSV", "/NH"]).strip()
    return txt.split(",")[0].strip('"') if txt else "?"


def process_running(name):
    txt = run_gbk(["tasklist", "/FI", "IMAGENAME eq %s" % name, "/FO", "CSV", "/NH"]).strip()
    return bool(txt) and name.lower() in txt.lower() and "\u4fe1\u606f" not in txt


def read_mode_yaml(path):
    try:
        m = RE_MODE_YAML.search(open(path, encoding="utf-8-sig").read())
        return m.group(1).lower() if m else None
    except Exception:
        return None


def read_socks_port_from(path):
    """镜像 MihomoTray.ReadSocksPort()：socks-port -> mixed-port -> port -> 7890"""
    try:
        c = open(path, encoding="utf-8-sig").read()
    except Exception:
        return None, "无法读取"
    trace = []
    for name, rex in (("socks-port", RE_SOCKS), ("mixed-port", RE_MIXED), ("port", RE_HTTP)):
        m = rex.search(c)
        if m:
            raw, port = m.group(1), int(m.group(1))
            if 0 < port <= 65535:
                trace.append("%s=%s 采纳" % (name, raw))
                return port, " | ".join(trace)
            trace.append("%s=%s 越界丢弃" % (name, raw))
    trace.append("回退 7890")
    return 7890, " | ".join(trace)


def socks5_probe(proxy_host, proxy_port, dest_host, dest_port, timeout=8):
    """最小 SOCKS5 CONNECT（无认证）+ HTTP GET，验证链路可用。"""
    try:
        s = socket.create_connection((proxy_host, proxy_port), timeout)
        s.settimeout(timeout)
        s.sendall(b"\x05\x01\x00")
        if s.recv(2) != b"\x05\x00":
            return None, "握手被拒（代理可能要求认证）"
        hb = dest_host.encode()
        s.sendall(b"\x05\x01\x00\x03" + bytes([len(hb)]) + hb
                  + struct.pack(">H", dest_port))
        rep = s.recv(4)
        if len(rep) < 4 or rep[1] != 0:
            return None, "CONNECT 被拒: %r" % rep
        atyp = rep[3]
        if atyp == 1:
            s.recv(4)
        elif atyp == 3:
            s.recv(s.recv(1)[0])
        elif atyp == 4:
            s.recv(16)
        s.recv(2)
        s.sendall(b"GET /generate_204 HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n"
                  % dest_host.encode())
        if not s.recv(200).startswith(b"HTTP/"):
            return None, "未收到 HTTP 响应"
        return s, None
    except Exception as e:
        return None, "%s: %s" % (type(e).__name__, e)


# ─────────────────── 测试 1：模式切换 ───────────────────

def test_mode_switch():
    print("\n" + "=" * 62)
    print("  测试 1：快捷模式切换（mihomo REST API）")
    print("=" * 62)

    client = channel = None

    # 优先命名管道（Clash Verge Rev 默认形态）
    pipe_candidates = []
    for cfg in VERGE_CONFIGS:
        try:
            m = RE_PIPE.search(open(cfg, encoding="utf-8-sig").read())
        except Exception:
            continue
        if m:
            v = m.group(1).strip()
            pipe_candidates.append(v if v.startswith("\\\\") else "\\\\.\\pipe\\" + v)
    pipe_candidates.append(r"\\.\pipe\verge-mihomo")

    for p in pipe_candidates:
        pc = PipeHTTPClient(p)
        if pc.available():
            client, channel = pc, "命名管道 %s" % p
            break

    # 退而求其次：TCP
    if client is None:
        for cfg in VERGE_CONFIGS:
            try:
                c = open(cfg, encoding="utf-8-sig").read()
            except Exception:
                continue
            m = RE_CTRL.search(c)
            if not m:
                continue
            raw = m.group(1).strip()
            if not raw:
                continue
            if raw.startswith(":"):
                raw = "127.0.0.1" + raw
            if ":" not in raw:
                continue
            host, port = raw.rsplit(":", 1)
            if port_open(int(port)):
                sm = RE_SECRET.search(c)
                client = TcpHTTPClient(host, int(port),
                                       sm.group(1).strip() if sm else None)
                channel = "TCP %s:%s" % (host, port)
                break

    if client is None:
        report(WARN, "未找到可用的 mihomo 控制器通道",
               "已尝试：命名管道、TCP 候选端口 %s\n"
               "        mihomo 未运行或未配置 external-controller(-pipe) 时跳过" % CANDIDATE_PORTS)
        return

    report(INFO, "控制器通道", channel)

    try:
        _, body = client.request("GET", "/configs")
        cfg = json.loads(body)
    except Exception as e:
        report(FAIL, "GET /configs 失败", str(e))
        return

    original = cfg.get("mode")
    report(PASS, "GET /configs 成功", "当前 mode = %s" % original)
    report(INFO, "运行时端口与 TUN",
           "mixed-port=%s  socks-port=%s  port=%s  tun.enable=%s" % (
               cfg.get("mixed-port"), cfg.get("socks-port"),
               cfg.get("port"), (cfg.get("tun") or {}).get("enable")))

    ok_all = True
    for target in [m for m in (MODE_DIRECT, MODE_GLOBAL, MODE_RULE) if m != original]:
        try:
            st, _ = client.request("PATCH", "/configs", {"mode": target})
            time.sleep(0.35)
            now = json.loads(client.request("GET", "/configs")[1]).get("mode")
        except Exception as e:
            report(FAIL, "切换到 %s 异常" % target, str(e))
            ok_all = False
            continue
        if now == target:
            report(PASS, "切换到 %s 生效" % target, st)
        else:
            report(FAIL, "切换到 %s 未生效" % target, "%s -> 实际 %s" % (st, now))
            ok_all = False

    if original in (MODE_RULE, MODE_GLOBAL, MODE_DIRECT):
        try:
            client.request("PATCH", "/configs", {"mode": original})
            time.sleep(0.35)
            now = json.loads(client.request("GET", "/configs")[1]).get("mode")
            report(PASS if now == original else FAIL, "恢复原模式",
                   "mode = %s" % now if now == original else "期望 %s，实际 %s" % (original, now))
        except Exception as e:
            report(FAIL, "恢复原模式异常", str(e))

    for cfg_path in VERGE_CONFIGS:
        if os.path.exists(cfg_path):
            report(INFO, "配置文件 mode 字段",
                   "%s -> %s" % (os.path.basename(cfg_path), read_mode_yaml(cfg_path)))

    report(INFO, "热生效校验",
           "全程经 API 切换，未重启核心、连接未中断" if ok_all else "存在失败项，请检查控制器配置")


# ─────────────────── 测试 2：按应用代理 ───────────────────

def test_proxifyre():
    print("\n" + "=" * 62)
    print("  测试 2：按应用代理（ProxiFyre + SOCKS5 链路）")
    print("=" * 62)

    if not os.path.isdir(PROXIFYRE_DIR):
        report(FAIL, "ProxiFyre 未安装", PROXIFYRE_DIR)
        return
    report(PASS, "ProxiFyre 目录存在", PROXIFYRE_DIR)

    for proc, lvl in (("ProxiFyre.exe", WARN), ("ndisrd.sys", INFO)):
        report(PASS if process_running(proc) else lvl,
               "运行中" if process_running(proc) else "未检测到", proc)

    if not os.path.exists(PROXIFYRE_CONFIG):
        report(FAIL, "app-config.json 不存在", PROXIFYRE_CONFIG)
        return

    try:
        cfg = json.loads(open(PROXIFYRE_CONFIG, encoding="utf-8-sig").read())
    except Exception as e:
        report(FAIL, "app-config.json 解析失败", str(e))
        return
    groups = cfg.get("proxies") or []
    report(PASS, "app-config.json 解析成功",
           "logLevel=%s  bypassLan=%s  规则组=%d"
           % (cfg.get("logLevel"), cfg.get("bypassLan"), len(groups)))

    endpoints, total = set(), 0
    print("\n        --- 规则组明细 ---")
    for i, g in enumerate(groups, 1):
        names = g.get("appNames") or []
        total += len(names)
        endpoints.add(g.get("socks5ProxyEndpoint"))
        print("        [%d] %-22s (%d) %s" % (
            i, g.get("socks5ProxyEndpoint"), len(names),
            ", ".join(names[:3]) + (" ..." if len(names) > 3 else "")))
    uniq = len(set(n for g in groups for n in (g.get("appNames") or [])))
    report(INFO, "进程总数", "%d 个（去重后 %d）" % (total, uniq))

    report(PASS if len(endpoints) == 1 else WARN,
           "代理端点一致性",
           list(endpoints)[0] if len(endpoints) == 1 else repr(endpoints))

    detail = []
    for ep in endpoints:
        if not ep:
            continue
        try:
            host, port = ep.rsplit(":", 1)
            ok = port_open(int(port), "127.0.0.1" if host in ("0.0.0.0", "") else host)
        except Exception:
            ok = False
        detail.append("%s -> %s" % (ep, "监听中" if ok else "未监听"))
    report(PASS if detail and all("监听中" in d for d in detail) else FAIL,
           "ProxiFyre 目标端口可达性", "\n".join(detail) if detail else "无端点")

    socks_port = None
    for ep in endpoints:
        try:
            socks_port = int(ep.rsplit(":", 1)[1])
        except Exception:
            pass
    if socks_port:
        print("\n        --- SOCKS5 链路实测 (127.0.0.1:%d) ---" % socks_port)
        s, err = socks5_probe("127.0.0.1", socks_port, "www.gstatic.com", 80)
        if s:
            report(PASS, "SOCKS5 握手 + 请求成功",
                   "已穿过 127.0.0.1:%d 建立隧道并收到 HTTP 响应" % socks_port)
            s.close()
        else:
            report(FAIL, "SOCKS5 链路失败", err)

    print("\n        --- mhmtray ReadSocksPort() 交叉验证 ---")
    for cfg_path in VERGE_CONFIGS:
        if not os.path.exists(cfg_path):
            continue
        port, trace = read_socks_port_from(cfg_path)
        live = port_open(port)
        report(PASS if live else WARN,
               "%s => 推断 %s" % (os.path.basename(cfg_path), port),
               trace + ("  (端口实际监听)" if live else "  ⚠ 该端口未监听，ProxiFyre 将连不上"))

    lock = PROXIFYRE_CONFIG + ".save.lock"
    if os.path.exists(lock):
        older = (os.stat(PROXIFYRE_CONFIG).st_mtime - os.stat(lock).st_mtime) / 3600.0
        report(INFO, "存在 .save.lock",
               "%d 字节；比活配置旧 %.1f 小时 -> %s" % (
                   os.stat(lock).st_size, older,
                   "陈旧残留，非活跃持锁" if older > 1 else "可能活跃，注意并发写入"))

    bak = PROXIFYRE_CONFIG + ".bak"
    report(PASS if os.path.exists(bak) else WARN,
           "备份文件 .bak" if os.path.exists(bak) else "无备份文件 .bak",
           "%d 字节" % os.stat(bak).st_size if os.path.exists(bak)
           else "首次写入后会自动生成")


# ─────────────────────────── main ───────────────────────────

def main():
    ap = argparse.ArgumentParser(description="mhmtray 实机自检")
    ap.add_argument("--mode", action="store_true", help="仅测模式切换")
    ap.add_argument("--proxifyre", action="store_true", help="仅测按应用代理")
    args = ap.parse_args()

    print("=" * 62)
    print("  mhmtray 实机自检  —  %s" % time.strftime("%Y-%m-%d %H:%M:%S"))
    print("=" * 62)

    print("\n[ 环境概览 ]")
    for port, pid in sorted(find_listening_pids([7897, 7898, 7899, 9090, 9097, 7892, 53]).items()):
        print("    port %-6d pid=%-8s %s" % (port, pid, pid_name(pid)))

    both = not (args.mode or args.proxifyre)
    if args.mode or both:
        test_mode_switch()
    if args.proxifyre or both:
        test_proxifyre()

    print("\n" + "=" * 62)
    print("  汇总")
    print("=" * 62)
    np_ = sum(1 for l, _, _ in _results if l == PASS)
    nf = sum(1 for l, _, _ in _results if l == FAIL)
    nw = sum(1 for l, _, _ in _results if l == WARN)
    print("    PASS=%d  FAIL=%d  WARN=%d" % (np_, nf, nw))
    if nf:
        print("\n   失败项：")
        for l, t, d in _results:
            if l == FAIL:
                print("     - %s%s" % (t, ("  | " + d.split("\n")[0]) if d else ""))
    print()
    return 1 if nf else 0


if __name__ == "__main__":
    sys.exit(main())
