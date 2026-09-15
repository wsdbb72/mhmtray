#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
cold_warm_probe.py —— 验证「越用越卡」的冷/热差异假说。

假说：
  IsMihomoRunning() 有 5 秒缓存，但**缓存只能缓存"找到了进程"这一结果**。
  一旦找到，_mihomoProcess 变成非 null，之后 CheckMihomoStatus() 只做
  Process.HasExited（微秒级），所以**第二次打开菜单应该飞快**。

  那么"慢"只可能出现在两种时刻：
    (A) 冷启动后第一次打开菜单（_mihomoProcess 还是 null -> 全量枚举 ~1300ms）
    (B) 缓存被主动清空的时刻（_lastMihomoProcessLookupUtc = DateTime.MinValue）
        —— 代码里有 4 处会清空它，其中 2316 行在 OnStartStop 里，
           2472 行在配置切换里，2594 行在订阅流程里。

  另外 ResolveCurrentMode() -> ReadCoreMode() -> GET /configs 是**每次调用都真的发 HTTP**，
  没有任何缓存。9097 未监听时每次稳定 2 秒。这一项**每次打开菜单都会付**。

本脚本连续多次测量，观察第 1 次与后续次的差异。
"""
import http.client
import subprocess
import time

CANDIDATES = ["mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta"]


def ms(t0):
    return (time.perf_counter() - t0) * 1000.0


def enumerate_cold():
    """完整枚举（模拟 _mihomoProcess == null 时的 FindMihomoProcess）。"""
    hits = []
    for nm in CANDIDATES:
        try:
            r = subprocess.run(
                ["tasklist", "/FI", "IMAGENAME eq %s.exe" % nm, "/FO", "CSV", "/NH"],
                capture_output=True, text=True, errors="replace", timeout=20)
            for line in r.stdout.splitlines():
                if line.lower().startswith('"%s.exe"' % nm.lower()):
                    hits.append(nm)
        except Exception:
            pass
    return hits


def get_configs(port=9097, timeout=4.0):
    t0 = time.perf_counter()
    try:
        conn = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
        conn.request("GET", "/configs", headers={"User-Agent": "MihomoTray"})
        resp = conn.getresponse()
        n = len(resp.read())
        conn.close()
        return "HTTP %d / %d bytes" % (resp.status, n)
    except Exception as e:
        return "%s after %.0fms" % (type(e).__name__, ms(t0))


print("=" * 72)
print("A) 进程枚举：冷启动第一次 vs 之后（模拟 5 秒缓存命中）")
print("=" * 72)
for i in range(1, 4):
    t0 = time.perf_counter()
    hits = enumerate_cold()
    print("  第 %d 次全量枚举: %8.2f ms   -> %s" % (i, ms(t0), hits))

print()
print("  说明：C# 端只要 _mihomoProcess != null，之后只做 HasExited（微秒级）。")
print("       所以『找到过一次』之后，进程枚举不再是瓶颈。")

print()
print("=" * 72)
print("B) GET /configs（ReadCoreMode，每次菜单打开都执行，无缓存）")
print("=" * 72)
for i in range(1, 4):
    t0 = time.perf_counter()
    r = get_configs()
    print("  第 %d 次 GET /configs: %8.2f ms   -> %s" % (i, ms(t0), r))

print()
print("=" * 72)
print("C) TCP 连接被拒绝的代价（解释为什么是 2 秒）")
print("=" * 72)
import socket
for port in (9090, 9097, 7891):
    t0 = time.perf_counter()
    s = socket.socket()
    s.settimeout(6.0)
    try:
        s.connect(("127.0.0.1", port))
        print("  :%-5d connect OK   %.2f ms" % (port, ms(t0)))
    except Exception as e:
        print("  :%-5d %-22s %.2f ms  <-- 这就是菜单卡住的时间" % (port, type(e).__name__, ms(t0)))
    finally:
        s.close()

print()
print("=" * 72)
print("结论")
print("=" * 72)
print("  1. GET /configs 每次都真的发 HTTP，命中拒绝时稳定 ~2000ms。")
print("     ResolveCurrentMode() 在菜单渲染链上，所以**每次打开菜单都要付这 2 秒**。")
print("     这才是用户说的『依然很卡』的直接原因。")
print("  2. 进程枚举只在缓存冷的时候才贵（~1300ms），但不是每次付。")
print("  3. 修复方向：")
print("     - 菜单渲染路径上禁止任何 HTTP / 进程枚举；模式也应纳入后台快照。")
print("     - API 探测前先做一次廉价的 TCP 可达性判断，不可达就立刻放弃，")
print("       不要等 4 秒超时（拒绝场景下 2 秒也是白等）。")
print("     - 把 GET /configs 的结果也缓存到快照里，模式变化由快照线程去发现。")
