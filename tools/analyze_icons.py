# -*- coding: utf-8 -*-
r"""分析现有托盘图标：确认 KY 字母 + 右上角小圆灯的位置与配色，
为生成"蓝灯"变体提供精确参数。"""
import collections
import os
import sys

try:
    from PIL import Image
except ImportError:
    print('需要 Pillow：pip install pillow')
    sys.exit(1)

HERE = os.path.dirname(os.path.abspath(__file__))
ICON_DIR = os.path.join(os.path.dirname(HERE), 'mihomo-tray')

for name in ('on.png', 'off.png', 'warn.png'):
    p = os.path.join(ICON_DIR, name)
    if not os.path.isfile(p):
        print('%-10s 缺失' % name)
        continue
    im = Image.open(p).convert('RGBA')
    w, h = im.size
    px = im.load()

    # 统计不透明像素的包围盒
    minx, miny, maxx, maxy = w, h, -1, -1
    for y in range(h):
        for x in range(w):
            if px[x, y][3] > 16:
                if x < minx: minx = x
                if y < miny: miny = y
                if x > maxx: maxx = x
                if y > maxy: maxy = y

    # 收集主要颜色（不透明且非黑非白）
    hist = collections.Counter()
    for y in range(0, h, max(1, h // 200)):
        for x in range(0, w, max(1, w // 200)):
            r, g, b, a = px[x, y]
            if a > 200:
                hist[(r // 24 * 24, g // 24 * 24, b // 24 * 24)] += 1

    print('=== %s  %dx%d  RGBA' % (name, w, h))
    print('    不透明包围盒: x[%d..%d] y[%d..%d]' % (minx, maxx, miny, maxy))
    print('    主要颜色 (RGB, 计数):')
    for c, n in hist.most_common(6):
        print('      %-18s %d' % (str(c), n))
    print()
