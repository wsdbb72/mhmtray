# -*- coding: utf-8 -*-
"""下载指定 CI artifact 并校验 SHA-256。

关键点（踩过的坑）：
  - artifact 会 302 跳到 Azure Blob / objects.githubusercontent.com；
  - 跨域跳转时必须**完全移除 Authorization 头**，否则 Azure 会把 GitHub token
    当成自己的凭据校验，返回 401 InvalidAuthenticationInfo；
  - 目标 URL 自带 SAS 签名，本身就授权了读取。
"""
import hashlib
import io
import json
import os
import subprocess
import sys
import urllib.request
import zipfile

PROXY = os.environ.get('MHMTRAY_PROXY', 'http://127.0.0.1:7897')
REPO = 'wsdbb72/mhmtray'


def get_token():
    p = subprocess.run(['git', 'credential', 'fill'],
                       input='protocol=https\nhost=github.com\n\n',
                       capture_output=True, text=True)
    for line in p.stdout.splitlines():
        if line.startswith('password='):
            return line.split('=', 1)[1].strip()
    return None


class NoRedirectOnCrossOrigin(urllib.request.HTTPRedirectHandler):
    """跨域跳转时丢掉 Authorization，避免 Azure 误判。"""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is None:
            return None
        same_host = urllib.parse.urlparse(req.full_url).netloc == \
            urllib.parse.urlparse(newurl).netloc
        if not same_host:
            new.headers.pop('Authorization', None)
            new.headers.pop('authorization', None)
            new.add_header('User-Agent', 'mhmtray-ci-fetch/1.0')
        return new


def open_with_proxy(url, headers=None):
    proxy_handler = urllib.request.ProxyHandler({'https': PROXY, 'http': PROXY})
    opener = urllib.request.build_opener(proxy_handler, NoRedirectOnCrossOrigin())
    req = urllib.request.Request(url, headers=headers or {})
    return opener.open(req, timeout=300)


def main():
    if len(sys.argv) < 3:
        print('用法: fetch_ci_artifact.py <artifact_id> <expected_sha256|->')
        return 2

    art_id = sys.argv[1]
    expected = sys.argv[2]
    out = os.path.join('artifacts', 'ci', 'MihomoTray-Windows-%s.zip' % art_id)

    token = get_token()
    if not token:
        print('[FAIL] 未能取得 token')
        return 1
    print('[ OK ] 已取得 token（长度 %d，不回显）' % len(token))

    os.makedirs(os.path.dirname(out), exist_ok=True)
    url = 'https://api.github.com/repos/%s/actions/artifacts/%s/zip' % (REPO, art_id)

    print('[..] 下载中：%s' % url)
    try:
        resp = open_with_proxy(url, {
            'Authorization': 'Bearer ' + token,
            'Accept': 'application/vnd.github+json',
            'User-Agent': 'mhmtray-ci-fetch/1.0',
        })
        data = resp.read()
    except Exception as e:
        print('[FAIL] 下载失败：%s: %s' % (type(e).__name__, e))
        return 1

    print('[ OK ] 下载完成：%d 字节' % len(data))

    digest = hashlib.sha256(data).hexdigest()
    print('[..] SHA-256: %s' % digest)
    if expected and expected != '-':
        exp = expected.replace('sha256:', '')
        if digest == exp:
            print('[ OK ] SHA-256 与 CI 声明一致')
        else:
            print('[FAIL] SHA-256 不匹配，期望 %s' % exp)
            return 1

    with open(out, 'wb') as f:
        f.write(data)
    print('[ OK ] 已保存：%s' % out)

    # 列出 zip 内容并解出 exe
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        names = z.namelist()
        print('[..] 压缩包内含 %d 个条目:' % len(names))
        for n in names:
            print('       %-40s %d 字节' % (n, z.getinfo(n).file_size))
        exe = [n for n in names if n.lower().endswith('.exe')]
        if exe:
            target = os.path.join('artifacts', 'ci', 'MihomoTray.exe')
            with z.open(exe[0]) as src, open(target, 'wb') as dst:
                dst.write(src.read())
            exe_digest = hashlib.sha256(open(target, 'rb').read()).hexdigest()
            print('[ OK ] 已解出 exe：%s' % target)
            print('       exe SHA-256: %s' % exe_digest)
            print('       exe 大小  : %d 字节' % os.path.getsize(target))
    return 0


if __name__ == '__main__':
    import urllib.parse  # noqa: E402  (redirect_request 里用到)
    sys.exit(main())
