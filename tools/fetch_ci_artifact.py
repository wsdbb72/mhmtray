# -*- coding: utf-8 -*-
"""用 git 凭据管理器里的 token 下载 CI artifact。token 不回显。"""
import json
import os
import subprocess
import sys
import urllib.request

PROXY = 'http://127.0.0.1:7897'
ART_URL = ('https://api.github.com/repos/wsdbb72/mhmtray/actions/'
           'artifacts/10404745304/zip')   # 0cea02e 的 MihomoTray-Windows
OUT = 'artifacts/ci/MihomoTray-Windows.zip'


def get_token():
    p = subprocess.run(['git', 'credential', 'fill'],
                       input='protocol=https\nhost=github.com\n\n',
                       capture_output=True, text=True)
    for line in p.stdout.splitlines():
        if line.startswith('password='):
            return line.split('=', 1)[1].strip()
    return None


def main():
    token = get_token()
    if not token:
        print('[FAIL] 未能取得 token')
        return 1
    print('[ OK ] 已取得 token（长度 %d，不回显）' % len(token))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)

    # 走代理
    proxy_handler = urllib.request.ProxyHandler({'https': PROXY, 'http': PROXY})
    opener = urllib.request.build_opener(proxy_handler)
    opener.addheaders = [
        ('Authorization', 'Bearer ' + token),
        ('Accept', 'application/vnd.github+json'),
        ('User-Agent', 'mhmtray-ci-fetch'),
    ]

    print('[..] 下载中：%s' % ART_URL)
    try:
        resp = opener.open(ART_URL, timeout=180)
        # artifact 会 302 到 objects.githubusercontent.com
        data = resp.read()
    except Exception as e:
        print('[FAIL] 下载失败：%s: %s' % (type(e).__name__, e))
        return 1

    with open(OUT, 'wb') as f:
        f.write(data)

    print('[ OK ] 已保存 %s (%d bytes)' % (OUT, len(data)))
    if data[:2] != b'PK':
        print('[WARN] 前两字节不是 PK，可能仍是错误 JSON：')
        print(data[:300].decode('utf-8', 'ignore'))
        return 1
    print('[ OK ] 是合法 zip（PK 魔数校验通过）')
    return 0


if __name__ == '__main__':
    sys.exit(main())
