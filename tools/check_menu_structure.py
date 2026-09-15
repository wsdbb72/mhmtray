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

print()
print('=' * 72)
print('  结果: %s' % ('PASS' if ok and seq_ok else 'FAIL'))
print('=' * 72)
sys.exit(0 if ok and seq_ok else 1)
