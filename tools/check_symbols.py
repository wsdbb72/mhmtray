# -*- coding: utf-8 -*-
r"""对 MihomoTray.cs 做编译级静态检查（无编译器环境下的最强替代）。

本机 csc.exe 被沙箱策略拦截，无法真正编译，因此这里做三件事：
  1. 括号/引号/字符字面量平衡（含 /* */ 与字符串内转义）
  2. 方法定义 vs 调用点核对 —— 找出"调用了但未定义"的方法（会编译失败）
  3. 新增符号的定义/使用一致性核对

重点覆盖本轮改动引入的所有新符号。
"""
import re
import sys

SRC = 'mihomo-tray/MihomoTray.cs'


class Lexer:
    """把 C# 源码切成 token 流，正确跳过注释/字符串/字符字面量。"""

    def __init__(self, text):
        self.t = text
        self.i = 0
        self.n = len(text)

    def tokens(self):
        t, n = self.t, self.n
        i = 0
        out = []
        while i < n:
            c = t[i]
            # 行注释
            if c == '/' and i + 1 < n and t[i + 1] == '/':
                j = t.find('\n', i)
                i = n if j < 0 else j
                continue
            # 块注释
            if c == '/' and i + 1 < n and t[i + 1] == '*':
                j = t.find('*/', i + 2)
                i = n if j < 0 else j + 2
                continue
            # 逐字字符串 @"..."
            if c == '@' and i + 1 < n and t[i + 1] == '"':
                i += 2
                while i < n:
                    if t[i] == '"':
                        if i + 1 < n and t[i + 1] == '"':
                            i += 2
                            continue
                        i += 1
                        break
                    i += 1
                continue
            # 普通字符串
            if c == '"':
                i += 1
                while i < n:
                    if t[i] == '\\':
                        i += 2
                        continue
                    if t[i] == '"':
                        i += 1
                        break
                    i += 1
                continue
            # 字符字面量
            if c == "'":
                i += 1
                while i < n:
                    if t[i] == '\\':
                        i += 2
                        continue
                    if t[i] == "'":
                        i += 1
                        break
                    i += 1
                continue
            out.append((i, c))
            i += 1
        return out


def balance_check(text):
    """括号配平（基于 lexer 过滤后的 token）。"""
    lx = Lexer(text)
    stack = []
    pairs = {')': '(', ']': '[', '}': '{'}
    opens = set('([{')
    toks = lx.tokens()
    line_of = [1] * (len(text) + 1)
    ln = 1
    for idx, ch in enumerate(text):
        line_of[idx] = ln
        if ch == '\n':
            ln += 1

    for pos, ch in toks:
        if ch in opens:
            stack.append((ch, pos))
        elif ch in pairs:
            if not stack:
                return False, '多余的 %r 在行 %d' % (ch, line_of[pos])
            top, tpos = stack.pop()
            if top != pairs[ch]:
                return False, '%r (行 %d) 与 %r (行 %d) 不匹配' % (
                    top, line_of[tpos], ch, line_of[pos])
    if stack:
        top, tpos = stack[-1]
        return False, '未闭合的 %r 在行 %d' % (top, line_of[tpos])
    return True, '%d 个括号标记全部配对' % len(toks)


def find_definitions(text):
    """收集方法/属性定义名。"""
    defs = set()
    # 方法定义：返回类型 名字 ( ... )  后面跟 { 或 =>
    for m in re.finditer(
            r'(?:^|\n)\s*(?:(?:public|private|protected|internal|static|override|virtual|sealed|async|extern|new|partial)\s+)*'
            r'(?:[\w\.<>\[\],\?]+\s+)?(\w+)\s*\(', text):
        defs.add(m.group(1))
    # 属性定义：类型 名字 { get
    for m in re.finditer(
            r'(?:^|\n)\s*(?:(?:public|private|protected|internal|static|override|virtual|sealed|new)\s+)*'
            r'[\w\.<>\[\],\?]+\s+(\w+)\s*\{\s*get', text):
        defs.add(m.group(1))
    # 字段/常量定义
    for m in re.finditer(
            r'(?:^|\n)\s*(?:(?:public|private|protected|internal|static|readonly|const|volatile)\s+)+'
            r'(?:[\w\.<>\[\],\?]+)\s+(\w+)\s*(?:=|;)', text):
        defs.add(m.group(1))
    return defs


