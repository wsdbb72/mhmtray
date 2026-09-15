# -*- coding: utf-8 -*-
r"""把 tun.png（KY + 蓝灯）以 Base64 追加为 EmbeddedIcons.TunBase64。

保持与既有 OnBase64/OffBase64/WarnBase64 完全相同的格式：
  public static readonly string TunBase64 = "<base64>";
"""
import base64
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ICON_DIR = os.path.join(os.path.dirname(HERE), 'mihomo-tray')
CS = os.path.join(ICON_DIR, 'EmbeddedIcons.cs')
PNG = os.path.join(ICON_DIR, 'tun.png')

with open(PNG, 'rb') as f:
    raw = f.read()
b64 = base64.b64encode(raw).decode('ascii')
print('tun.png       : %d 字节' % len(raw))
print('Base64 长度   : %d' % len(b64))
print('Base64 前缀   : %s' % b64[:40])

with open(CS, 'r', encoding='utf-8-sig') as f:
    text = f.read()

if 'TunBase64' in text:
    print('TunBase64 已存在，跳过注入（如需更新请先移除旧字段）')
    raise SystemExit(0)

field = '        public static readonly string TunBase64 = "%s";\n' % b64

# 插到 WarnBase64 那一行之后（保持声明顺序 On/Off/Warn/Tun）
marker = 'WarnBase64 = "'
idx = text.find(marker)
if idx < 0:
    raise SystemExit('未找到 WarnBase64 声明，无法定位插入点')
# 找到该行结尾的分号+换行
end = text.find('";\n', idx)
if end < 0:
    raise SystemExit('WarnBase64 行结尾格式异常')
insert_at = end + len('";\n')

text = text[:insert_at] + field + text[insert_at:]

with open(CS, 'w', encoding='utf-8', newline='\n') as f:
    f.write(text.lstrip('\ufeff'))

print()
print('已写入:', CS)
print('新文件大小: %d 字节' % os.path.getsize(CS))
