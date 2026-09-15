#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
菜单图标预览生成器。

背景：MihomoTray.cs 的 CreateMenuIcon 原本为每个 key 都绘制同一个灰色圆点，
等于没有图标信息量。改为按 key 绘制可区分的矢量图形后，需要一个不依赖
.NET 编译的途径来确认「画出来的形状确实能区分」。

做法：把 C# 中的 18x18 绘制指令按等价几何在 Pillow 里重放，输出对照图。
这不是逐像素复刻（GDI+ 与 Pillow 的抗锯齿实现不同），而是形状等价性验证：
能看出每个图标代表什么即可。

用法：
    python tools/icon_preview.py [输出路径]
"""

import math
import os
import sys

try:
    from PIL import Image, ImageDraw
except ImportError:
    print("需要 Pillow：pip install pillow")
    sys.exit(2)

SIZE = 18
SCALE = 8                       # 放大倍数，便于肉眼确认形状
CANVAS = SIZE * SCALE

ICON_TEXT = (128, 143, 156)
ACCENT = (42, 171, 238)
ACTIVE_GREEN = (52, 199, 89)
DANGER = (229, 57, 53)
MUTED = (96, 96, 96)
PANEL = (255, 255, 255)


def s(v):
    """逻辑坐标 -> 画布坐标。"""
    return v * SCALE


def pts(seq):
    return [(s(x), s(y)) for x, y in seq]


def draw_icon(key, active=False):
    """按 CreateMenuIcon 的分支重放绘制；返回放大后的 RGBA 图。"""
    img = Image.new("RGBA", (CANVAS, CANVAS), PANEL + (255,))
    d = ImageDraw.Draw(img)

    fill = ACTIVE_GREEN if active else ICON_TEXT
    if key == "exit":
        fill = DANGER
    elif key == "status-on":
        fill = ACTIVE_GREEN
    elif key == "status-off":
        fill = MUTED

    w = max(1, int(round(1.6 * SCALE)))
    dot = max(1, int(round(1.6 * SCALE)))

    if key == "play":
        d.polygon(pts([(6, 4), (14, 9), (6, 14)]), fill=fill)

    elif key == "stop":
        d.rectangle([s(5.5), s(5.5), s(12.5), s(12.5)], fill=fill)

    elif key == "status-on":
        d.ellipse([s(5), s(5), s(13), s(13)], fill=fill)

    elif key == "status-off":
        d.ellipse([s(5), s(5), s(13), s(13)], outline=fill, width=w)

    elif key == "proxy":
        for a, b in [((3.5, 6.5), (13, 6.5)), ((10.5, 4), (13, 6.5)),
                     ((10.5, 9), (13, 6.5)), ((14.5, 11.5), (5, 11.5)),
                     ((7.5, 9), (5, 11.5)), ((7.5, 14), (5, 11.5))]:
            d.line([s(a[0]), s(a[1]), s(b[0]), s(b[1])], fill=fill, width=w)

    elif key == "tun":
        d.rectangle([s(3.5), s(4), s(14.5), s(12.5)], outline=fill, width=w)
        d.line([s(6), s(12.5), s(6), s(15)], fill=fill, width=w)
        d.line([s(9), s(12.5), s(9), s(15)], fill=fill, width=w)
        d.line([s(12), s(12.5), s(12), s(15)], fill=fill, width=w)

    elif key == "guard":
        d.line(pts([(9, 3.5), (14, 5.5), (14, 9.5), (9, 14.5),
                    (4, 9.5), (4, 5.5), (9, 3.5)]), fill=fill, width=w, joint="curve")

    elif key == "refresh":
        d.arc([s(4), s(4), s(14), s(14)], 60, 310, fill=fill, width=w)
        d.polygon(pts([(9.4, 3.0), (14.1, 4.4), (11.4, 8.1)]), fill=fill)

    elif key == "download":
        for a, b in [((9, 3.5), (9, 10.5)), ((6, 7.5), (9, 10.5)),
                     ((12, 7.5), (9, 10.5)), ((4, 13.5), (14, 13.5))]:
            d.line([s(a[0]), s(a[1]), s(b[0]), s(b[1])], fill=fill, width=w)

    elif key == "profile":
        d.line(pts([(3, 13.5), (3, 5), (7.5, 5), (9, 7),
                    (15, 7), (15, 13.5), (3, 13.5)]),
               fill=fill, width=w, joint="curve")

    elif key == "list":
        for y in (5, 9, 13):
            d.line([s(6), s(y), s(14), s(y)], fill=fill, width=w)
        for y in (4.2, 8.2, 12.2):
            d.ellipse([s(3.2), s(y), s(4.8), s(y + 1.6)], fill=fill)

    elif key == "plus":
        d.line([s(9), s(4.5), s(9), s(13.5)], fill=fill, width=w)
        d.line([s(4.5), s(9), s(13.5), s(9)], fill=fill, width=w)

    elif key == "edit":
        for a, b in [((5, 13), (12, 6)), ((10.6, 4.6), (13.4, 7.4)),
                     ((4.2, 13.8), (5.2, 12.4))]:
            d.line([s(a[0]), s(a[1]), s(b[0]), s(b[1])], fill=fill, width=w)

    elif key == "settings":
        d.ellipse([s(6.5), s(6.5), s(11.5), s(11.5)], outline=fill, width=w)
        for x, y in [(8.2, 3), (8.2, 13.4), (3, 8.2), (13.4, 8.2)]:
            d.ellipse([s(x), s(y), s(x + 1.6), s(y + 1.6)], fill=fill)

    elif key == "panel":
        d.rectangle([s(3.5), s(4.5), s(14.5), s(13.5)], outline=fill, width=w)
        d.line([s(3.5), s(7.5), s(14.5), s(7.5)], fill=fill, width=w)

    elif key == "power":
        d.arc([s(4), s(4.5), s(14), s(14.5)], -60, 240, fill=fill, width=w)
        d.line([s(9), s(3), s(9), s(8)], fill=fill, width=w)

    elif key == "startup":
        for a, b in [((9, 14), (9, 6)), ((6, 9), (9, 6)), ((12, 9), (9, 6)),
                     ((5, 15), (13, 15))]:
            d.line([s(a[0]), s(a[1]), s(b[0]), s(b[1])], fill=fill, width=w)

    elif key == "mode":
        d.line([s(4.5), s(9), s(8), s(9)], fill=fill, width=w)
        d.line([s(8), s(9), s(11), s(4.8)], fill=fill, width=w)
        d.line([s(8), s(9), s(11), s(13.2)], fill=fill, width=w)
        for x, y in [(3, 7.8), (11.6, 3.6), (11.6, 12)]:
            d.ellipse([s(x), s(y), s(x + 2.4), s(y + 2.4)], fill=fill)

    elif key == "appproxy":
        d.rectangle([s(3.5), s(3.5), s(9.5), s(9.5)], outline=fill, width=w)
        d.line([s(11), s(12.5), s(14.5), s(12.5)], fill=fill, width=w)
        d.line([s(12.6), s(10.6), s(14.5), s(12.5)], fill=fill, width=w)
        d.line([s(12.6), s(14.4), s(14.5), s(12.5)], fill=fill, width=w)

    elif key == "app":
        d.rectangle([s(4), s(4), s(14), s(14)], outline=fill, width=w)

    elif key == "empty":
        d.ellipse([s(4), s(4), s(14), s(14)], outline=fill, width=w)
        d.line([s(5.2), s(12.8), s(12.8), s(5.2)], fill=fill, width=w)

    elif key == "exit":
        d.line([s(11.5), s(3.5), s(11.5), s(14.5)], fill=fill, width=w)
        d.line([s(4), s(9), s(10), s(9)], fill=fill, width=w)
        d.line([s(7.5), s(6.5), s(10), s(9)], fill=fill, width=w)
        d.line([s(7.5), s(11.5), s(10), s(9)], fill=fill, width=w)

    else:
        d.ellipse([s(5.5), s(5.5), s(13), s(13)], fill=fill)

    return img


LABELS = [
    ("play", "启动"), ("stop", "停止"), ("status-on", "运行中"),
    ("status-off", "已停止"), ("proxy", "系统代理"), ("tun", "TUN"),
    ("guard", "守护"), ("refresh", "更新"), ("download", "下载"),
    ("profile", "配置"), ("list", "列表"), ("plus", "新增"),
    ("edit", "编辑"), ("settings", "设置"), ("panel", "面板"),
    ("power", "自启"), ("startup", "启动项"), ("mode", "规则模式"),
    ("appproxy", "按应用"), ("app", "应用"), ("empty", "空"),
    ("exit", "退出"),
]

CELL_W = CANVAS + 20
CELL_H = CANVAS + 44


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "artifacts", "menu-icons.png")
    out = os.path.abspath(out)
    os.makedirs(os.path.dirname(out), exist_ok=True)

    cols = 6
    rows = int(math.ceil(len(LABELS) / float(cols)))
    sheet = Image.new("RGB", (cols * CELL_W, rows * CELL_H), (244, 247, 250))

    try:
        from PIL import ImageFont
        font = ImageFont.load_default()
    except Exception:
        font = None

    dr = ImageDraw.Draw(sheet)
    for idx, (key, label) in enumerate(LABELS):
        r, c = divmod(idx, cols)
        x0 = c * CELL_W
        y0 = r * CELL_H

        dr.rectangle([x0 + 6, y0 + 6, x0 + CELL_W - 6, y0 + CELL_H - 6],
                     fill=PANEL, outline=(214, 222, 232))

        icon = draw_icon(key)
        sheet.paste(icon, (x0 + 10, y0 + 10))

        text = "%s\n%s" % (key, label)
        if font:
            dr.multiline_text((x0 + 10, y0 + CANVAS + 14), text,
                              fill=(70, 80, 92), font=font, spacing=2)

    sheet.save(out)
    print("已生成: %s" % out)
    print("图标数: %d" % len(LABELS))


if __name__ == "__main__":
    main()
