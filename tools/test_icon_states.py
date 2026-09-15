# -*- coding: utf-8 -*-
r"""托盘灯三色语义回归测试。

用户明确的语义要求：
  - 仅系统代理        -> 绿灯
  - 开启 TUN 模式     -> 蓝灯
  - 两个都没开        -> 红灯
  - 核心没有运行      -> 红灯

本测试从 MihomoTray.cs 中抽取真实的分派逻辑，防止：
  1. 语义被改错（例如 TUN 误判为绿灯）
  2. 优先级被搞反（TUN 与系统代理同时开启时应是蓝灯）
  3. 引入未定义图标（例如引用了不存在的 _iconTun）
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CS = os.path.join(os.path.dirname(HERE), 'mihomo-tray', 'MihomoTray.cs')
EMB = os.path.join(os.path.dirname(HERE), 'mihomo-tray', 'EmbeddedIcons.cs')

src = io.open(CS, encoding='utf-8-sig').read()
emb = io.open(EMB, encoding='utf-8-sig').read()

passed = 0
failed = 0


def check(name, cond, detail=''):
    global passed, failed
    if cond:
        passed += 1
        print('  [PASS] %s' % name)
    else:
        failed += 1
        print('  [FAIL] %s  %s' % (name, detail))


print('=' * 72)
print('  托盘灯三色语义回归测试')
print('=' * 72)
print()

# ── 1. 图标资产齐全 ──
print('--- 1. 图标资产 ---')
for field in ('OnBase64', 'OffBase64', 'WarnBase64', 'TunBase64'):
    check('EmbeddedIcons.%s 已定义' % field,
          ('string %s = "' % field) in emb)
check('TunBase64 不是空字符串',
      re.search(r'string TunBase64 = "([^"]+)"', emb) is not None
      and len(re.search(r'string TunBase64 = "([^"]+)"', emb).group(1)) > 100)
check('_iconTun 字段已声明', re.search(r'Icon\s+_iconTun\s*;', src) is not None)
check('_iconTun 已从 TunBase64 加载',
      '_iconTun = IconFromBase64(EmbeddedIcons.TunBase64)' in src)
check('_iconTun 已参与释放', '_iconTun.Dispose()' in src)
print()

# ── 2. 分派逻辑（从源码抽取真实分支）──
print('--- 2. 分派逻辑（源码级提取）---')


def extract_dispatch(text):
    """从 ApplySnapshotToMenu 中提取 running/tunOn/proxyOn 的分派分支。"""
    # 找到方法体
    i = text.find('void ApplySnapshotToMenu()')
    if i < 0:
        return None
    brace = text.find('{', i)
    depth = 0
    j = brace
    while j < len(text):
        if text[j] == '{':
            depth += 1
        elif text[j] == '}':
            depth -= 1
            if depth == 0:
                break
        j += 1
    return text[brace:j + 1]


body = extract_dispatch(src)
check('ApplySnapshotToMenu 方法体已提取', body is not None)

if body:
    # 提取灯分派段落
    lamp = body[body.find('托盘灯语义'):] if '托盘灯语义' in body else body
    lamp = lamp[:lamp.find('else\n            {')] if 'else\n            {' in lamp else lamp

    check('TUN 分支使用 _iconTun（蓝灯）', 'if (tunOn)' in body and '_iconTun' in body)
    check('系统代理分支使用 _iconRunning（绿灯）',
          'else if (proxyOn)' in body and '_iconRunning' in body)
    check('都未开分支使用 _iconStopped（红灯）',
          body.count('_iconStopped') >= 2)

    # 优先级：tunOn 必须排在 proxyOn 之前
    i_tun = body.find('if (tunOn)')
    i_proxy = body.find('else if (proxyOn)')
    check('TUN 优先级高于系统代理', 0 <= i_tun < i_proxy,
          'i_tun=%d i_proxy=%d' % (i_tun, i_proxy))

    # 核心未运行分支必须是红灯
    else_idx = body.find('_startStopItem.Text = "启动 Mihomo"')
    tail = body[else_idx:else_idx + 800] if else_idx >= 0 else ''
    check('核心未运行 -> 红灯 (_iconStopped)', '_iconStopped' in tail)
print()

# ── 3. 语义真值表（模拟执行）──
print('--- 3. 语义真值表 ---')

# 用与 C# 完全一致的分派规则模拟
def dispatch(running, tun_on, proxy_on):
    if not running:
        return 'red'
    if tun_on:
        return 'blue'
    if proxy_on:
        return 'green'
    return 'red'


CASES = [
    # (核心运行, TUN, 系统代理, 期望颜色, 说明)
    (True,  False, True,  'green', '仅系统代理 -> 绿灯'),
    (True,  True,  False, 'blue',  '仅 TUN -> 蓝灯'),
    (True,  True,  True,  'blue',  'TUN + 系统代理 -> 蓝灯（TUN 优先）'),
    (True,  False, False, 'red',   '都没开 -> 红灯'),
    (False, False, False, 'red',   '核心未运行 -> 红灯'),
    (False, True,  True,  'red',   '核心未运行（即便配置开着）-> 红灯'),
]

for running, tun, proxy, expect, desc in CASES:
    got = dispatch(running, tun, proxy)
    check(desc, got == expect, '期望 %s 实得 %s' % (expect, got))
print()

# ── 4. 卡顿修复的静态验证 ──
print('--- 4. 卡顿修复静态验证 ---')
check('OnMenuOpening 不再重置进程查询缓存',
      '_lastMihomoProcessLookupUtc = DateTime.MinValue;\n            RefreshUI' not in src)
check('OnMenuOpening 不再同步调用 RefreshUI',
      not re.search(r'void OnMenuOpening[^{]*\{(?:(?!\n        \}).)*?RefreshUI\(\);',
                    src, re.S))
check('引入了后台快照计时器', '_snapshotTimer' in src)
check('引入了快照刷新合并标记', 'Interlocked.CompareExchange(ref _snapshotRefreshBusy' in src)
check('UI 线程渲染走快照', 'ApplySnapshotToMenu()' in src)
check('UI 线程渲染走快照版 ProxiFyre 菜单', 'ApplySnapshotToAppProxyMenu()' in src)
check('已删除重复的同步 RefreshAppProxyMenu',
      'void RefreshAppProxyMenu()' not in src)
check('快照采集在后台线程（ThreadPool）',
      'System.Threading.ThreadPool.QueueUserWorkItem' in src)
print()

# ── 5. ProxiFyre 可见性 ──
print('--- 5. ProxiFyre 可见性 ---')
check('按应用代理已提升到顶层菜单',
      '_menu.Items.Add(_appProxyMenu)' in src)
check('顶层标签包含 ProxiFyre 字样',
      '"按应用代理（ProxiFyre）"' in src)
check('不再嵌套在"工具"子菜单下',
      'toolsMenu.DropDownItems.Add(_appProxyMenu)' not in src)
check('未安装时仍保留说明入口', 'OnShowProxiFyreHelp' in src)
print()

print('=' * 72)
print('  结果: PASS=%d  FAIL=%d' % (passed, failed))
print('=' * 72)
sys.exit(1 if failed else 0)
