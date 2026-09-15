# -*- coding: utf-8 -*-
r"""验证 CI 产出的 MihomoTray.exe 确实包含本次新增的功能。

★ 方法学（重要的踩坑记录）★
  不要对 .NET exe 直接做 UTF-16LE 子串搜索来判定"字符串是否存在"。
  #US（User String）堆以"压缩长度前缀 + 数据"逐条存放，
  长字符串并不与任意扫描偏移对齐，且线上数据按条切分，
  直接扫描会大量**假阴性**：
    - OffBase64（127684 字符）：头部能中，中段/尾段全不中
    - TunBase64（131428 字符）：头/中/尾全不中（但实际完整存在）
  正确做法是按 PE -> CLI Header -> 元数据流表定位 #US 堆，
  再用 ECMA-335 压缩整数逐条解码（见 tools/parse_us_heap.py）。
"""
import base64
import io
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EXE = os.path.join(ROOT, 'artifacts', 'ci', 'MihomoTray.exe')
CS = os.path.join(ROOT, 'mihomo-tray', 'EmbeddedIcons.cs')

if not os.path.isfile(EXE):
    print('未找到 exe:', EXE)
    sys.exit(1)

data = open(EXE, 'rb').read()


# ── #US 堆解析（最小实现）──
def _u16(o):
    return struct.unpack_from('<H', data, o)[0]


def _u32(o):
    return struct.unpack_from('<I', data, o)[0]


def extract_us_strings():
    pe = _u32(0x3C)
    if data[pe:pe + 4] != b'PE\0\0':
        return []
    n_sections = _u16(pe + 6)
    opt_size = _u16(pe + 20)
    opt_off = pe + 24
    magic = _u16(opt_off)
    dd_off = opt_off + (112 if magic == 0x20B else 96)

    cli_rva = _u32(dd_off + 14 * 8)
    sections = []
    sec_off = opt_off + opt_size
    for i in range(n_sections):
        s = sec_off + i * 40
        sections.append((_u32(s + 12), _u32(s + 8), _u32(s + 20), _u32(s + 16)))

    def rva2off(rva):
        for vaddr, vsize, rawptr, rawsize in sections:
            if vaddr <= rva < vaddr + max(vsize, rawsize):
                return rawptr + (rva - vaddr)
        return None

    cli = rva2off(cli_rva)
    md = rva2off(_u32(cli + 8))
    if data[md:md + 4] != b'BSJB':
        return []

    ver_len = _u32(md + 12)
    p = md + 16 + ver_len + 2
    n_streams = _u16(p)
    p += 2
    streams = {}
    for _ in range(n_streams):
        off = _u32(p); size = _u32(p + 4); p += 8
        name = b''
        while data[p] != 0:
            name += data[p:p + 1]
            p += 1
        p += 1
        p = (p + 3) & ~3
        streams[name.decode('latin1')] = (md + off, size)

    if '#US' not in streams:
        return []
    us_off, us_size = streams['#US']

    def read_compressed(off):
        b0 = data[off]
        if b0 & 0x80 == 0:
            return b0, off + 1
        if b0 & 0xC0 == 0x80:
            return ((b0 & 0x3F) << 8) | data[off + 1], off + 2
        return (((b0 & 0x1F) << 24) | (data[off + 1] << 16) |
                (data[off + 2] << 8) | data[off + 3]), off + 4

    out = []
    i = us_off + 1
    end = us_off + us_size
    while i < end:
        blen, j = read_compressed(i)
        if blen == 0:
            i = j
            continue
        raw = data[j:j + blen - 1]
        out.append(raw.decode('utf-16-le', errors='replace'))
        i = j + blen
    return out


strings = extract_us_strings()
blob = '\n'.join(strings)
u8 = data.decode('utf-8', errors='ignore')

passed = failed = 0


def check(name, cond, detail=''):
    global passed, failed
    if cond:
        passed += 1
        print('  [PASS] %-46s %s' % (name, detail))
    else:
        failed += 1
        print('  [FAIL] %-46s %s' % (name, detail))


print('=' * 76)
print('  CI 产物内容验证（基于 #US 堆正确解析）')
print('=' * 76)
print('文件    : %s' % EXE)
print('大小    : %d 字节' % len(data))
print('#US 堆  : 提取到 %d 条字符串，共 %d 字符' % (len(strings), len(blob)))
print()

print('--- 1. 新增符号（元数据 #Strings / #~，UTF-8）---')
for s, desc in [
    ('CaptureSnapshot', '后台快照采集'),
    ('RequestSnapshotRefresh', '快照刷新请求'),
    ('ApplySnapshotToMenu', '快照 -> 主菜单'),
    ('ApplySnapshotToAppProxyMenu', '快照 -> ProxiFyre 子菜单'),
    ('RefreshModeMenuFromSnapshot', '快照 -> 规则模式'),
    ('ShowMenuLoadingPlaceholder', '快照未就绪占位'),
    ('OnShowProxiFyreHelp', 'ProxiFyre 未安装说明入口'),
    ('_snapshotRefreshBusy', '快照重入保护标记'),
    ('_snapshotReady', '快照就绪标记'),
    ('_snapshotProxiFyreEndpointAlive', '端点存活快照字段'),
    ('_snapshotProxiFyreAppNames', '被代理应用名快照字段'),
    ('_snapshotTimer', '后台快照计时器字段'),
    ('TunBase64', '蓝灯图标 Base64 字段'),
    ('_iconTun', '蓝灯图标字段'),
]:
    check(s, (s in u8) or (s in blob), desc)
print()

print('--- 2. 新增 UI 文案（#US 堆）---')
for s, desc in [
    ('按应用代理（ProxiFyre）', '顶层菜单标签（含 ProxiFyre）'),
    ('ProxiFyre 未安装 · 点此查看说明', '未安装时的说明入口'),
    ('读取中…', '快照未就绪占位'),
    ('(读取中…)', 'ProxiFyre 子菜单占位'),
    ('未在本机检测到 ProxiFyre。', '说明弹窗正文'),
    ('按应用代理 · 说明', '说明弹窗标题'),
    ('预期安装位置：', '说明弹窗正文（含安装路径）'),
]:
    check(s, (s in blob) or (s in u8), desc)
print()

print('--- 3. 旧符号已移除 ---')
check('RefreshAppProxyMenu 已删除',
      not (('RefreshAppProxyMenu' in u8) or ('RefreshAppProxyMenu' in blob)),
      '消除重复同步实现')
print()

print('--- 4. 四个图标 Base64 完整嵌入 ---')
if not os.path.isfile(CS):
    print('  [SKIP] 未找到 EmbeddedIcons.cs，跳过图标比对')
else:
    src = io.open(CS, encoding='utf-8-sig').read()
    for name, label in [('OnBase64', '绿灯'), ('OffBase64', '红灯'),
                        ('WarnBase64', '黄灯（保留）'), ('TunBase64', '蓝灯（新增）')]:
        m = re.search(r'string %s = "([^"]+)"' % name, src)
        if not m:
            check(name, False, '源码中未定义')
            continue
        b64 = m.group(1)
        raw = base64.b64decode(b64)
        check('%s (%s)' % (name, label), b64 in blob,
              '%d 字符 / PNG %d 字节' % (len(b64), len(raw)))
        if name == 'TunBase64':
            check('TunBase64 是合法 PNG', raw[:8] == b'\x89PNG\r\n\x1a\n'
                  and raw[-8:] == b'IEND\xaeB`\x82')
print()

print('=' * 76)
print('  结果: PASS=%d  FAIL=%d' % (passed, failed))
print('=' * 76)
sys.exit(1 if failed else 0)
