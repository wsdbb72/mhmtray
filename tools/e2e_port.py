# -*- coding: utf-8 -*-
r"""实机端到端验证：新端口解析逻辑 -> 真实 SOCKS5 隧道。

不是只跑单元测试，而是：
  1. 用与 C# ResolveProxiFyreEndpoint() 相同的优先级逻辑，从真实文件算出端点
  2. 校验该端点确实在监听（真实 TCP connect）
  3. 穿过该端点建立真实 SOCKS5 隧道，取回一个真实 HTTP 响应
  4. 反向验证：把端点换成一个死端口，确认检测能识别出来（证明校验有效）

任何一步失败都说明修复无效。
"""
import json
import os
import re
import socket
import struct
import sys

PROXIFYRE_CONFIG = r'C:\Program Files\ProxiFyre\app-config.json'
MHMTRAY_TRAY_CONFIG = r'E:\tools\mihomo\tray-config.json'
MHMTRAY_BASE = r'E:\tools\mihomo'

SOCKS_PORT_RE = re.compile(r'(?m)^socks-port:\s*(\d+)')
MIXED_PORT_RE = re.compile(r'(?m)^mixed-port:\s*(\d+)')
HTTP_PORT_RE = re.compile(r'(?m)^port:\s*(\d+)')
COMMON_PROXY_PORTS = [7897, 7890, 7891, 7893, 7899, 1080, 10808, 2080]

ok_count = 0
fail_count = 0


def check(label, passed, detail=''):
    global ok_count, fail_count
    if passed:
        ok_count += 1
        print('  [PASS] %s' % label)
    else:
        fail_count += 1
        print('  [FAIL] %s' % label)
    if detail:
        print('         %s' % detail)
    return passed


def is_listening(port, host='127.0.0.1', timeout=0.25):
    """真实 TCP 探测，与 C# IsLocalPortListening() 语义一致。"""
    if not (0 < port <= 65535):
        return False
    s = socket.socket()
    s.settimeout(timeout)
    try:
        return s.connect_ex((host, port)) == 0
    except Exception:
        return False
    finally:
        s.close()


def read_proxifyre_endpoint():
    try:
        d = json.load(open(PROXIFYRE_CONFIG, encoding='utf-8-sig'))
        for p in d.get('proxies') or []:
            ep = p.get('socks5ProxyEndpoint')
            if ep:
                return ep
    except Exception:
        pass
    return None


def read_active_config_path():
    """按 tray-config.json 的 activeConfigPath 解析出活动配置绝对路径。"""
    try:
        d = json.load(open(MHMTRAY_TRAY_CONFIG, encoding='utf-8-sig'))
        rel = d.get('activeConfigPath') or 'config.yaml'
        return os.path.join(MHMTRAY_BASE, rel.replace('/', os.sep))
    except Exception:
        return os.path.join(MHMTRAY_BASE, 'config.yaml')


def read_socks_port_from(path):
    """复刻 C# ReadSocksPort()：socks-port > mixed-port > port，0 跳过。"""
    try:
        content = open(path, 'rb').read().decode('utf-8-sig')
    except Exception:
        return 7890
    for rx in (SOCKS_PORT_RE, MIXED_PORT_RE, HTTP_PORT_RE):
        m = rx.search(content)
        if m:
            p = int(m.group(1))
            if 0 < p <= 65535:
                return p
    return 7890


def extract_port(endpoint):
    if not endpoint:
        return None
    i = endpoint.rfind(':')
    if i < 0:
        return None
    try:
        p = int(endpoint[i + 1:].strip())
        return p if 0 < p <= 65535 else None
    except ValueError:
        return None


def resolve_endpoint(override, existing, config_path):
    """复刻修复后的 ResolveProxiFyreEndpoint()。"""
    if override and 0 < override <= 65535:
        return '127.0.0.1:%d' % override, 'override'

    ep = extract_port(existing)
    if existing and ep and is_listening(ep):
        return existing, 'existing(listening)'

    inferred = read_socks_port_from(config_path)
    if is_listening(inferred):
        return '127.0.0.1:%d' % inferred, 'inferred(listening)'

    for c in COMMON_PROXY_PORTS:
        if c == inferred:
            continue
        if is_listening(c):
            return '127.0.0.1:%d' % c, 'common-scan'

    return '127.0.0.1:%d' % inferred, 'fallback(not listening)'


