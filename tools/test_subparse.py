# -*- coding: utf-8 -*-
"""
订阅解析对比测试。

背景：MihomoTray 的 LoadSubscriptions 原实现用单条正则解析
    {"subscriptions":[{...}]}
  其中 [^}]* 无法跨越 } ，且要求 name 必须出现在 url 之前，
  对含转义引号、含 } 的链接、字段顺序颠倒等情况会静默丢条目。
本脚本用 Python 等价实现新旧两版逻辑，验证新实现确实覆盖这些情形。

注意：本文件必须经 Write 工具写入，不要用 shell heredoc——
heredoc 会吞掉反斜杠，导致「转义」用例的输入被破坏而产生假失败。
"""

import re

# ── 新版逻辑（对应 MihomoTray.cs） ──────────────────────────────


def extract_array_body(js, key):
    k = '"%s"' % key
    ki = js.find(k)
    if ki < 0:
        return None
    i = js.find(':', ki + len(k))
    if i < 0:
        return None
    i += 1
    while i < len(js) and js[i].isspace():
        i += 1
    if i >= len(js) or js[i] != '[':
        return None
    start = i + 1
    depth = 1
    instr = esc = False
    i = start
    while i < len(js):
        c = js[i]
        if instr:
            if esc: esc = False
            elif c == '\\': esc = True
            elif c == '"': instr = False
            i += 1
            continue
        if c == '"':
            instr = True
            i += 1
            continue
        if c == '[':
            depth += 1
        elif c == ']':
            depth -= 1
            if depth == 0:
                return js[start:i]
        i += 1
    return None


def split_objects(body):
    res = []
    depth = 0
    start = -1
    instr = esc = False
    for i, c in enumerate(body):
        if instr:
            if esc: esc = False
            elif c == '\\': esc = True
            elif c == '"': instr = False
            continue
        if c == '"':
            instr = True
            continue
        if c == '{':
            if depth == 0:
                start = i
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0 and start >= 0:
                res.append(body[start:i + 1])
                start = -1
    return res


def unescape(s):
    """对应 C# 的 UnescapeJsonString：只处理 JSON 规范定义的转义。"""
    out = []
    i = 0
    simple = {'"': '"', '\\': '\\', '/': '/', 'b': '\b',
              'f': '\f', 'n': '\n', 'r': '\r', 't': '\t'}
    while i < len(s):
        c = s[i]
        if c != '\\' or i + 1 >= len(s):
            out.append(c)
            i += 1
            continue
        n = s[i + 1]
        if n in simple:
            out.append(simple[n])
            i += 2
        elif n == 'u' and i + 6 <= len(s):
            try:
                out.append(chr(int(s[i + 2:i + 6], 16)))
                i += 6
            except ValueError:
                out.append('u')
                i += 2
        else:
            out.append('\\')
            out.append(n)
            i += 2
    return ''.join(out)


def object_field(obj, name):
    """对应 C# 的 ExtractObjectString；无该字段返回 None。"""
    needle = '"%s"' % name
    idx = obj.find(needle)
    if idx < 0:
        return None
    i = obj.find(':', idx + len(needle))
    if i < 0:
        return None
    i += 1
    while i < len(obj) and obj[i].isspace():
        i += 1
    if i >= len(obj) or obj[i] != '"':
        return None
    i += 1
    buf = []
    esc = False
    while i < len(obj):
        c = obj[i]
        if esc:
            buf.append('\\')
            buf.append(c)
            esc = False
            i += 1
            continue
        if c == '\\':
            esc = True
            i += 1
            continue
        if c == '"':
            break
        buf.append(c)
        i += 1
    return unescape(''.join(buf))


def parse_new(js):
    body = extract_array_body(js, 'subscriptions')
    if body is None:
        return []
    out = []
    for o in split_objects(body):
        n = object_field(o, 'name')
        u = object_field(o, 'url')
        if n is None and u is None:
            continue
        out.append((n if n is not None else '', u if u is not None else ''))
    return out


