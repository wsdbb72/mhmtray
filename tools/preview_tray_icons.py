# -*- coding: utf-8 -*-
r"""生成托盘图标预览图，用于肉眼核对"KY + 右上角圆灯"设计是否保留、
以及红/绿/蓝三色是否清晰可辨。"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ICON_DIR = os.path.join(os.path.dirname(HERE), 'mihomo-tray')
OUT_DIR = os.path.join(os.path.dirname(HERE), 'artifacts')
os.makedirs(OUT_DIR, exist_ok=True)

ITEMS = [
    ('off.png', 'RED  (核心未运行 / 都未开)'),
    ('on.png', 'GREEN (仅系统代理)'),
    ('tun.png', 'BLUE (TUN 已开启)'),
    ('warn.png', 'YELLOW (旧 warn，保留兼容)'),
]

# 每个图标渲染成 32/48/128 三档，模拟托盘实际尺寸与放大查看
ZOOMS = [32, 48, 128]
CELL = 160
PAD = 16

canvas_w = CELL * len(ITEMS)
canvas_h = CELL * len(ZOOMS) + 90

canvas = Image.new('RGB', (canvas_w, canvas_h), (246, 246, 248))

for col, (fname, label) in enumerate(ITEMS):
    p = os.path.join(ICON_DIR, fname)
    if not os.path.isfile(p):
        print('缺失:', fname)
        continue
    src = Image.open(p).convert('RGBA')
    for row, z in enumerate(ZOOMS):
        thumb = src.resize((z, z), Image.LANCZOS)
        # 贴到浅灰棋盘上，方便看透明边缘
        tile = Image.new('RGBA', (CELL, CELL), (255, 255, 255, 255))
        ox = (CELL - z) // 2
        oy = (CELL - z) // 2
        tile.alpha_composite(thumb, (ox, oy))
        canvas.paste(tile.convert('RGB'), (col * CELL, row * CELL))

canvas.save(os.path.join(OUT_DIR, 'tray-icons-preview.png'))
print('已写出:', os.path.join(OUT_DIR, 'tray-icons-preview.png'))
print()
print('列顺序 (左->右):')
for i, (fn, lb) in enumerate(ITEMS):
    print('  %d. %-10s %s' % (i + 1, fn, lb))
print('行尺寸 (上->下):', ZOOMS)
