# -*- coding: utf-8 -*-
r"""把 CI 产出的新 exe 部署到 E:\tools\mihomo\，带备份与哈希校验。"""
import hashlib
import os
import shutil
import sys

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                   'artifacts', 'ci', 'MihomoTray.exe')
DST_DIR = r'E:\tools\mihomo'
DST = os.path.join(DST_DIR, 'MihomoTray.exe')


def sha256(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not os.path.isfile(SRC):
        print('未找到新 exe:', SRC)
        return 1
    if not os.path.isdir(DST_DIR):
        print('部署目录不存在:', DST_DIR)
        return 1

    print('=' * 72)
    print('  部署 MihomoTray.exe')
    print('=' * 72)
    new_hash = sha256(SRC)
    print('新版本 : %s' % SRC)
    print('  大小 : %d 字节' % os.path.getsize(SRC))
    print('  SHA  : %s' % new_hash)
    print()

    # 检查是否有进程占用
    import subprocess
    r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq MihomoTray.exe', '/FO', 'CSV'],
                       capture_output=True, text=True, errors='ignore')
    if 'MihomoTray.exe' in r.stdout:
        print('[WARN] MihomoTray.exe 正在运行，请先退出后再部署。')
        print(r.stdout.strip()[:400])
        return 2
    print('[ OK ] 无进程占用')

    # 备份现有版本
    if os.path.isfile(DST):
        old_hash = sha256(DST)
        print('[ OK ] 现有版本 SHA: %s' % old_hash)
        if old_hash == new_hash:
            print('[ OK ] 与新版完全一致，无需部署。')
            return 0
        stamp = __import__('datetime').datetime.now().strftime('%Y%m%d-%H%M%S')
        bak = DST + '.bak-' + stamp
        shutil.copy2(DST, bak)
        bak_hash = sha256(bak)
        print('[ OK ] 已备份 -> %s' % bak)
        print('       备份 SHA: %s  %s' % (
            bak_hash, '一致' if bak_hash == old_hash else '不一致!!'))
    else:
        print('[INFO] 目标位置尚无 exe，直接安装')

    # 复制新版本
    shutil.copy2(SRC, DST)
    final = sha256(DST)
    print()
    print('[ OK ] 已部署 -> %s' % DST)
    print('       大小 : %d 字节' % os.path.getsize(DST))
    print('       SHA  : %s  %s' % (final, '一致' if final == new_hash else '不一致!!'))
    print()
    print('=' * 72)
    print('  部署完成' if final == new_hash else '  部署失败：哈希不一致')
    print('=' * 72)
    return 0 if final == new_hash else 1


if __name__ == '__main__':
    sys.exit(main())
