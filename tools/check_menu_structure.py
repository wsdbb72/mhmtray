# -*- coding: utf-8 -*-
r"""验证菜单结构：把 BuildMenu 中的顶层结构解析出来，
确认 ProxiFyre 入口已在顶层，且整体层级符合预期。"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CS = os.path.join(os.path.dirname(HERE), 'mihomo-tray', 'MihomoTray.cs')
src = io.open(CS, encoding='utf-8-sig').read()

i = src.find('void BuildMenu()')
if i < 0:
    raise SystemExit('未找到 BuildMenu')
brace = src.find('{', i)
depth = 0
j = brace
while j < len(src):
    if src[j] == '{':
        depth += 1
    elif src[j] == '}':
        depth -= 1
        if depth == 0:
            break
    j += 1
body = src[brace:j + 1]

print('=' * 72)
print('  BuildMenu 结构解析')
print('=' * 72)
print()

# 顶层 _menu.Items.Add(xxx)
tops = re.findall(r'_menu\.Items\.Add\(([^)]+)\);', body)
print('顶层菜单项 (%d):' % len(tops))
for t in tops:
    t = t.strip()
    if t == 'new ToolStripSeparator()':
        print('    ────────────')
    else:
        # 找出标签
        m = re.search(r'new ToolStripMenuItem\("([^"]+)"', t)
        label = m.group(1) if m else t
        print('    %s' % label)
print()

seps = len([t for t in tops if t.strip() == 'new ToolStripSeparator()'])
labels = [t for t in tops if t.strip() != 'new ToolStripSeparator()']
print('统计: %d 个可点项, %d 条分隔线' % (len(labels), seps))
print()

# 断言
ok = True

if '_menu.Items.Add(_appProxyMenu);' in body:
    print('[PASS] 按应用代理在顶层')
else:
    print('[FAIL] 按应用代理不在顶层')
    ok = False

if 'toolsMenu.DropDownItems.Add(_appProxyMenu);' not in body:
    print('[PASS] 已从"工具"子菜单移除')
else:
    print('[FAIL] 仍嵌套在"工具"子菜单')
    ok = False

if '"按应用代理（ProxiFyre）"' in body:
    print('[PASS] 顶层标签显式包含 ProxiFyre')
else:
    print('[FAIL] 顶层标签未包含 ProxiFyre')
    ok = False

# 确认顶层顺序合理：状态 -> 面板/启停 -> 模式/TUN/代理/守护 -> 按应用代理 -> 配置 -> 工具 -> 启动 -> 退出
def pos(needle):
    k = body.find(needle)
    return k if k >= 0 else 10 ** 9

order = [
    ('状态行', '_menu.Items.Add(_statusItem)'),
    ('打开面板', 'openPanelItem'),
    ('启停', '_startStopItem)'),
    ('规则模式', '_modeMenu)'),
    ('TUN', '_tunItem)'),
    ('系统代理', '_proxyItem)'),
    ('系统代理守护', '_proxyGuardItem)'),
    ('按应用代理', '_appProxyMenu)'),
    ('配置与订阅', 'configMenu)'),
    ('工具', 'toolsMenu)'),
    ('启动设置', 'startupMenu)'),
    ('退出', 'exitItem)'),
]
prev = -1
seq_ok = True
for name, needle in order:
    p = pos(needle)
    if p == 10 ** 9:
        print('[WARN] 未定位到: %s' % name)
        continue
    if p < prev:
        print('[FAIL] 顺序错乱: %s' % name)
        seq_ok = False
    prev = p
if seq_ok:
    print('[PASS] 顶层顺序符合预期')

# ── 新增护栏：ProxiFyre 必须有"关闭"入口（用户明确反馈缺失）──
sub = src[src.find('void ApplySnapshotToAppProxyMenu()'):]
sub = sub[:sub.find('\n        /// <summary>ProxiFyre 未安装时')] if 'void ApplySnapshotToAppProxyMenu()' in src else ''
if sub:
    has_stop = 'OnToggleProxiFyreService' in sub
    has_stop_label = ('关闭 ProxiFyre' in sub) and ('停止服务' in sub)
    if has_stop and has_stop_label:
        print('[PASS] 按应用代理子菜单含"关闭 ProxiFyre（停止服务）"开关')
    else:
        print('[FAIL] 按应用代理子菜单缺少关闭开关')
        ok = False

    # 关闭项必须调用真正停服务的实现，而不是只清名单
    if 'StopProxiFyreService' in src and 'RunSc("stop " + ProxiFyreServiceName)' in src:
        print('[PASS] 停止走 sc stop <服务名>（真正停服务，非仅清名单）')
    else:
        print('[FAIL] 未实现真正的服务停止')
        ok = False
else:
    print('[WARN] 未定位到 ApplySnapshotToAppProxyMenu')

# ── 新增护栏：菜单渲染路径不得同步发 HTTP / 枚举进程 ──
# 这是"菜单依然卡顿"的根因，必须长期防回归。
#
# 注意：必须先把注释剥掉再扫。否则精心写的"绝不在这里调 XXX()"这类
# 说明性注释会把检查器自己绊倒，产生"代码有问题"的假警报——
# 本项目已经在这类假警报上浪费过时间（见 #US 堆那次误判）。
def strip_comments(text):
    """移除 // 行注释与 /* */ 块注释，保留代码。"""
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
        if c == '"':                      # 跳过字符串字面量
            out.append(c)
            i += 1
            while i < n:
                if text[i] == '\\':
                    out.append(text[i:i + 2]); i += 2; continue
                out.append(text[i])
                if text[i] == '"':
                    i += 1; break
                i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def method_body(source, anchor):
    """取出某个方法的花括号体（不含注释）。"""
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

