# -*- coding: utf-8 -*-
r"""决定性验证：正确解析 .NET 程序集的 #US（User String）堆。

背景：.NET 的 #US 堆对字符串使用"压缩整数长度前缀 + UTF-16LE 数据"，
      且当字符串中出现 >0x7F 的字节时，会用 0x80 起的转义编码。
      因此对 exe 直接做 UTF-16LE 子串搜索会**漏检长字符串**。
      正确做法是按元数据表定位 #US 堆，逐条解码。

本脚本用最小实现的 PE + CLI 元数据解析器，提取 #US 堆中所有字符串，
再在其中查找四个图标 Base64 与新增文案。
"""
import base64
import io
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
EXE = os.path.join(ROOT, 'artifacts', 'ci', 'MihomoTray.exe')
CS = os.path.join(ROOT, 'mihomo-tray', 'EmbeddedIcons.cs')

data = open(EXE, 'rb').read()


def u16(o):
    return struct.unpack_from('<H', data, o)[0]


def u32(o):
    return struct.unpack_from('<I', data, o)[0]


def u64(o):
    return struct.unpack_from('<Q', data, o)[0]


# ── 1. PE ──
pe = u32(0x3C)
assert data[pe:pe + 4] == b'PE\0\0', 'PE 签名不符'
n_sections = u16(pe + 6)
opt_size = u16(pe + 20)
opt_off = pe + 24
magic = u16(opt_off)
print('PE 可选头 magic: 0x%X (%s)' % (magic, 'PE32+' if magic == 0x20B else 'PE32'))

# DataDirectory 起始
if magic == 0x20B:
    dd_off = opt_off + 112
else:
    dd_off = opt_off + 96

cli_rva = u32(dd_off + 14 * 8)
cli_size = u32(dd_off + 14 * 8 + 4)
print('CLI Header RVA: 0x%X  size=%d' % (cli_rva, cli_size))

sections = []
sec_off = opt_off + opt_size
for i in range(n_sections):
    s = sec_off + i * 40
    name = data[s:s + 8].rstrip(b'\0').decode('latin1')
    vsize = u32(s + 8)
    vaddr = u32(s + 12)
    rawsize = u32(s + 16)
    rawptr = u32(s + 20)
    sections.append((name, vaddr, vsize, rawptr, rawsize))
    print('  sect %-8s VA=0x%08X VS=%8d RAW=0x%08X RS=%8d' % (name, vaddr, vsize, rawptr, rawsize))


def rva2off(rva):
    for name, vaddr, vsize, rawptr, rawsize in sections:
        if vaddr <= rva < vaddr + max(vsize, rawsize):
            return rawptr + (rva - vaddr)
    return None


cli = rva2off(cli_rva)
md_rva = u32(cli + 8)
md = rva2off(md_rva)
print('元数据根偏移: 0x%X' % md)
assert data[md:md + 4] == b'BSJB', 'BSJB 签名不符'
ver_len = u32(md + 12)
ver = data[md + 16:md + 16 + ver_len].rstrip(b'\0').decode('latin1')
print('运行时版本: %s' % ver)
p = md + 16 + ver_len
p += 2  # flags
n_streams = u16(p)
p += 2
print('流数量: %d' % n_streams)

streams = {}
for _ in range(n_streams):
    off = u32(p); size = u32(p + 4); p += 8
    name = b''
    while data[p] != 0:
        name += data[p:p + 1]
        p += 1
    p += 1
    p = (p + 3) & ~3
    streams[name.decode('latin1')] = (md + off, size)
    print('  stream %-8s off=0x%X size=%d' % (name.decode('latin1'), md + off, size))

us_off, us_size = streams['#US']
print()
print('#US 堆: 0x%X .. 0x%X (%d 字节)' % (us_off, us_off + us_size, us_size))


# ── 2. 解析 #US 堆 ──
def read_compressed(off):
    """读取 ECMA-335 压缩无符号整数。"""
    b0 = data[off]
    if b0 & 0x80 == 0:
        return b0, off + 1
    if b0 & 0xC0 == 0x80:
        return ((b0 & 0x3F) << 8) | data[off + 1], off + 2
    return (((b0 & 0x1F) << 24) | (data[off + 1] << 16) |
            (data[off + 2] << 8) | data[off + 3]), off + 4


strings = []
i = us_off + 1  # 首字节 0x00 是空串哨兵
end = us_off + us_size
while i < end:
    blen, j = read_compressed(i)
    if blen == 0:
        i = j
        continue
    nbytes = blen - 1  # 末字节是终止标志
    raw = data[j:j + nbytes]
    # #US 用"特殊 UTF-16"：偶数位是低字节，奇数位是高位（小端）
    try:
        s = raw.decode('utf-16-le', errors='replace')
    except Exception:
        s = ''
    strings.append(s)
    i = j + blen
    if i <= us_off:
        break

print('提取到 %d 条字符串，总长 %d 字符' % (len(strings), sum(len(s) for s in strings)))
blob = '\n'.join(strings)
print()

# ── 3. 校验图标 ──
print('=' * 74)
print('  四个图标 Base64 在 #US 堆中的存在性')
print('=' * 74)
src = io.open(CS, encoding='utf-8-sig').read()
passed = failed = 0
for name in ['OnBase64', 'OffBase64', 'WarnBase64', 'TunBase64']:
    import re
    b64 = re.search(r'string %s = "([^"]+)"' % name, src).group(1)
    hit = b64 in blob
    mark = 'PASS' if hit else 'FAIL'
    if hit:
        passed += 1
    else:
        failed += 1
    print('  [%s] %-12s 长度=%-8d %s' % (
        mark, name, len(b64),
        '完整嵌入' if hit else '未找到'))
print()

# ── 4. 新增短文案 ──
print('--- 新增文案 ---')
for s in ['按应用代理（ProxiFyre）', 'ProxiFyre 未安装 · 点此查看说明',
          '读取中…', '(读取中…)']:
    hit = s in blob
    passed += hit
    failed += (not hit)
    print('  [%s] %s' % ('PASS' if hit else 'FAIL', s))
print()

# ── 5. 长文案（应当能搜到了）──
print('--- 长文案（正确解析后应可搜到）---')
for s in ['未在本机检测到 ProxiFyre。', '预期安装位置：', '按应用代理 · 说明',
          '写入 ProxiFyre 配置失败：', '未检测到任何正在监听的常见代理端口，']:
    hit = s in blob
    passed += hit
    failed += (not hit)
    print('  [%s] %s' % ('PASS' if hit else 'FAIL', s[:44]))

print()
print('=' * 74)
print('  结果: PASS=%d  FAIL=%d' % (passed, failed))
print('=' * 74)
sys.exit(1 if failed else 0)
