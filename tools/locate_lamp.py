# -*- coding: utf-8 -*-
r"""定位 off.png 中右上角指示灯（红色）的精确区域，
并据此从 off.png 生成蓝灯变体 tun.png。

原理：off.png 的红色指示灯是唯一的高饱和红像素群，
把它整体色相旋转到蓝色，同时保留 KY 字母与抗锯齿边缘不变。
"""
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ICON_DIR = os.path.join(os.path.dirname(HERE), 'mihomo-tray')

RED = os.path.join(ICON_DIR, 'off.png')

im = Image.open(RED).convert('RGBA')
w, h = im.size
px = im.load()

# 收集"灯"像素：偏红（R 明显大于 G、B），且足够不透明
lamp = []
for y in range(h):
    for x in range(w):
        r, g, b, a = px[x, y]
        if a > 60 and r > 120 and r - g > 60 and r - b > 60:
            lamp.append((x, y))

if not lamp:
    print('未找到红色指示灯像素，无法生成蓝灯')
    sys.exit(1)

xs = [p[0] for p in lamp]
ys = [p[1] for p in lamp]
cx0, cx1 = min(xs), max(xs)
cy0, cy1 = min(ys), max(ys)
print('红灯像素数        : %d' % len(lamp))
print('指示灯包围盒      : x[%d..%d] y[%d..%d]' % (cx0, cx1, cy0, cy1))
print('灯中心            : (%d, %d)' % ((cx0 + cx1) // 2, (cy0 + cy1) // 2))
print('灯直径            : %d x %d' % (cx1 - cx0 + 1, cy1 - cy0 + 1))
print('图像尺寸          : %d x %d' % (w, h))
print()
print('灯位于右上角      : %s' % ('是' if (cx0 + cx1) / 2 > w * 0.6 and (cy0 + cy1) / 2 < h * 0.4 else '否'))
