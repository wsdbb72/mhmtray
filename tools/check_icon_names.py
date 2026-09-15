# -*- coding: utf-8 -*-
r"""校验 MihomoTray.cs 中所有 UiStyles.MenuIcon("xxx", ...) 调用
使用的图标名都在 switch 里真实实现（或至少不会导致空白）。

背景：曾出现 MenuIcon("help", ...) —— "help" 未在 switch 中定义，
会落到 default 分支（一个无名圆点），属于"静默降级"的可用性问题。
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
CS = os.path.join(os.path.dirname(HERE), 'mihomo-tray', 'MihomoTray.cs')

src = io.open(CS, encoding='utf-8-sig').read()

# 1. 所有被调用的图标名
used = set(re.findall(r'MenuIcon\(\s*"([a-zA-Z0-9_-]+)"', src))

# 2. switch 中定义的名字（MenuIcon 实现里的 case "xxx"）
i = src.find('public static Image MenuIcon')
if i < 0:
    raise SystemExit('未找到 MenuIcon 实现')
# 截取到下一个同名风格的成员（粗略：取其后 200 行）
seg = src[i:i + 14000]
defined = set(re.findall(r'case\s+"([a-zA-Z0-9_-]+)"', seg))

print('=' * 72)
print('  MenuIcon 图标名一致性校验')
print('=' * 72)
print()
print('已实现 (%d): %s' % (len(defined), ', '.join(sorted(defined))))
print()
print('被调用 (%d): %s' % (len(used), ', '.join(sorted(used))))
print()

missing = sorted(used - defined)
unused = sorted(defined - used)

if missing:
    print('!! 未实现但被调用（会静默降级为无名圆点）:')
    for m in missing:
        # 找出调用位置
        for mm in re.finditer(r'MenuIcon\(\s*"%s"' % re.escape(m), src):
            line = src[:mm.start()].count('\n') + 1
            print('     %-12s  MihomoTray.cs:%d' % (m, line))
else:
    print('OK: 所有被调用的图标名都已实现。')

print()
if unused:
    print('提示: 已实现但当前未被调用: %s' % ', '.join(unused))

print()
print('=' * 72)
print('  结果: %s' % ('PASS' if not missing else 'FAIL'))
print('=' * 72)
sys.exit(1 if missing else 0)