# ── 旧版逻辑（正则） ───────────────────────────────────────────


def parse_old(js):
    m = re.search(r'"subscriptions"\s*:\s*\[(.*?)\]', js, re.S)
    if not m:
        return []
    return [(x.group(1), x.group(2)) for x in re.finditer(
        r'\{[^}]*"name"\s*:\s*"([^"]+)"[^}]*"url"\s*:\s*"([^"]+)"[^}]*\}',
        m.group(1))]


BS = chr(92)   # 反斜杠，避免源码里的转义歧义

CASES = [
    ("正常两条",
     '{"subscriptions":[{"name":"A","url":"https://a.com/1"},'
     '{"name":"B","url":"https://b.com/2"}]}',
     [("A", "https://a.com/1"), ("B", "https://b.com/2")]),

    ("url 在 name 之前",
     '{"subscriptions":[{"url":"https://a.com/1","name":"A"}]}',
     [("A", "https://a.com/1")]),

    ("链接含转义斜杠 (\\/)",
     '{"subscriptions":[{"name":"A","url":"https:' + BS + '/' + BS +
     '/a.com' + BS + '/s?token=t"}]}',
     [("A", "https://a.com/s?token=t")]),

    ("名称含转义引号",
     '{"subscriptions":[{"name":"A ' + BS + '"pro' + BS + '"","url":"https://a.com/1"}]}',
     [('A "pro"', "https://a.com/1")]),

    ("链接含花括号",
     '{"subscriptions":[{"name":"A","url":"https://a.com/{p}/1"}]}',
     [("A", "https://a.com/{p}/1")]),

    ("缺少 url 字段（保留条目）",
     '{"subscriptions":[{"name":"A"}]}',
     [("A", "")]),

    ("订阅数组为空",
     '{"subscriptions":[]}',
     []),

    ("数组后还有其他键",
     '{"subscriptions":[{"name":"A","url":"https://a.com/1"}],"proxiFyrePort":7897}',
     [("A", "https://a.com/1")]),

    ("名称含换行转义 (\\n)",
     '{"subscriptions":[{"name":"A' + BS + 'nX","url":"https://a.com/1"}]}',
     [("A\nX", "https://a.com/1")]),

    ("assetVersions 在 subscriptions 之前",
     '{"assetVersions":{"k":{"size":1}},'
     '"subscriptions":[{"name":"A","url":"https://a.com/1"}]}',
     [("A", "https://a.com/1")]),

    ("名称含反斜杠 d（旧 Regex.Unescape 会吃掉）",
     '{"subscriptions":[{"name":"A' + BS + BS + 'd+","url":"https://a.com/1"}]}',
     [('A' + BS + 'd+', "https://a.com/1")]),

    ("三条混合顺序",
     '{"subscriptions":[{"url":"https://c.com/3","name":"C"},'
     '{"name":"A","url":"https://a.com/1"},'
     '{"name":"B","url":"https://b.com/2"}]}',
     [("C", "https://c.com/3"), ("A", "https://a.com/1"),
      ("B", "https://b.com/2")]),
]


def main():
    new_fail = old_fail = 0
    for label, doc, want in CASES:
        got = parse_new(doc)
        old = parse_old(doc)
        ok = (got == want)
        if not ok:
            new_fail += 1
        if old != want:
            old_fail += 1
        print('%-46s 新:%-5s 原:%-5s' % (
            label, 'PASS' if ok else 'FAIL', 'PASS' if old == want else 'FAIL'))
        if not ok:
            print('     期望 %r' % (want,))
            print('     实际 %r' % (got,))

    print()
    print('新解析失败: %d / %d' % (new_fail, len(CASES)))
    print('原解析失败: %d / %d' % (old_fail, len(CASES)))
    return 1 if new_fail else 0


if __name__ == '__main__':
    raise SystemExit(main())