body_mode = method_body(code, 'void RefreshModeMenuFromSnapshot(bool running)')
if body_mode is None:
    print('[WARN] 未定位到 RefreshModeMenuFromSnapshot')
else:
    if 'ResolveCurrentMode()' in body_mode:
        print('[FAIL] RefreshModeMenuFromSnapshot 仍调 ResolveCurrentMode —— 会同步发 HTTP / 枚举进程')
        ok = False
    else:
        print('[PASS] RefreshModeMenuFromSnapshot 不调 ResolveCurrentMode（模式只读快照）')

# ApplySnapshotToMenu 整条链上不得出现昂贵调用
body_apply = method_body(code, 'void ApplySnapshotToMenu()')
if body_apply is None:
    print('[WARN] 未定位到 ApplySnapshotToMenu')
else:
    expensive = ['ResolveCurrentMode()', 'IsMihomoRunning()', 'FindManagedMihomoProcesses()',
                 'Process.GetProcessesByName', 'TryControllerApi(', 'IsAutoStartEnabled()',
                 'ReadProxiFyreAppNames()', 'ResolveProxiFyreEndpoint()']
    bad = [x for x in expensive if x in body_apply]
    if not bad:
        print('[PASS] ApplySnapshotToMenu 全链无昂贵调用（纯内存渲染）')
    else:
        print('[FAIL] ApplySnapshotToMenu 仍含昂贵调用: %s' % ', '.join(bad))
        ok = False

# 模式必须真的进了快照发布区
body_cap = method_body(code, 'void CaptureSnapshot()')
if body_cap and '_snapshotMode = mode;' in body_cap and 'mode = ResolveCurrentMode();' in body_cap:
    print('[PASS] 模式已在后台快照中采集并发布（_snapshotMode）')
else:
    print('[FAIL] 模式未纳入快照采集/发布')
    ok = False

print()
print('=' * 72)
print('  结果: %s' % ('PASS' if ok and seq_ok else 'FAIL'))
print('=' * 72)
sys.exit(0 if ok and seq_ok else 1)