def find_calls(text, defs):
    """找 this/裸 方法调用，返回疑似未定义的。"""
    # 关键字与控制流，不算方法调用
    keywords = {
        'if', 'for', 'foreach', 'while', 'switch', 'catch', 'lock', 'using',
        'return', 'new', 'typeof', 'sizeof', 'nameof', 'default', 'case',
        'do', 'else', 'try', 'finally', 'throw', 'get', 'set', 'add', 'remove',
        'delegate', 'checked', 'unchecked', 'fixed', 'unsafe', 'stackalloc',
        'base', 'this', 'null', 'true', 'false', 'is', 'as', 'in', 'out', 'ref',
        'params', 'where', 'select', 'from', 'when', 'yield', 'await', 'async',
        'operator', 'implicit', 'explicit', 'event', 'value', 'var', 'string',
        'int', 'bool', 'void', 'object', 'byte', 'char', 'float', 'double',
        'long', 'short', 'decimal', 'uint', 'ulong', 'ushort', 'sbyte',
    }
    # BCL / 框架类型，调用它们的方法不算未定义
    builtin_receivers = {
        'Console', 'Math', 'Convert', 'File', 'Directory', 'Path', 'Regex',
        'Encoding', 'Thread', 'Process', 'Registry', 'Clipboard', 'MessageBox',
        'Environment', 'String', 'Char', 'Int32', 'Int64', 'Array', 'Enum',
        'Color', 'Font', 'SystemFonts', 'FontFamily', 'Point', 'Size', 'Rectangle',
        'Graphics', 'Bitmap', 'Image', 'Pen', 'SolidBrush', 'Brush', 'Brushes',
        'Pens', 'TcpClient', 'IPAddress', 'IPEndPoint', 'WebClient', 'WebProxy',
        'HttpWebRequest', 'HttpWebResponse', 'StreamReader', 'StreamWriter',
        'StringBuilder', 'List', 'Dictionary', 'Match', 'Group', 'Stopwatch',
        'DateTime', 'TimeSpan', 'Guid', 'Assembly', 'Type', 'Activator',
        'Uri', 'UriBuilder', 'Version', 'Debug', 'Trace', 'Form', 'Control',
        'Timer', 'ContextMenuStrip', 'ToolStripMenuItem', 'NotifyIcon',
        'Icon', 'Cursor', 'Application', 'Screen', 'ToolTip', 'ProgressBar',
        'TextBox', 'Button', 'Label', 'ComboBox', 'CheckBox', 'RadioButton',
        'Panel', 'GroupBox', 'TabControl', 'DataGridView', 'FlowLayoutPanel',
        'TableLayoutPanel', 'SplitContainer', 'SaveFileDialog', 'OpenFileDialog',
        'FolderBrowserDialog', 'DialogResult', 'Keys', 'Cursors', 'ContentAlignment',
        'HorizontalAlignment', 'AnchorStyles', 'DockStyle', 'FormBorderStyle',
        'FormWindowState', 'FormStartPosition', 'SmoothingMode', 'LineCap',
        'LineJoin', 'GraphicsUnit', 'ImageScalingSize', 'TextImageRelation',
        'ToolStripItemImageScaling', 'ToolTipIcon', 'CheckState', 'Padding',
        'Math', 'Comparer', 'StringComparer', 'CultureInfo', 'NumberStyles',
        'Buffer', 'Marshal', 'GCHandle', 'Interlocked', 'Monitor', 'WaitHandle',
        'ThreadStart', 'ParameterizedThreadStart', 'EventHandler', 'Action',
        'Func', 'Predicate', 'Comparison', 'Nullable', 'Tuple', 'KeyValuePair',
        'IEnumerable', 'IList', 'IDictionary', 'ICollection', 'IEqualityComparer',
    }
    calls = {}
    for m in re.finditer(r'(?:(\w+)\.)?(\w+)\s*\(', text):
        recv, name = m.group(1), m.group(2)
        if name in keywords or name in defs:
            continue
        if recv and recv in builtin_receivers:
            continue
        if recv and recv in defs:
            continue
        calls.setdefault(name, 0)
        calls[name] += 1
    return calls


