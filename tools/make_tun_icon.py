# -*- coding: utf-8 -*-
r"""从 off.png（KY + 红灯）生成 tun.png（KY + 蓝灯）。

设计约束（用户明确要求）：
  - 保持 KY 字母 + 右上角小圆灯的模式不变
  - 只改指示灯颜色：红 -> 蓝

做法：只在指示灯包围盒内做"红 -> 蓝"的色相映射，
      KY 字母区（暗色低饱和）完全不动，抗锯齿边缘按亮度重着色。
"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ICON_DIR = os.path.join(os.path.dirname(HERE), 'mihomo-tray')

SRC = os.path.join(ICON_DIR, 'off.png')
DST = os.path.join(ICON_DIR, 'tun.png')

# 目标蓝色：与 mihomo/Clash 生态常用的"运行中"蓝一致，饱和度接近原红
BLUE = (41, 121, 255)

# 指示灯区域（含少量外扩以覆盖抗锯齿边缘）
BOX = (200, 56, 255, 112)


def to_blue(r, g, b, a):
    """把红色指示灯像素映射为蓝色，保留亮度层次。"""
    if a == 0:
        return (r, g, b, a)
    # 用原始亮度作为强度（红通道是灯的主亮度来源）
    lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0
    # 高饱和红 -> 纯蓝；亮度决定深浅
    nr = int(BLUE[0] * lum)
    ng = int(BLUE[1] * lum)
    nb = int(BLUE[2] * lum)
    return (min(255, nr), min(255, ng), min(255, nb), a)


def main():
    im = Image.open(SRC).convert('RGBA')
    w, h = im.size
    px = im.load()

    x0, y0, x1, y1 = BOX
    changed = 0
    for y in range(max(0, y0), min(h, y1)):
        for x in range(max(0, x0), min(w, x1)):
            r, g, b, a = px[x, y]
            if a == 0:
                continue
            # 仅改"偏红"的像素；暗色 KY 字母不动
            if r > 90 and r - g > 30 and r - b > 30:
                px[x, y] = to_blue(r, g, b, a)
                changed += 1

    im.save(DST, 'PNG')
    print('源文件   :', SRC)
    print('输出文件 :', DST)
    print('改写像素 : %d' % changed)
    print('尺寸     : %d x %d' % im.size)
    print('文件大小 : %d 字节' % os.path.getsize(DST))


if __name__ == '__main__':
    main()
