# -*- coding: utf-8 -*-
r"""复现 C# MihomoTray.ReadSocksPort() 的端口探测优先级，用真实的 .NET 语义。

C# 源码（verbatim string，\s 是真正的正则转义）：
    static readonly Regex HttpPortRegex  = new Regex(@"(?m)^port:\s*(\d+)");
    static readonly Regex MixedPortRegex = new Regex(@"(?m)^mixed-port:\s*(\d+)");
    static readonly Regex SocksPortRegex = new Regex(@"(?m)^socks-port:\s*(\d+)");

    int ReadSocksPort() {
        string content = ReadActiveConfigContent();
        if (!string.IsNullOrEmpty(content)) {
            foreach (var re in new[] { SocksPortRegex, MixedPortRegex, HttpPortRegex })
                { var m = re.Match(content);
                  if (m.Success) { int port;
                      if (int.TryParse(m.Groups[1].Value, out port) && port > 0 && port <= 65535)
                          return port; } } }
        return 7890;
    }

注意：`socks-port: 0` 是 mihomo 的“禁用该出入口”写法（0 表示关闭），
但这里的 `port > 0` 判断会把 0 跳过并继续回退到 mixed-port —— 这正是期望行为。
"""
import re
import sys

HTTP_PORT = re.compile(r'(?m)^port:\s*(\d+)')
MIXED_PORT = re.compile(r'(?m)^mixed-port:\s*(\d+)')
SOCKS_PORT = re.compile(r'(?m)^socks-port:\s*(\d+)')


def read_socks_port(content):
    """完整复刻 C# 逻辑，含 `port > 0` 与上限校验。"""
    if not content:
        return 7890
    for rx in (SOCKS_PORT, MIXED_PORT, HTTP_PORT):
        m = rx.search(content)
        if m:
            port = int(m.group(1))
            if 0 < port <= 65535:
                return port
    return 7890


def report(path):
    raw = open(path, 'rb').read()
    # .NET File.ReadAllText(Encoding.UTF8) 会剥离 BOM
    content = raw.decode('utf-8-sig')

    print('=' * 68)
    print('配置文件:', path)
    print('-' * 68)
    for label, rx in (('socks-port', SOCKS_PORT),
                      ('mixed-port', MIXED_PORT),
                      ('port      ', HTTP_PORT)):
        m = rx.search(content)
        print('  %s = %s' % (label, m.group(1) if m else '(未找到)'))

    hit = None
    for label, rx in (('socks-port', SOCKS_PORT),
                      ('mixed-port', MIXED_PORT),
                      ('port', HTTP_PORT)):
        m = rx.search(content)
        if m and 0 < int(m.group(1)) <= 65535:
            hit = (label, int(m.group(1)))
            break

    print('-' * 68)
    if hit:
        print('  ReadSocksPort() 返回 -> %d   (命中 %s)' % (hit[1], hit[0]))
    else:
        print('  ReadSocksPort() 返回 -> 7890 (全部无效，回退默认值)')
    return hit[1] if hit else 7890


if __name__ == '__main__':
    files = sys.argv[1:]
    if not files:
        print('用法: probe_port.py <yaml> [yaml...]')
        sys.exit(1)
    for f in files:
        report(f)