def socks5_http_get(host, port, target_host, target_port, path='/'):
    """真实 SOCKS5 握手 + CONNECT + HTTP GET，返回响应首行。"""
    s = socket.socket()
    s.settimeout(8)
    try:
        s.connect((host, port))
        # greeting: VER=5, NMETHODS=1, METHOD=0(no auth)
        s.sendall(b'\x05\x01\x00')
        resp = s.recv(2)
        if len(resp) < 2 or resp[0] != 5 or resp[1] != 0:
            return None, 'SOCKS5 greeting rejected: %r' % (resp,)

        # CONNECT: VER=5 CMD=1 RSV=0 ATYP=3(domain)
        tb = target_host.encode()
        req = b'\x05\x01\x00\x03' + bytes([len(tb)]) + tb + struct.pack('>H', target_port)
        s.sendall(req)

        rep = s.recv(4)
        if len(rep) < 4 or rep[1] != 0:
            return None, 'SOCKS5 CONNECT failed, REP=%d' % (rep[1] if len(rep) > 1 else -1)

        atyp = rep[3]
        if atyp == 1:
            s.recv(4)
        elif atyp == 3:
            ln = s.recv(1)[0]
            s.recv(ln)
        elif atyp == 4:
            s.recv(16)
        s.recv(2)  # port

        s.sendall(('GET %s HTTP/1.1\r\nHost: %s\r\nUser-Agent: curl/8\r\n'
                   'Connection: close\r\n\r\n' % (path, target_host)).encode())
        data = s.recv(256)
        if not data:
            return None, 'empty response through tunnel'
        return data.split(b'\r\n')[0].decode('latin-1'), None
    except Exception as e:
        return None, '%s: %s' % (type(e).__name__, e)
    finally:
        s.close()


def main():
    print('=' * 70)
    print('  实机端到端验证 — 端口解析 + 真实 SOCKS5 隧道')
    print('=' * 70)

    # ── 1. 活动配置与推断端口 ──
    print('\n--- 1. 活动配置推断 ---')
    cfg = read_active_config_path()
    check('tray-config.json 的 activeConfigPath 可解析', os.path.isfile(cfg), cfg)
    inferred = read_socks_port_from(cfg)
    print('         推断端口 = %d' % inferred)
    check('推断端口在 1-65535 范围内', 0 < inferred <= 65535, 'inferred=%d' % inferred)

    # ── 2. 修复后的解析逻辑 ──
    print('\n--- 2. ResolveProxiFyreEndpoint() 实机结果 ---')
    existing = read_proxifyre_endpoint()
    print('         ProxiFyre 已写端点 = %s' % existing)
    endpoint, path = resolve_endpoint(None, existing, cfg)
    print('         本轮解析 = %s   (路径: %s)' % (endpoint, path))
    check('解析出的端点确实在监听', is_listening(extract_port(endpoint)),
          endpoint)

    # 关键：这正是修复前会漏掉的场景——推断端口(7891)与实监听端口(7897)不一致
    if inferred != extract_port(endpoint):
        print('         [注] 推断端口 %d 与实监听端口 %s 不同；'
              '修复前会直接把 %d 写给 ProxiFyre（死端口）'
              % (inferred, extract_port(endpoint), inferred))
        check('推断端口不一致时仍解析出可用端点', is_listening(extract_port(endpoint)),
              '说明监听校验生效')
        check('推断端口 %d 本身确实无人监听' % inferred, not is_listening(inferred),
              '证实旧逻辑会指向死端口')

    # ── 3. 真实隧道 ──
    print('\n--- 3. 穿过解析出的端点建真实隧道 ---')
    port = extract_port(endpoint)
    line, err = socks5_http_get('127.0.0.1', port, 'www.gstatic.com', 80, '/generate_204')
    if line:
        check('SOCKS5 隧道 + HTTP 响应', line.startswith('HTTP/'),
              '%s:%d -> %s' % ('127.0.0.1', port, line))
    else:
        check('SOCKS5 隧道 + HTTP 响应', False, err or 'unknown')

    line2, err2 = socks5_http_get('127.0.0.1', port, 'cp.cloudflare.com', 80, '/generate_204')
    if line2:
        check('第二条独立隧道', line2.startswith('HTTP/'), line2)
    else:
        check('第二条独立隧道', False, err2 or 'unknown')

    # ── 4. 反向验证：死端口必须被识别 ──
    print('\n--- 4. 反向验证（证明监听校验真的有效）---')
    dead = 59999
    check('死端口 %d 被判定为未监听' % dead, not is_listening(dead),
          '若这里判为在监听，则整个校验逻辑不可信')
    ep2, path2 = resolve_endpoint(None, '127.0.0.1:%d' % dead, cfg)
    print('         ProxiFyre 写成死端口时的解析结果 = %s (%s)' % (ep2, path2))
    check('死端点被正确替换为可用端点',
          path2 != 'existing(listening)' and is_listening(extract_port(ep2)),
          '修复前会原样沿用死端口')

    # ── 5. ProxiFyre 链一致性 ──
    print('\n--- 5. ProxiFyre 配置一致性 ---')
    try:
        d = json.load(open(PROXIFYRE_CONFIG, encoding='utf-8-sig'))
        eps = set(p['socks5ProxyEndpoint'] for p in d.get('proxies') or [])
        apps = sum(len(p.get('appNames') or []) for p in d.get('proxies') or [])
        check('ProxiFyre 端点全部可用', all(is_listening(extract_port(e)) for e in eps),
              '%d 个规则组 / %d 个应用 / 端点 %s' % (len(d.get('proxies') or []), apps, sorted(eps)))
    except Exception as e:
        check('ProxiFyre 配置可读', False, str(e))

    print()
    print('=' * 70)
    print('  汇总: PASS=%d  FAIL=%d' % (ok_count, fail_count))
    print('=' * 70)
    return 1 if fail_count else 0


if __name__ == '__main__':
    sys.exit(main())
