# -*- coding: utf-8 -*-
r"""实测"每次打开右键菜单"的开销，复刻 OnMenuOpening 的调用链。

目的：量化卡顿根因，而不是凭猜测优化。
"""
import json
import os
import re
import socket
import subprocess
import time

PF_DIR = r'C:\Program Files\ProxiFyre'
PF_CFG = os.path.join(PF_DIR, 'app-config.json')
PF_EXE = os.path.join(PF_DIR, 'ProxiFyre.exe')
COMMON_PORTS = [7897, 7890, 7891, 7893, 7899, 1080, 10808, 2080]
MIHOMO_NAMES = ['mihomo', 'mihomo-alpha', 'clash-meta', 'Clash.Meta']


def timeit(label, fn, rounds=1):
    t0 = time.perf_counter()
    for _ in range(rounds):
        r = fn()
    dt = (time.perf_counter() - t0) / rounds * 1000.0
    print('  %-42s %8.2f ms' % (label, dt))
    return dt, r


def ps_names(name):
    """等价 Process.GetProcessesByName —— 用 tasklist 近似。"""
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq %s.exe' % name, '/FO', 'CSV'],
                       capture_output=True, text=True, errors='ignore')
    return [l for l in r.stdout.splitlines() if name.lower() in l.lower()]


def is_listening(port, timeout=0.12):
    s = socket.socket()
    s.settimeout(timeout)
    try:
        return s.connect_ex(('127.0.0.1', port)) == 0
    except Exception:
        return False
    finally:
        s.close()


def read_app_names():
    if not os.path.isfile(PF_CFG):
        return []
    txt = open(PF_CFG, encoding='utf-8-sig').read()
    out = []
    for m in re.finditer(r'"appNames"\s*:\s*\[(.*?)\]', txt, re.S):
        for it in re.finditer(r'"([^"]+)"', m.group(1)):
            v = it.group(1).strip()
            if v and v not in out:
                out.append(v)
    return out


def resolve_endpoint():
    """复刻新版 ResolveProxiFyreEndpoint —— 注意它会调 IsLocalPortListening。"""
    # 读取 ProxiFyre 已写端点
    ep = None
    try:
        d = json.load(open(PF_CFG, encoding='utf-8-sig'))
        for p in d.get('proxies') or []:
            if p.get('socks5ProxyEndpoint'):
                ep = p['socks5ProxyEndpoint']
                break
    except Exception:
        pass
    if ep:
        port = int(ep.rsplit(':', 1)[1])
        if is_listening(port):
            return ep
    for c in COMMON_PORTS:
        if is_listening(c):
            return '127.0.0.1:%d' % c
    return '127.0.0.1:7890'


def main():
    print('=' * 72)
    print('  OnMenuOpening 开销实测（复刻调用链）')
    print('=' * 72)
    print()

    print('--- 单次开销分解 ---')
    t_mihomo, _ = timeit('IsMihomoRunning()  全量枚举 4 个进程名',
                         lambda: [ps_names(n) for n in MIHOMO_NAMES])
    t_inst, _ = timeit('IsProxiFyreInstalled()  文件存在性',
                       lambda: os.path.isfile(PF_EXE))
    t_running, _ = timeit('IsProxiFyreRunning()  枚举进程',
                          lambda: ps_names('ProxiFyre'))
    t_names, names = timeit('ReadProxiFyreAppNames()  读文件+正则',
                            read_app_names)
    t_resolve, ep = timeit('ResolveProxiFyreEndpoint()  含 1 次 TCP',
                           resolve_endpoint)
    t_listen, _ = timeit('IsLocalPortListening()  单次 TCP (120ms 超时)',
                         lambda: is_listening(7897))
    print()
    print('  已发现进程名数:', len(names))
    print('  解析出的端点  :', ep)

    total = t_mihomo + t_inst + t_running + t_names + t_resolve
    print()
    print('  => 单次打开菜单同步阻塞约 %.0f ms' % total)

    print()
    print('--- 连续 5 次（模拟快速点几次菜单）---')
    t0 = time.perf_counter()
    for i in range(5):
        [ps_names(n) for n in MIHOMO_NAMES]
        os.path.isfile(PF_EXE)
        ps_names('ProxiFyre')
        read_app_names()
        resolve_endpoint()
    dt = (time.perf_counter() - t0) * 1000
    print('  5 次累计: %.0f ms   平均 %.0f ms/次' % (dt, dt / 5))

    print()
    print('--- 关键对比：缓存失效 vs 生效 ---')
    # 有缓存时 IsMihomoRunning 只查 HasExited（微秒级）
    print('  IsMihomoRunning 有缓存 : ~0.01 ms  (仅 HasExited)')
    print('  IsMihomoRunning 无缓存 : %6.1f ms  (全量枚举)' % t_mihomo)
    print('  -> 第 489 行把 _lastMihomoProcessLookupUtc 重置为 MinValue,')
    print('     5 秒缓存被彻底废掉, 每次打开菜单都走全量枚举路径。')


if __name__ == '__main__':
    main()
