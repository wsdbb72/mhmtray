# -*- coding: utf-8 -*-
r"""
test_proxifyre_switch.py —— 校验 ProxiFyre 关闭选项的行为契约。

用户反馈"proxifyre 缺少关闭选项"。本脚本把这个需求固化成可回归的断言：

  1) 菜单里必须有一个「关闭 ProxiFyre（停止服务）」总开关；
  2) 该开关在运行时显示"关闭"、停止时显示"启动"；
  3) 关闭动作必须真的停服务（sc stop），而不是只清空 appNames；
  4) 停止路径必须有等待/兜底，避免"点了还在跑"；
  5) 该开关的判定不得依赖可能过期的快照（否则连点会重复执行同一动作）；
  6) 非管理员时必须有明确提示，不能静默失败。

同时区分两种"关闭"语义，避免用户混淆：
  · 启用全部代理 / 停用全部代理  -> 只改 appNames，服务仍在跑
  · 关闭 ProxiFyre（停止服务）   -> 服务真的停掉
"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CS = os.path.join(os.path.dirname(HERE), 'mihomo-tray', 'MihomoTray.cs')
src = io.open(CS, encoding='utf-8-sig').read()


def strip_comments(text):
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            j = text.find('\n', i)
            i = n if j < 0 else j
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            j = text.find('*/', i + 2)
            i = n if j < 0 else j + 2
            continue
        if c == '"':
            out.append(c)
            i += 1
            while i < n:
                if text[i] == '\\':
                    out.append(text[i:i + 2]); i += 2; continue
                out.append(text[i])
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def method_body(source, anchor):
    k = source.find(anchor)
    if k < 0:
        return None
    b = source.find('{', k + len(anchor))
    if b < 0:
        return None
    depth = 0
    j = b
    while j < len(source):
        if source[j] == '{':
            depth += 1
        elif source[j] == '}':
            depth -= 1
            if depth == 0:
                return source[b:j + 1]
        j += 1
    return None


code = strip_comments(src)

PASS = 0
FAIL = 0


def check(cond, label, detail=''):
    global PASS, FAIL
    if cond:
        PASS += 1
        print('  [PASS] %s%s' % (label, ('  ' + detail) if detail else ''))
    else:
        FAIL += 1
        print('  [FAIL] %s%s' % (label, ('  ' + detail) if detail else ''))


print('=' * 74)
print('  ProxiFyre 关闭选项 · 行为契约校验')
print('=' * 74)
print()

# ── 1. 菜单必须有总开关 ──
print('1) 菜单入口')
body_menu = method_body(code, 'void ApplySnapshotToAppProxyMenu()')
check(body_menu is not None, '存在 ApplySnapshotToAppProxyMenu()')

if body_menu:
    check('关闭 ProxiFyre（停止服务）' in body_menu,
          '运行时显示「关闭 ProxiFyre（停止服务）」')
    check('启动 ProxiFyre 服务' in body_menu,
          '停止时显示「启动 ProxiFyre 服务」')
    check('OnToggleProxiFyreService' in body_menu,
          '菜单项绑定了 OnToggleProxiFyreService')
    check('_appProxyServiceItem' in body_menu, '使用独立字段 _appProxyServiceItem')
    # 必须放在"启用全部代理"之前/附近，且与它分隔，避免语义混淆
    p_service = body_menu.find('_appProxyServiceItem')
    p_all = body_menu.find('_appProxyAllItem')
    check(0 < p_service < p_all,
          '总开关排在「启用全部代理」之前（先服务、后名单）')
    check('需管理员' in body_menu,
          '非管理员时菜单文案标注「需管理员」')

# ── 2. 关闭必须真的停服务 ──
print()
print('2) 关闭语义（必须真的停服务）')
body_stop = method_body(code, 'bool StopProxiFyreService(out string error)')
check(body_stop is not None, '存在 StopProxiFyreService()')

if body_stop:
    check('RunSc("stop " + ProxiFyreServiceName)' in body_stop,
          '调用 sc stop <服务名>')
    check('IsProxiFyreRunning()' in body_stop,
          '停止后校验服务确实不在了')
    check('Thread.Sleep' in body_stop,
          '有轮询等待（sc stop 是异步的，发完请求就返回）')
    check('KillProxiFyreProcesses()' in body_stop,
          '有兜底强杀，保证"关闭"语义真的生效')
    check('_isAdmin' in body_stop,
          '非管理员时拒绝并给出原因（不静默失败）')
    # 必须与"清名字"区分：停止函数里不应出现写 appNames 的动作
    check('WriteProxiFyreAppNames' not in body_stop,
          '停止服务不改 appNames（与「停用全部代理」语义分离）')

# ── 3. 两种关闭语义必须在代码注释里被说清 ──
print()
print('3) 两种"关闭"语义分离')
# 注释被 strip 掉了，这里回到原文找说明
check('停用全部代理 = 清空 appNames 后重启服务' in src or
      ('停用全部代理' in src and '清空 appNames' in src),
      '注释明确区分「停用全部代理」与「停止服务」')

body_all = method_body(code, 'void SetAllGameAppsEnabled(bool enable)')
if body_all:
    check('WriteProxiFyreAppNames' in body_all,
          '「停用全部代理」走 WriteProxiFyreAppNames（只改名单）')

# ── 4. 回调不得依赖过期快照 ──
print()
print('4) 回调的状态判定')
body_cb = method_body(code, 'void OnToggleProxiFyreService(object sender, EventArgs e)')
check(body_cb is not None, '存在 OnToggleProxiFyreService()')

if body_cb:
    check('IsProxiFyreRunning()' in body_cb,
          '现场查询权威状态（不用快照，避免连点重复执行同一动作）')
    check('_snapshotProxiFyreRunning' not in body_cb,
          '不依赖 _snapshotProxiFyreRunning（最多滞后 3 秒）')
    check('RefreshUI()' in body_cb, '操作后触发刷新')
    # 失败路径也要刷新，否则 UI 会停在错误状态
    fail_refresh = body_cb.count('RefreshUI()')
    check(fail_refresh >= 3,
          '成功/失败路径都刷新',
          '(RefreshUI 出现 %d 次)' % fail_refresh)

# ── 5. 图标名必须存在（否则会静默退化成无名圆点）──
print()
print('5) 图标名有效性')
implemented = set()
import re
mk = code.find('static Image CreateMenuIcon(string key, bool active)')
if mk > 0:
    seg = code[mk:mk + 16000]
    for m in re.finditer(r'case "([a-z0-9\-]+)":', seg):
        implemented.add(m.group(1))
for name in ('on', 'off'):
    check(name in implemented, '图标 "%s" 已实现' % name)

if body_menu:
    used = set(re.findall(r'MenuIcon\("([a-z0-9\-]+)"', body_menu))
    missing = sorted(x for x in used if x not in implemented)
    check(not missing, '子菜单用到的图标名全部有实现',
          ('缺失: %s' % missing) if missing else '')

print()
print('=' * 74)
print('  汇总: PASS=%d  FAIL=%d' % (PASS, FAIL))
print('=' * 74)
sys.exit(0 if FAIL == 0 else 1)
