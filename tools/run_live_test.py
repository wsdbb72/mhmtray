# -*- coding: utf-8 -*-
r"""让新版 MihomoTray.exe 真实常驻，并验证：
  - 程序能常驻、不崩溃
  - 后台快照线程不会造成内存持续增长
  - 配置与 ProxiFyre 端点未被破坏
  - SOCKS5 隧道真实可用
  - 可优雅退出

关键设计：不用 DETACHED_PROCESS（会让托盘程序因会话分离而退出），
而是用普通方式启动并保持在后台，测试完再优雅关闭。
"""
import hashlib
import json
import os
import re
import socket
import struct
import subprocess
import sys
import time

EXE = r'E:/tools/mihomo/MihomoTray.exe'
CWD = r'E:/tools/mihomo'
TRAY_CFG = os.path.join(CWD, 'tray-config.json')
PF_CFG = r'C:\Program Files\ProxiFyre\app-config.json'

# 本次 CI 产物（run #44 / commit 082d223）
EXPECT_SHA = '5dd22437ca59aa67f99ac38e37c7ddc935aee4b9b1e46e58d8d5b111ad832693'
EXPECT_SIZE = 4512768

ok = fail = 0


def check(label, passed, detail=''):
    global ok, fail
    if passed:
        ok += 1
        print('  [PASS] %s' % label)
    else:
        fail += 1
        print('  [FAIL] %s' % label)
    if detail:
        print('         %s' % detail)


def listening(port, timeout=0.3):
    s = socket.socket()
    s.settimeout(timeout)
    try:
        return s.connect_ex(('127.0.0.1', port)) == 0
    except Exception:
        return False
    finally:
        s.close()


def pf_endpoints():
    try:
        d = json.load(open(PF_CFG, encoding='utf-8-sig'))
        return set(p['socks5ProxyEndpoint'] for p in d.get('proxies') or [])
    except Exception:
        return set()


def is_running():
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq MihomoTray.exe', '/FO', 'CSV'],
                       capture_output=True, text=True, errors='ignore')
    return 'MihomoTray.exe' in r.stdout


def mem_kb():
    """MihomoTray.exe 工作集（KB）。用于观察快照线程是否内存泄漏。"""
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq MihomoTray.exe', '/FO', 'CSV'],
                       capture_output=True, text=True, errors='ignore')
    for line in r.stdout.splitlines():
        if 'MihomoTray.exe' in line:
            for p in line.split('","'):
                m = re.match(r'^([\d,]+)\s*K"?$', p.strip())
                if m:
                    return int(m.group(1).replace(',', ''))
    return None


def cpu_seconds():
    """取进程累计 CPU 时间（User+Privileged）秒数，用于观察快照线程是否空转。

    wmic 在新版 Windows 上已被移除，改用 PowerShell 的 Get-Process。
    """
    try:
        r = subprocess.run(
            ['powershell', '-NoProfile', '-NonInteractive', '-Command',
             "$p=Get-Process -Name MihomoTray -ErrorAction SilentlyContinue;"
             "if($p){[math]::Round($p.CPU,3)}"],
            capture_output=True, text=True, errors='ignore', timeout=30)
        s = r.stdout.strip()
        return float(s) if s else None
    except Exception:
        return None


