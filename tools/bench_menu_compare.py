# -*- coding: utf-8 -*-
r"""对比"修复前 vs 修复后"打开右键菜单的 UI 线程耗时。

修复前（OnMenuOpening 同步做全部采集）：
    _lastMihomoProcessLookupUtc = DateTime.MinValue;   <- 废掉 5 秒缓存
    RefreshUI();          -> IsMihomoRunning() 全量枚举 4 个进程名
    RefreshSubscriptions();  -> 读文件
    RefreshAppProxyMenu();   -> IsProxiFyreRunning() 枚举 + 读配置 + TCP 探测

修复后（OnMenuOpening 只读内存快照）：
    ApplySnapshotToMenu();        <- 纯内存字段读取
    ApplySnapshotToAppProxyMenu() <- 纯内存列表遍历
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


def ps_names(name):
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


def before_open():
    """修复前的整条同步链路。"""
    [ps_names(n) for n in MIHOMO_NAMES]      # IsMihomoRunning -> FindMihomoProcess
    ps_names('ProxiFyre')                    # IsProxiFyreRunning
    os.path.isfile(PF_EXE)                   # IsProxiFyreInstalled
    read_app_names()                         # ReadProxiFyreAppNames
    resolve_endpoint()                       # ResolveProxiFyreEndpoint (含 TCP)


# 修复后：UI 线程只读快照。Python 侧用"读已备好的内存结构"等价模拟。
_SNAP = {
    'running': False, 'tun': False, 'proxy': False,
    'pf_installed': False, 'pf_running': False,
    'app_names': ['a'] * 20, 'endpoint': None, 'endpoint_alive': False,
}


def after_open():
    """修复后：只读快照 + 构建菜单项（纯内存）。"""
    s = _SNAP
    items = []
    items.append('status:%s' % s['running'])
    if s['running']:
        items.append('blue' if s['tun'] else ('green' if s['proxy'] else 'red'))
    else:
        items.append('red')
    items.append('tun:%s' % s['tun'])
    items.append('proxy:%s' % s['proxy'])
    for n in s['app_names']:
        items.append(n)
    items.append('ep:%s' % s['endpoint'])
    return items


def bench(label, fn, rounds=5):
    # 预热一次，避免首次的进程创建成本污染
    fn()
    t0 = time.perf_counter()
    for _ in range(rounds):
        fn()
    dt = (time.perf_counter() - t0) / rounds * 1000.0
    print('  %-38s %9.2f ms' % (label, dt))
    return dt


print('=' * 72)
print('  右键菜单 UI 线程耗时对比（修复前 vs 修复后）')
print('=' * 72)
print()

# 先填充快照（模拟后台线程已跑过一轮）
_SNAP['pf_installed'] = os.path.isfile(PF_EXE)
_SNAP['app_names'] = read_app_names() or ['a'] * 20
_SNAP['endpoint'] = resolve_endpoint()
_SNAP['pf_running'] = bool(ps_names('ProxiFyre'))
_SNAP['running'] = bool([p for n in MIHOMO_NAMES for p in ps_names(n)])

print('--- 采样 ---')
t_before = bench('修复前: OnMenuOpening（同步全采集）', before_open, rounds=5)
t_after = bench('修复后: OnMenuOpening（只读快照）', after_open, rounds=2000)
print()

print('--- 结论 ---')
print('  修复前 UI 线程阻塞 : %9.2f ms' % t_before)
print('  修复后 UI 线程阻塞 : %9.2f ms' % t_after)
if t_after > 0:
    print('  提速倍数           : %.0f x' % (t_before / t_after))
print('  减少的绝对耗时     : %9.2f ms' % (t_before - t_after))
print()
print('  说明：修复前 >100ms 的同步阻塞会让 Windows 在用户移动鼠标后才定位菜单，')
print('        表现为"菜单延迟弹出并跟着鼠标乱跑"。')
print('        修复后 UI 线程仅做内存读取，菜单即时渲染。')
print()

print('--- 快照内容抽样 ---')
print('  核心运行     :', _SNAP['running'])
print('  被代理应用数 :', len(_SNAP['app_names']))
print('  上游端点     :', _SNAP['endpoint'])
print('  ProxiFyre 运行:', _SNAP['pf_running'])
print()
print('=' * 72)
