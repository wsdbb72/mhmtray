# -*- coding: utf-8 -*-
r"""查询 GitHub Actions 运行状态与产物，复用 git 凭据（无需额外登录）。

用法:
  python tools/ci_status.py                # 列出最近 5 次运行
  python tools/ci_status.py --wait         # 等待最新一次 Windows 构建完成
  python tools/ci_status.py --artifacts N  # 列出某次运行的产物
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.request

REPO = 'wsdbb72/mhmtray'


def token():
    p = subprocess.run(['git', 'credential', 'fill'],
                       input='protocol=https\nhost=github.com\n\n',
                       capture_output=True, text=True)
    for line in p.stdout.splitlines():
        if line.startswith('password='):
            return line.split('=', 1)[1].strip()
    return None


def api(path, tok, raw=False):
    """调用 GitHub API。

    注意：本机经代理上网，环境变量里的 http(s)_proxy 指向不可达的 127.0.0.1:6253，
    而 git 自己配置的是可用的 127.0.0.1:7897。因此这里显式指定代理，
    避免 Python 读到错误的环境变量后连接被拒。
    """
    url = 'https://api.github.com' + path
    req = urllib.request.Request(url, headers={
        'Authorization': 'Bearer ' + tok,
        'Accept': 'application/vnd.github+json',
        'User-Agent': 'mhmtray-ci-status/1.0',
    })
    proxy = os.environ.get('MHMTRAY_PROXY', 'http://127.0.0.1:7897')
    opener = urllib.request.build_opener(
        urllib.request.ProxyHandler({'http': proxy, 'https': proxy}))
    with opener.open(req, timeout=60) as r:
        data = r.read()
    return data if raw else json.loads(data)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--wait', action='store_true')
    ap.add_argument('--artifacts', type=int, default=None)
    ap.add_argument('--limit', type=int, default=5)
    args = ap.parse_args()

    tok = token()
    if not tok:
        print('无法从 git 凭据取得 token')
        sys.exit(1)

    runs = api('/repos/%s/actions/runs?per_page=%d' % (REPO, args.limit), tok)
    items = runs.get('workflow_runs') or []

    print('=' * 78)
    print('  %s · 最近 %d 次运行' % (REPO, len(items)))
    print('=' * 78)
    for r in items:
        print('  #%-10s %-22s %-12s %s' % (
            r['run_number'], r['name'], r['status'],
            (r.get('conclusion') or '-')[:12]))
        print('      %s' % r['head_commit']['message'].splitlines()[0][:70])
        print('      %s  %s' % (r['head_sha'][:8], r['created_at']))
    print()

    if args.artifacts is not None:
        # --artifacts 接受"运行编号"(run_number)，需先映射为内部 run_id
        target = None
        for r in items:
            if r['run_number'] == args.artifacts:
                target = r
                break
        if not target:
            print('未在最近 %d 次运行中找到 #%d（可加大 --limit）'
                  % (len(items), args.artifacts))
            sys.exit(1)
        print('--- 运行 #%d (%s) 的产物 ---' % (args.artifacts, target['name']))
        print('    run_id=%d  sha=%s' % (target['id'], target['head_sha'][:8]))
        arts = api('/repos/%s/actions/runs/%d/artifacts' % (REPO, target['id']), tok)
        for a in arts.get('artifacts') or []:
            print('  %-26s %9d 字节  expired=%s' % (a['name'], a['size_in_bytes'], a['expired']))
            print('      digest: %s' % (a.get('digest') or '-'))
            print('      id    : %s' % a['id'])
            print('      url   : %s' % a['archive_download_url'])
        print()

    if args.wait:
        # 找最新一次 Windows 构建
        target = None
        for r in items:
            if 'Windows' in (r['name'] or ''):
                target = r
                break
        if not target:
            print('未找到 Windows 构建运行')
            sys.exit(1)

        print('--- 等待运行 #%d (%s) ---' % (target['run_number'], target['name']))
        run_id = target['id']
        for _ in range(120):  # 最多等 20 分钟
            r = api('/repos/%s/actions/runs/%d' % (REPO, run_id), tok)
            st = r['status']
            cc = r.get('conclusion')
            print('  status=%-12s conclusion=%s' % (st, cc or '-'))
            if st == 'completed':
                print()
                print('  结论:', cc)
                if cc == 'success':
                    arts = api('/repos/%s/actions/runs/%d/artifacts' % (REPO, run_id), tok)
                    for a in arts.get('artifacts') or []:
                        print('  产物: %s  %d 字节  id=%s' % (
                            a['name'], a['size_in_bytes'], a['id']))
                        print('        digest=%s' % (a.get('digest') or '-'))
                sys.exit(0 if cc == 'success' else 1)
            time.sleep(10)
        print('等待超时')
        sys.exit(2)


if __name__ == '__main__':
    main()
