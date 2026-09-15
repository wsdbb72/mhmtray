# -*- coding: utf-8 -*-
"""检查 ProxiFyre 安装状态与配置内容，用于诊断"看不到按应用代理设置"。"""
import json
import os

d = r'C:\Program Files\ProxiFyre'
cfg = os.path.join(d, 'app-config.json')
exe = os.path.join(d, 'ProxiFyre.exe')

print('ProxiFyreDir      =', d)
print('ExePath 存在      =', os.path.isfile(exe))
print('ConfigPath 存在   =', os.path.isfile(cfg))

if not os.path.isfile(cfg):
    raise SystemExit('配置文件不存在，IsProxiFyreInstalled 会返回 False')

txt = open(cfg, encoding='utf-8-sig').read()
data = json.loads(txt)

print()
print('--- app-config.json 顶层键 ---')
for k, v in data.items():
    if isinstance(v, list):
        print('  %-24s list[%d]' % (k, len(v)))
    else:
        print('  %-24s %r' % (k, v))

print()
print('--- proxies ---')
for p in data.get('proxies') or []:
    print('  ', json.dumps(p, ensure_ascii=False))

print()
print('--- logLevel / exeNames / appNames ---')
for k in ('logLevel', 'exeNames', 'appNames'):
    if k in data:
        print('  %-12s = %s' % (k, json.dumps(data[k], ensure_ascii=False)))