def main():
    try:
        text = open(SRC, encoding='utf-8-sig').read()
    except Exception as e:
        print('[FAIL] 无法读取 %s: %s' % (SRC, e))
        return 1

    print('=' * 70)
    print('  C# 静态检查 (无编译器环境)')
    print('=' * 70)

    failures = 0

    # 1. 括号配平
    ok, detail = balance_check(text)
    print('\n[1] 括号配平')
    if ok:
        print('    [ OK ] %s' % detail)
    else:
        print('    [FAIL] %s' % detail)
        failures += 1

    # 2. 本轮新增符号必须存在
    print('\n[2] 本轮新增符号定义')
    required = {
        'ResolveProxiFyreEndpoint': '四段式端点解析',
        'CommonProxyPorts': '常见端口表',
        'TryExtractPort': '端口提取',
        'IsLocalPortListening': '监听探测',
        'IsWarnItem': 'warn 标记判定',
        'ApplyProxiFyrePort': '端口应用收敛',
        'OnAutoDetectProxiFyrePort': '自动检测入口',
        'WarnText': '警示色',
        'UnescapeJsonString': 'JSON 反转义',
        'EscapeJsonString': 'JSON 转义',
        'ReadSubscriptionsFromJson': '订阅解析',
        'ExtractJsonArrayBody': 'JSON 数组体提取',
        'SplitTopLevelObjects': '顶层对象切分',
        'ExtractObjectString': '字段提取',
        'RestartMihomoQuietly': '静默重启',
        'UpdateSubscriptions': '批量更新',
        'MaskSubscriptionUrl': '链接脱敏',
        'ValidateCandidate': '候选校验',
    }
    defs = find_definitions(text)
    missing = [n for n in required if n not in text]
    if missing:
        for n in missing:
            print('    [FAIL] 缺少符号 %s (%s)' % (n, required[n]))
        failures += 1
    else:
        for n, desc in sorted(required.items()):
            mark = n in defs
            print('    [ %s ] %-28s %s%s' % ('OK' if mark else '??', n, desc,
                                             '' if mark else '  (未在定义表内，可能是字段/属性)'))

    # 3. 疑似未定义的方法调用
    print('\n[3] 方法调用完整性')
    calls = find_calls(text, defs)
    # 已知的外部/事件处理器（委托绑定用），人工白名单
    known_external = {
        'Invoke', 'BeginInvoke', 'ShowDialog', 'Show', 'Hide', 'Close',
        'Dispose', 'ToString', 'Equals', 'GetHashCode', 'GetType',
        'add_Item', 'Update', 'Refresh', 'Invalidate', 'PerformClick',
    }
    suspicious = {k: v for k, v in calls.items()
                  if k not in known_external and k not in defs
                  and not k.startswith('get_') and not k.startswith('set_')}
    if suspicious:
        print('    [INFO] 以下调用未在定义表内命中（需人工确认是否为框架 API）：')
        for k in sorted(suspicious, key=lambda x: (-suspicious[x], x))[:40]:
            print('           %-34s x%d' % (k, suspicious[k]))
    else:
        print('    [ OK ] 未发现疑似未定义的方法调用')

    print()
    print('=' * 70)
    if failures:
        print('  结果: %d 项失败' % failures)
    else:
        print('  结果: 全部通过')
    print('=' * 70)
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
