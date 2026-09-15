# -*- coding: utf-8 -*-
r"""验证 ResolveProxiFyreEndpoint() 的新优先级逻辑（纯逻辑复刻，不依赖 .NET）。

覆盖场景：
  1. 用户显式指定 -> 直接用，不看监听状态
  2. ProxiFyre 配置里的端点仍在监听 -> 沿用
  3. ProxiFyre 配置里的端点已死 -> 回退到推断端口（若在监听）
  4. 推断端口也没监听 -> 兜底扫常见端口
  5. 全都没监听 -> 返回推断值（让探测器报错，不静默指向 7890）

ReadSocksPort 的优先级：socks-port(非0) > mixed-port(非0) > port(非0) > 7890
"""
import re

SOCKS_PORT = re.compile(r'(?m)^socks-port:\s*(\d+)')
MIXED_PORT = re.compile(r'(?m)^mixed-port:\s*(\d+)')
HTTP_PORT = re.compile(r'(?m)^port:\s*(\d+)')
COMMON_PROXY_PORTS = [7897, 7890, 7891, 7893, 7899, 1080, 10808, 2080]


def read_socks_port(content):
    if content:
        for rx in (SOCKS_PORT, MIXED_PORT, HTTP_PORT):
            m = rx.search(content)
            if m:
                port = int(m.group(1))
                if 0 < port <= 65535:      # 0 == 禁用，跳过
                    return port
    return 7890


def try_extract_port(endpoint):
    if not endpoint:
        return None
    idx = endpoint.rfind(':')
    if idx < 0 or idx + 1 >= len(endpoint):
        return None
    try:
        p = int(endpoint[idx + 1:].strip())
    except ValueError:
        return None
    return p if 0 < p <= 65535 else None


def resolve_endpoint(override, existing_endpoint, config_content, listening_ports):
    """listening_ports: 模拟 IsLocalPortListening 的集合。"""
    listening = set(listening_ports)

    if override and 0 < override <= 65535:
        return '127.0.0.1:%d' % override, 'override'

    ep = try_extract_port(existing_endpoint)
    if existing_endpoint and ep and ep in listening:
        return existing_endpoint, 'existing(listening)'

    inferred = read_socks_port(config_content)
    if inferred in listening:
        return '127.0.0.1:%d' % inferred, 'inferred(listening)'

    for cand in COMMON_PROXY_PORTS:
        if cand == inferred:
            continue
        if cand in listening:
            return '127.0.0.1:%d' % cand, 'common-scan'

    return '127.0.0.1:%d' % inferred, 'fallback(inferred, not listening)'


MIAO = """
port: 7890
socks-port: 7891
mixed-port: 7893
"""

SOCKS_DISABLED = """
port: 7890
socks-port: 0
mixed-port: 7893
"""

CASES = [
    # (说明, override, existing, config, listening, 期望端点, 期望路径)
    ('用户显式指定优先，即使没人监听',
     7897, None, MIAO, [], '127.0.0.1:7897', 'override'),

    ('显式指定覆盖一切',
     1080, '127.0.0.1:7897', MIAO, [7897], '127.0.0.1:1080', 'override'),

    ('沿用 ProxiFyre 已写且仍在监听的端点',
     None, '127.0.0.1:7897', MIAO, [7897], '127.0.0.1:7897', 'existing(listening)'),

    ('ProxiFyre 端点已死 -> 用推断端口（在监听）',
     None, '127.0.0.1:9999', MIAO, [7891], '127.0.0.1:7891', 'inferred(listening)'),

    ('ProxiFyre 端点已死 + 推断端口也死 -> 兜底扫到 7897',
     None, '127.0.0.1:9999', MIAO, [7897], '127.0.0.1:7897', 'common-scan'),

    ('无 existing、推断端口在监听',
     None, None, MIAO, [7891], '127.0.0.1:7891', 'inferred(listening)'),

    ('socks-port: 0 应跳过，回退到 mixed-port 7893',
     None, None, SOCKS_DISABLED, [7893], '127.0.0.1:7893', 'inferred(listening)'),

    ('全都没监听 -> 返回推断值，不静默改成 7890',
     None, None, MIAO, [], '127.0.0.1:7891', 'fallback(inferred, not listening)'),

    ('existing 是 host:port 且刚好端口在监听 -> 原样沿用',
     None, '127.0.0.1:7893', MIAO, [7893], '127.0.0.1:7893', 'existing(listening)'),

    ('existing 格式非法 -> 走推断',
     None, 'garbage', MIAO, [7891], '127.0.0.1:7891', 'inferred(listening)'),
]


def main():
    failed = 0
    for i, (desc, ovr, exist, cfg, listen, want_ep, want_path) in enumerate(CASES, 1):
        got_ep, got_path = resolve_endpoint(ovr, exist, cfg, listen)
        ok = (got_ep == want_ep and got_path == want_path)
        if not ok:
            failed += 1
        print('%s %2d. %s' % ('[ OK ]' if ok else '[FAIL]', i, desc))
        if not ok:
            print('         期望 %s (%s)' % (want_ep, want_path))
            print('         实际 %s (%s)' % (got_ep, got_path))

    print()
    print('失败: %d / %d' % (failed, len(CASES)))
    return 1 if failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