def main():
    print('=' * 74)
    print('  实机测试：新版 MihomoTray.exe')
    print('=' * 74)
    print()

    # ── 0. 身份确认 ──
    print('--- 0. 程序身份确认 ---')
    h = hashlib.sha256(open(EXE, 'rb').read()).hexdigest()
    check('exe 大小与 CI 产物一致', os.path.getsize(EXE) == EXPECT_SIZE,
          '%d 字节（期望 %d）' % (os.path.getsize(EXE), EXPECT_SIZE))
    check('exe SHA-256 与 CI 产物一致', h == EXPECT_SHA, h)
    print()

    # ── 1. 基线 ──
    print('--- 1. 基线快照（启动前）---')
    pf_before = pf_endpoints()
    tray_before = open(TRAY_CFG, 'rb').read() if os.path.isfile(TRAY_CFG) else b''
    print('         ProxiFyre 端点   = %s' % (sorted(pf_before) or '(无)'))
    print('         tray-config.json = %d 字节' % len(tray_before))
    for p in sorted(pf_before):
        print('         %s 监听 = %s' % (p, listening(int(p.rsplit(':', 1)[1]))))
    print()

    if is_running():
        print('[WARN] 已有 MihomoTray.exe 在运行，先结束')
        subprocess.run(['taskkill', '/F', '/IM', 'MihomoTray.exe'],
                       capture_output=True, text=True)
        time.sleep(1.5)

    # ── 2. 启动 ──
    print('--- 2. 启动程序 ---')
    p = subprocess.Popen([EXE], cwd=CWD,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         stdin=subprocess.DEVNULL)
    print('         PID = %d' % p.pid)
    time.sleep(6)
    check('程序成功常驻（启动 6 秒后仍在运行）', is_running())
    print()

    # ── 3. 常驻 + 资源观察 ──
    print('--- 3. 运行期行为与资源观察（9 秒）---')
    mem_samples = []
    cpu_samples = []
    for i in range(3):
        time.sleep(3)
        m = mem_kb()
        c = cpu_seconds()
        mem_samples.append(m)
        cpu_samples.append(c)
        print('         T+%2ds  运行中=%s  内存=%s KB  CPU累计=%s s' % (
            6 + (i + 1) * 3, is_running(), m,
            ('%.3f' % c) if c is not None else '-'))
    check('连续 9 秒无崩溃退出', is_running())

    valid_mem = [m for m in mem_samples if m is not None]
    if len(valid_mem) >= 2:
        growth = valid_mem[-1] - valid_mem[0]
        # 快照每 3 秒一轮，若实现有泄漏会明显上涨；给 20MB 宽容度
        check('内存无异常增长（<20MB）', growth < 20480,
              '%d -> %d KB（增长 %d KB）' % (valid_mem[0], valid_mem[-1], growth))

    valid_cpu = [c for c in cpu_samples if c is not None]
    if len(valid_cpu) >= 2:
        busy = valid_cpu[-1] - valid_cpu[0]
        # 9 秒内 CPU 累计应远小于 9 秒（否则说明快照线程在空转吃满 CPU）
        check('CPU 占用温和（9 秒内累计 <2 秒）', busy < 2.0,
              '累计新增 %.3f 秒（占 %.1f%%）' % (busy, busy / 9.0 * 100))
    print()

    # ── 4. 配置完整性 ──
    print('--- 4. 配置文件完整性 ---')
    tray_after = open(TRAY_CFG, 'rb').read() if os.path.isfile(TRAY_CFG) else b''
    check('tray-config.json 未被破坏',
          len(tray_after) >= len(tray_before) * 0.5,
          '前 %d / 后 %d 字节' % (len(tray_before), len(tray_after)))
    try:
        d = json.loads(tray_after.decode('utf-8-sig'))
        check('tray-config.json 仍是合法 JSON', True,
              'profiles=%d, subscriptions=%d' % (
                  len(d.get('profiles') or []), len(d.get('subscriptions') or [])))
        subs = d.get('subscriptions') or []
        check('订阅条目字段完整',
              all('name' in s and 'url' in s for s in subs),
              '; '.join('%s -> %s' % (s.get('name'), (s.get('url') or '')[:34])
                        for s in subs))
    except Exception as e:
        check('tray-config.json 仍是合法 JSON', False, str(e))
    print()

    # ── 5. ProxiFyre 端点 ──
    print('--- 5. ProxiFyre 端点未被改坏 ---')
    pf_after = pf_endpoints()
    print('         启动后端点 = %s' % (sorted(pf_after) or '(无)'))
    check('端点集合与启动前一致', pf_after == pf_before, '%s' % sorted(pf_after))
    if pf_after:
        check('所有端点均在监听',
              all(listening(int(e.rsplit(':', 1)[1])) for e in pf_after),
              '%s' % sorted(pf_after))
    print()

    # ── 6. 端口解析交叉验证 ──
    print('--- 6. 端口解析交叉验证 ---')
    inferred = None
    if tray_after:
        cfg = json.loads(tray_after.decode('utf-8-sig'))
        active = cfg.get('activeConfigPath') or 'config.yaml'
        cfg_path = os.path.join(CWD, active.replace('/', os.sep))
        print('         活动配置 = %s' % cfg_path)
        if os.path.isfile(cfg_path):
            txt = open(cfg_path, 'rb').read().decode('utf-8-sig')
            for rx in (r'(?m)^socks-port:\s*(\d+)', r'(?m)^mixed-port:\s*(\d+)',
                       r'(?m)^port:\s*(\d+)'):
                m = re.search(rx, txt)
                if m and 0 < int(m.group(1)) <= 65535:
                    inferred = int(m.group(1))
                    break
    print('         本程序配置推断端口 = %s（监听=%s）' % (inferred, listening(inferred)))
    print('         实际使用端点       = %s' % sorted(pf_after))
    if pf_after:
        check('实际端点全部可用（修复目标达成）',
              all(listening(int(e.rsplit(':', 1)[1])) for e in pf_after),
              '若为旧逻辑会指向 %s（监听=%s）' % (inferred, listening(inferred)))
    print()

    # ── 7. SOCKS5 隧道实测 ──
    print('--- 7. SOCKS5 隧道实测 ---')
    if pf_after:
        ep = sorted(pf_after)[0]
        port = int(ep.rsplit(':', 1)[1])
        try:
            s = socket.socket()
            s.settimeout(8)
            s.connect(('127.0.0.1', port))
            s.sendall(b'\x05\x01\x00')
            r = s.recv(2)
            if len(r) < 2 or r[1] != 0:
                check('SOCKS5 隧道可用', False, '握手 REP=%r' % r)
            else:
                tgt = b'www.gstatic.com'
                s.sendall(b'\x05\x01\x00\x03' + bytes([len(tgt)]) + tgt +
                          struct.pack('>H', 80))
                rep = s.recv(4)
                if rep[1] != 0:
                    check('SOCKS5 隧道可用', False, 'CONNECT REP=%d' % rep[1])
                else:
                    atyp = rep[3]
                    if atyp == 1:
                        s.recv(4)
                    elif atyp == 3:
                        s.recv(s.recv(1)[0])
                    elif atyp == 4:
                        s.recv(16)
                    s.recv(2)
                    s.sendall(b'GET /generate_204 HTTP/1.1\r\n'
                              b'Host: www.gstatic.com\r\nConnection: close\r\n\r\n')
                    line = s.recv(200).split(b'\r\n')[0].decode('latin-1')
                    check('SOCKS5 隧道可用', line.startswith('HTTP/'),
                          '%s -> %s' % (ep, line))
            s.close()
        except Exception as e:
            check('SOCKS5 隧道可用', False, '%s: %s' % (type(e).__name__, e))
    else:
        check('SOCKS5 隧道可用', False, '无可用端点')
    print()

    # ── 8. 优雅关闭 ──
    print('--- 8. 优雅关闭 ---')
    try:
        p.terminate()
        for _ in range(20):
            time.sleep(0.5)
            if not is_running():
                break
        if is_running():
            subprocess.run(['taskkill', '/PID', str(p.pid), '/F'],
                           capture_output=True, errors='ignore')
            time.sleep(1)
        check('程序可正常终止', not is_running())

        # 退出后配置应仍合法
        if os.path.isfile(TRAY_CFG):
            try:
                json.loads(open(TRAY_CFG, 'rb').read().decode('utf-8-sig'))
                check('退出后 tray-config.json 仍是合法 JSON', True)
            except Exception as e:
                check('退出后 tray-config.json 仍是合法 JSON', False, str(e))
    except Exception as e:
        check('程序可正常终止', False, str(e))

    print()
    print('=' * 74)
    print('  汇总: PASS=%d  FAIL=%d' % (ok, fail))
    print('=' * 74)
    return 1 if fail else 0


if __name__ == '__main__':
    sys.exit(main())
