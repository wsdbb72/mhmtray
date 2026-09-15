#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
profile_menu_path.py —— 拆解"右键菜单打开"这一刻的真实耗时。

背景：上一轮把 OnMenuOpening 里的进程枚举/文件解析搬到了后台快照线程，
bench 显示 UI 线程从 1472ms 降到 0ms，但用户反馈**菜单依然慢**。
说明真正的瓶颈不在那些被搬走的调用上，而是在**仍然同步留在 UI 线程**的调用里。

本脚本按 ApplySnapshotToMenu() 的实际调用顺序，逐个测量每个子调用的独立耗时，
定位真正的阻塞点。

关键怀疑对象：
  ApplySnapshotToMenu -> RefreshModeMenuFromSnapshot -> ResolveCurrentMode
      -> IsMihomoRunning()   (进程枚举)
      -> ReadCoreMode()      -> TryControllerApi(GET /configs)   <-- HTTP，超时 4000ms
  ApplySnapshotToMenu -> IsAutoStartEnabled()   (注册表读)

mihomo API 返回后 HTTP 连接不会立刻关闭（KeepAlive 由服务端控制），
而本程序每 3 秒就会刷新一次快照并再次请求；一旦服务端把连接攒满，
后续请求将阻塞到 Timeout(4000ms) 才失败 —— 这正是"越用越卡"的典型指纹。
"""
import json
import os
import socket
import subprocess
import sys
import time

BASE = r"E:\tools\mihomo"
TRAY_CONFIG = os.path.join(BASE, "tray-config.json")

# 候选核心进程名（与 C# 端 FindManagedMihomoProcesses 保持一致）
CANDIDATES = ["mihomo", "mihomo-alpha", "clash-meta", "Clash.Meta"]


def banner(t):
    print("\n" + "=" * 72)
    print(t)
    print("=" * 72)


def ms(t0):
    return (time.perf_counter() - t0) * 1000.0


def call(name, fn, repeat=1):
    """跑 fn，返回 (耗时ms, 结果)。repeat>1 时取最大耗时（最能暴露阻塞）。"""
    worst = 0.0
    result = None
    for _ in range(repeat):
        t0 = time.perf_counter()
        try:
            result = fn()
        except Exception as e:
            result = "EXC:" + str(e)
        worst = max(worst, ms(t0))
    print("  {0:<46} {1:>9.2f} ms   {2}".format(name, worst, _short(result)))
    return worst, result


def _short(v):
    s = repr(v)
    return s if len(s) <= 60 else s[:57] + "..."


def find_mihomo_processes():
    """进程枚举：这是上一轮测出的 1447ms 主因，确认它是否仍在 UI 线程上跑。"""
    hits = []
    for nm in CANDIDATES:
        try:
            r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq %s.exe" % nm,
                                "/FO", "CSV", "/NH"],
                               capture_output=True, text=True, errors="replace",
                               timeout=20)
            for line in r.stdout.splitlines():
                if line.lower().startswith('"%s.exe"' % nm.lower()):
                    hits.append(nm)
        except Exception:
            pass
    return hits


def read_tray_config():
    with open(TRAY_CONFIG, "r", encoding="utf-8-sig") as f:
        return json.load(f)


def probe_tcp(port, timeout=1.0):
    s = socket.socket()
    s.settimeout(timeout)
    t0 = time.perf_counter()
    try:
        s.connect(("127.0.0.1", port))
        return True, ms(t0)
    except Exception:
        return False, ms(t0)
    finally:
        s.close()


def http_get_configs(port, secret=None, timeout=4.0):
    """
    模拟 ReadCoreMode() -> TryControllerApi(GET /configs)。
    用最朴素的方式手写 HTTP，避免 requests 依赖，行为更接近 HttpWebRequest。
    """
    import http.client
    t0 = time.perf_counter()
    try:
        conn = http.client.HTTPConnection("127.0.0.1", port, timeout=timeout)
        headers = {"User-Agent": "MihomoTray"}
        if secret:
            headers["Authorization"] = "Bearer " + secret
        conn.request("GET", "/configs", headers=headers)
        resp = conn.getresponse()
        body = resp.read().decode("utf-8", "replace")
        conn.close()
        return "HTTP %d, %d bytes" % (resp.status, len(body))
    except Exception as e:
        return "%s after %.0fms" % (type(e).__name__, ms(t0))
    finally:
        pass


def main():
    banner("0) 环境事实")
    cfg = read_tray_config()
    active = cfg.get("activeConfigPath")
    print("  tray-config.json activeConfigPath = %s" % active)
    print("  proxiFyrePort override = %s" % cfg.get("proxiFyrePort"))
    print("  runMihomoOnStartup = %s" % cfg.get("runMihomoOnStartup"))

    # 从活动配置里读 external-controller
    ext = None
    try:
        ap = os.path.join(BASE, active.replace("/", os.sep))
        with open(ap, "r", encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                ls = line.strip()
                if ls.startswith("external-controller:"):
                    ext = ls.split(":", 1)[1].strip().strip('"').strip("'")
                    break
    except Exception as e:
        print("  读取活动配置失败: %s" % e)
    print("  external-controller = %s" % ext)

    banner("1) 仍在 UI 线程上的调用（ApplySnapshotToMenu 调用链）")
    print("  这些是每次打开菜单都会同步执行的，任何一项慢都会直接卡菜单：\n")

    print("  [ResolveCurrentMode]")
    _, hits = call("IsMihomoRunning()  -> 进程枚举 x4 名字",
                   find_mihomo_processes, repeat=3)

    # 端口：从 external-controller 解析
    ctrl_port = None
    if ext:
        tail = ext.split(":")[-1]
        if tail.isdigit():
            ctrl_port = int(tail)

    print("\n  [ReadCoreMode -> TryControllerApi GET /configs]  (超时 4000ms)")
    if ctrl_port:
        for i in range(1, 4):
            call("第 %d 次 GET /configs (port %d)" % (i, ctrl_port),
                 lambda: http_get_configs(ctrl_port), repeat=1)
    else:
        print("    (无法解析 external-controller 端口，跳过)")

    print("\n  [IsAutoStartEnabled -> 注册表读]")
    call("winreg 读 HKCU\\...\\Run\\MihomoTray",
         lambda: __import__("winreg").OpenKey(
             __import__("winreg").HKEY_CURRENT_USER,
             r"Software\Microsoft\Windows\CurrentVersion\Run", 0, 2)
         and "ok")

    banner("2) 端口监听现状")
    for p in [7890, 7891, 7893, 7897, 9090, 9097]:
        alive, cost = probe_tcp(p)
        print("  127.0.0.1:%-5d 监听=%-5s  connect耗时=%.2fms" % (p, alive, cost))

    banner("3) 结论提示")
    print("  · 若 GET /configs 稳定 <50ms 且进程枚举 <50ms，说明阻塞点不在这里；")
    print("  · 若 GET /configs 偶发接近 4000ms，则命中『连接池耗尽 + Timeout 阻塞』模式，")
    print("    根因就是菜单渲染路径上仍在做 HTTP 请求。")
    print("  · 若进程枚举仍 >500ms，则发现/枚举方式本身需要换（改用 Toolhelp32 快照）。")


if __name__ == "__main__":
    main()
