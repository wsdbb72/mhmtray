# -*- coding: utf-8 -*-
r"""
check_lang_features.py —— 防止用到旧编译器不支持的 C# 语法。

背景（真实教训）：
  本项目用 build.bat 调 .NET Framework 4.x 自带的旧编译器构建，
  语言级别是 C# 5。我写了 `out var addr`（C# 7.0 内联 out 声明），
  本地所有脚本检查（括号平衡、符号、结构）全部通过，
  但 CI 编译直接失败：
      error CS1026: ) expected
      error CS1002: ; expected
      error CS1525: Invalid expression term ')'

  这类问题本地无法用编译器发现，只能靠 CI 一轮一轮试——代价太高。
  因此把它固化成静态检查：在推送前就拦住。

本脚本扫描出 C# 6.0 及以后才引入的语法。
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FILES = [
    os.path.join(ROOT, 'mihomo-tray', 'MihomoTray.cs'),
    os.path.join(ROOT, 'mihomo-tray', 'EmbeddedIcons.cs'),
    os.path.join(ROOT, 'mihomo-tray', 'AssemblyInfo.cs'),
]

# (名称, 正则, 引入版本, 说明, 修复建议)
RULES = [
    ('内联 out 变量',
     re.compile(r'\bout\s+var\s+\w+'),
     'C# 7.0',
     'out var x 内联声明',
     '改为先声明：Type x; Foo(out x);'),

    ('out 变量内联声明（调用处）',
     # C# 7.0 的 "out Type x" 只在**调用处**才非法；方法签名里的
     # "bool M(out Foo x)" 是 C# 1.0 就有的合法写法，不能误报。
     #
     # 区分办法：签名以 ')' 或 '{' 结尾（后面接方法体），
     # 调用则以 ';' 结尾或嵌在表达式里。这里只认那些**不以方法体开始的**行。
     re.compile(r'(?<![\w\.])out\s+[A-Z][\w\.<>\[\]]*\s+\w+\s*[,\)](?![^;{]*\{)(?=[^;{]*;)'),
     'C# 7.0',
     '调用处内联声明 out 参数',
     '改为先声明再传出'),

    ('元组字面量',
     re.compile(r'\(\s*\w+\s*,\s*\w+\s*\)\s*=\s*'),
     'C# 7.0',
     '元组解构赋值',
     '用普通临时变量或 out 参数替代'),

    ('ValueTuple 类型',
     re.compile(r'\bValueTuple\s*<'),
     'C# 7.0',
     'ValueTuple 需要额外包',
     '避免使用'),

    ('模式匹配 is 类型模式',
     re.compile(r'\bis\s+[A-Z][\w\.<>]*\s+\w+\s*(?:&&|\|\||\)|;|\{)'),
     'C# 7.0',
     'if (x is Foo f) 模式匹配',
     '改为 (x as Foo) != null + 取变量'),

    ('switch 表达式',
     re.compile(r'=>\s*[^;{]*\bswitch\s*\{'),
     'C# 8.0',
     'switch 表达式',
     '改为传统 switch 语句'),

    ('null 合并赋值',
     re.compile(r'\?\?='),
     'C# 8.0',
     '??= 运算符',
     '改为 if (x == null) x = y;'),

    ('using 声明（无大括号）',
     re.compile(r'^\s*using\s+var\s+\w+\s*=\s*[^;]*;\s*$', re.M),
     'C# 8.0',
     'using 声明',
     '改为 using (...) { ... } 块'),

    ('字符串插值',
     # 只匹配真正的插值字符串起始标记：$" 或 $@" 或 @$"
     # 不能简单用 \$" —— 正则字面量里形如 @"...$") 的收尾美元符会被误判。
     # 要求 $ 后面紧跟引号，且 $ 之前不是引号/标识符字符。
     re.compile(r'(?<!["\w])\$(?:@")|(?<!["\w])@\$"'),
     'C# 6.0',
     '$"..." 插值字符串',
     '改为 string.Format 或 + 拼接'),

    ('nameof 运算符',
     re.compile(r'\bnameof\s*\('),
     'C# 6.0',
     'nameof',
     '改为字面量字符串'),

    ('表达式体成员',
     re.compile(r'^\s*(?:public|private|protected|internal|static)[^\n{;]*=>\s', re.M),
     'C# 6.0',
     '=> 表达式体方法/属性',
     '改为 { return ...; }'),

    ('自动属性初始化器',
     re.compile(r'^\s*(?:public|private|protected|internal)\s+[\w\.<>\[\]]+\s+\w+\s*\{\s*get;\s*set;\s*\}\s*=',
                re.M),
     'C# 6.0',
     '属性初始化器',
     '改为在构造函数里赋值'),

    ('catch 异常过滤器 when',
     re.compile(r'\bcatch\s*\([^)]*\)\s*when\s*\('),
     'C# 6.0',
     'when 过滤器',
     '改为 catch 内 if 判断并 rethrow'),

    ('静态 using',
     re.compile(r'^\s*using\s+static\s', re.M),
     'C# 6.0',
     'using static',
     '改为普通 using + 类名前缀'),

    ('局部函数',
     re.compile(r'^\s{8,}(?:void|int|bool|string|var)\s+\w+\s*\([^;]*\)\s*$\n\s*\{', re.M),
     'C# 7.0',
     '方法内定义函数',
     '改为类级私有方法或匿名委托'),

    ('数字分隔符',
     re.compile(r'\b\d+_\d+\b'),
     'C# 7.0',
     '1_000 数字分隔符',
     '去掉下划线'),

    ('二进制字面量',
     re.compile(r'\b0b[01]+\b'),
     'C# 7.0',
     '0b1010 二进制字面量',
     '改为十六进制或十进制'),

    ('局部引用 ref 局部变量',
     re.compile(r'\bref\s+var\s+\w+|\bref\s+[A-Z][\w\.<>]*\s+\w+\s*='),
     'C# 7.0',
     'ref 局部变量',
     '避免使用'),

    ('只读结构 / ref struct',
     re.compile(r'\bref\s+struct\b|\breadonly\s+struct\b'),
     'C# 7.2',
     'ref struct / readonly struct',
     '避免使用'),

    ('可空引用类型标注',
     re.compile(r'#nullable\b|\bstring\?\s+\w+'),
     'C# 8.0',
     '可空引用类型',
     '去掉标注'),

    ('索引/范围运算符',
     re.compile(r'\w\[\s*\^[\d\w]|\w\[\s*[\d\w]+\s*\.\.'),
     'C# 8.0',
     '^ 与 .. 运算',
     '改为常规索引'),

    ('全局 using',
     re.compile(r'^\s*global\s+using\s', re.M),
     'C# 10.0',
     'global using',
     '改为每文件 using'),
]

print('=' * 74)
print('  C# 语言级别兼容性检查（目标：C# 5，.NET Framework 4.x 旧编译器）')
print('=' * 74)
print()

total = 0
for path in FILES:
    if not os.path.isfile(path):
        print('[SKIP] %s 不存在' % os.path.basename(path))
        continue

    src = io.open(path, encoding='utf-8-sig').read()
    lines = src.splitlines()

    # 剥掉注释，避免把说明性文字当成代码
    def strip_comments(text):
        out = []
        i = 0
        n = len(text)
        while i < n:
            c = text[i]
            if c == '/' and i + 1 < n and text[i + 1] == '/':
                j = text.find('\n', i)
                i = n if j < 0 else j
                continue
            if c == '/' and i + 1 < n and text[i + 1] == '*':
                j = text.find('*/', i + 2)
                i = n if j < 0 else j + 2
                continue
            if c == '"':
                out.append(c)
                i += 1
                while i < n:
                    if text[i] == '\\':
                        out.append('  '); i += 2; continue
                    out.append(text[i])
                    if text[i] == '"':
                        i += 1
                        break
                    i += 1
                continue
            out.append(c)
            i += 1
        return ''.join(out)

    # 保持行号：把注释替换成等量空白
    stripped_lines = strip_comments(src).splitlines()
    # 补齐行数（strip 后行数应一致）
    while len(stripped_lines) < len(lines):
        stripped_lines.append('')

    print('扫描 %s (%d 行)' % (os.path.basename(path), len(lines)))
    hits = []
    for rname, rx, ver, desc, fix in RULES:
        for idx, line in enumerate(stripped_lines):
            for m in rx.finditer(line):
                hits.append((idx + 1, rname, ver, m.group(0).strip(), fix))

    if not hits:
        print('  [ OK ] 未发现高于 C# 5 的语法')
    else:
        total += len(hits)
        print('  [FAIL] 发现 %d 处可能不被旧编译器支持的语法：' % len(hits))
        seen = set()
        for ln, rname, ver, snippet, fix in sorted(hits):
            key = (ln, rname)
            if key in seen:
                continue
            seen.add(key)
            print('      line %-5d %-22s (%s)' % (ln, rname, ver))
            print('        %s' % snippet[:96])
            print('        -> %s' % fix)
    print()

print('=' * 74)
if total == 0:
    print('  结果: PASS —— 全部文件兼容 C# 5')
    print('=' * 74)
    sys.exit(0)
print('  结果: FAIL —— 共 %d 处高版本语法' % total)
print('=' * 74)
sys.exit(1)
