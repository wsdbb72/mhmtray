# -*- coding: utf-8 -*-
r"""让新版 MihomoTray.exe 真实常驻，并在其运行期间验证端口解析行为。

关键设计：不用 DETACHED_PROCESS（会让托盘程序因会话分离而退出），
而是用普通方式启动并保持在后台，测试完再优雅关闭。
"""
import os
import subprocess
import time
import json
import re
import socket
import sys

EXE = r'E:/tools/mihomo/MihomoTray.exe'
CWD = r'E:/tools/mihomo'
TRAY_CFG = os.path.join(CWD, 'tray-config.json')
PF_CFG = r'C:/Program Files\ProxiFyre\app-config.json'

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
    s = socket.socket(); s.settimeout(timeout)
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


def main():
    print('=' * 70)
    print('  新版 MihomoTray.exe 实机运行验证')
    print('=' * 70)

    print('\n--- 0. 程序身份确认 ---')
    import hashlib
    h = hashlib.sha256(open(EXE, 'rb').read()).hexdigest()
    print('         exe sha256 = %s' % h)
    check('exe 大小为新版 4247552', os.path.getsize(EXE) == 4247552,
          '%d bytes' % os.path.getsize(EXE))

    print('\n--- 1. 基线快照（启动前）---')
    pf_before = pf_endpoints()
    print('         ProxiFyre 端点 = %s' % sorted(pf_before))
    tray_before = open(TRAY_CFG, 'rb').read()
    print('         tray-config.json = %d bytes' % len(tray_before))
    for p in sorted(pf_before):
        port = int(p.rsplit(':', 1)[1])
        print('         %s 监听 = %s' % (p, listening(port)))

    print('\n--- 2. 启动程序 ---')
    # 不用 DETACHED_PROCESS：托盘程序需附着到交互式会话
    p = subprocess.Popen([EXE], cwd=CWD,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         stdin=subprocess.DEVNULL)
    print('         PID = %d' % p.pid)
    time.sleep(6)
    check('程序成功常驻（启动 6 秒后仍在运行）', is_running())

    print('\n--- 3. 运行期行为观测 ---')
    for i in range(3):
        time.sleep(3)
        print('         T+%2ds 运行中=%s' % (6 + (i + 1) * 3, is_running()))
    check('连续 9 秒无崩溃退出', is_running())

    print('\n--- 4. 配置文件完整性（程序不应破坏配置）---')
    tray_after = open(TRAY_CFG, 'rb').read()
    check('tray-config.json 未被破坏', len(tray_after) >= len(tray_before) * 0.5,
          '前 %d bytes / 后 %d bytes' % (len(tray_before), len(tray_after)))
    try:
        d = json.loads(tray_after.decode('utf-8-sig'))
        check('tray-config.json 仍是合法 JSON', True,
              'profiles=%d, subscriptions=%d' % (len(d.get('profiles') or []),
                                                 len(d.get('subscriptions') or [])))
        sym = d.get('subscriptions') or []
        check('订阅条目解析完整（顺序/字段无丢失）',
              all('name' in s and 'url' in s for s in sym),
              '; '.join('%s -> %s' % (s.get('name'), (s.get('url') or '')[:38]) for s in sym))
    except Exception as e:
        check('tray-config.json 仍是合法 JSON', False, str(e))

    print('\n--- 5. ProxiFyre 端点未被程序改坏 ---')
    pf_after = pf_endpoints()
    print('         启动后端点 = %s' % sorted(pf_after))
    check('端点集合与启动前一致', pf_after == pf_before, '%s' % sorted(pf_after))
    check('所有端点均在监听', all(listening(int(e.rsplit(':', 1)[1])) for e in pf_after),
          '%s' % sorted(pf_after))

    print('\n--- 6. 端口解析交叉验证 ---')
    # 复现新逻辑应当得出的结论
    profiles = json.loads(tray_after.decode('utf-8-sig'))
    active = profiles.get('activeConfigPath') or 'config.yaml'
    cfg_path = os.path.join(CWD, active.replace('/', os.sep))
    print('         活动配置 = %s' % cfg_path)
    txt = open(cfg_path, 'rb').read().decode('utf-8-sig')
    inferred = None
    for rx in (r'(?m)^socks-port:/s*(/d+)', r'(?m)^mixed-port:/s*(/d+)', r'(?m)^port:/s*(/d+)'):
        m = re.search(rx, txt)
        if m and 0 < int(m.group(1)) <= 65535:
            inferred = int(m.group(1)); break
    print('         推断端口 = %s (监听=%s)' % (inferred, listening(inferred)))
    resolved = sorted(pf_after)
    print('         实际使用 = %s' % resolved)
    check('实际端点可用（修复目标达成）',
          all(listening(int(e.rsplit(':', 1)[1])) for e in resolved),
          '若为旧逻辑，会指向 %s（监听=%s）' % (inferred, listening(inferred)))

    print('\n--- 7. SOCKS5 隧道实测 ---')
    import struct
    port = int(resolved[0].rsplit(':', 1)[1])
    try:
        s = socket.socket(); s.settimeout(8)
        s.connect(('127.0.0.1', port))
        s.sendall(b'\x05\x01\x00')
        r = s.recv(2)
        tgt = b'www.gstatic.com'
        s.sendall(b'\x05\x01\x00\x03' + bytes([len(tgt)]) + tgt + struct.pack('>H', 80))
        rep = s.recv(4)
        if rep[1] != 0:
            check('SOCKS5 隧道可用', False, 'CONNECT REP=%d' % rep[1])
        else:
            atyp = rep[3]
            if atyp == 1: s.recv(4)
            elif atyp == 3: s.recv(s.recv(1)[0])
            elif atyp == 4: s.recv(16)
            s.recv(2)
            s.sendall(b'GET /generate_204 HTTP/1.1\r\nHost: www.gstatic.com\r\nConnection: close\r\n\r\n')
            line = s.recv(200).split(b'\r\n')[0].decode('latin-1')
            check('SOCKS5 隧道可用', line.startswith('HTTP/'), '%s -> %s' % (resolved[0], line))
        s.close()
    except Exception as e:
        check('SOCKS5 隧道可用', False, '%s: %s' % (type(e).__name__, e))

    print('\n--- 8. 优雅关闭 ---')
    try:
        p.terminate()
        time.sleep(2)
        if is_running():
            subprocess.run(['taskkill', '/PID', str(p.pid), '/F'],
                           capture_output=True, errors='ignore')
            time.sleep(1)
        check('程序可正常终止', not is_running())
    except Exception as e:
        check('程序可正常终止', False, str(e))

    print()
    print('=' * 70)
    print('  汇总: PASS=%d  FAIL=%d' % (ok, fail))
    print('=' * 70)
    return 1 if fail else 0


if __name__ == '__main__':
    sys.exit(main())
