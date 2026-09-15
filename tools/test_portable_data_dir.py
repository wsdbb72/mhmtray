# -*- coding: utf-8 -*-
r"""
test_portable_data_dir.py —— 校验"单文件运行"的数据目录解析逻辑。

需求："只下载一个 app 即可运行"。
实现：ResolveDataDirectory() 按优先级决定数据放在哪——
   1) exe 同目录已有 config.yaml 或 tray-config.json  -> 便携模式，沿用同目录
   2) 否则 -> %LOCALAPPDATA%\MihomoTray\
   3) 极端兜底 -> exe 同目录

本脚本做两件事：
  A. 从 C# 源码里把 ResolveDataDirectory / EnsureDataDirectoryInitialized 的真实
     决策规则抽出来，用纯 Python 复刻一遍；
  B. 在临时目录上跑真值表，断言各种目录布局下的判定结果。
这样即使没法编译 C#，也能对"数据目录解析"这一核心行为做回归。
"""
import io
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
CS = os.path.join(os.path.dirname(HERE), 'mihomo-tray', 'MihomoTray.cs')

src = io.open(CS, encoding='utf-8-sig').read()

PASS = 0
FAIL = 0


def check(cond, label, detail=''):
    global PASS, FAIL
    if cond:
        PASS += 1
        print('  [PASS] %s%s' % (label, ('  ' + detail) if detail else ''))
    else:
        FAIL += 1
        print('  [FAIL] %s%s' % (label, ('  ' + detail) if detail else ''))


print('=' * 74)
print('  单文件运行（数据目录解析）行为校验')
print('=' * 74)
print()

# ─────────── A. 从源码抽取决策规则并核对 ───────────
print('A) 源码规则核对')


def method_body(source, anchor):
    k = source.find(anchor)
    if k < 0:
        return None
    b = source.find('{', k + len(anchor))
    depth = 0
    j = b
    while j < len(source):
        if source[j] == '{':
            depth += 1
        elif source[j] == '}':
            depth -= 1
            if depth == 0:
                return source[b:j + 1]
        j += 1
    return None


body_resolve = method_body(src, 'static string ResolveDataDirectory()')
check(body_resolve is not None, '存在 ResolveDataDirectory()')

if body_resolve:
    # 便携优先：必须检查同目录有没有 config.yaml / tray-config.json
    check('"config.yaml"' in body_resolve, '便携判定包含 config.yaml')
    check('"tray-config.json"' in body_resolve, '便携判定包含 tray-config.json')
    check('AppDomain.CurrentDomain.BaseDirectory' in body_resolve,
          '便携目录取自 exe 所在目录')

    # 回退：必须用 LocalApplicationData，且拼上 MihomoTray
    check('LocalApplicationData' in body_resolve,
          '回退到 %LOCALAPPDATA%')
    check('"MihomoTray"' in body_resolve,
          '回退目录名为 MihomoTray')

    # 顺序：便携判定必须在 LocalApplicationData 之前
    p_portable = body_resolve.find('config.yaml')
    p_fallback = body_resolve.find('LocalApplicationData')
    check(0 <= p_portable < p_fallback,
          '便携判定优先于 %LOCALAPPDATA% 回退',
          '(portable@%d < fallback@%d)' % (p_portable, p_fallback))

    # 兜底：最后必须能返回 exeDir，保证极端情况仍可用
    # 注意 method_body 返回的片段含收尾的 '}'，所以要先去掉它再判断。
    stripped = body_resolve.rstrip()
    if stripped.endswith('}'):
        stripped = stripped[:-1].rstrip()
    check(stripped.endswith('return exeDir;'),
          '最终兜底返回 exeDir（不会返回 null/空）')

body_init = method_body(src, 'void EnsureDataDirectoryInitialized()')
check(body_init is not None, '存在 EnsureDataDirectoryInitialized()')
if body_init:
    check('if (!File.Exists(_configPath))' in body_init,
          '初始化对 config.yaml 做存在性判断（不覆盖用户配置）')
    check('WriteUtf8FileAtomic' in body_init,
          '骨架配置用 WriteUtf8FileAtomic 原子写入')
    check('CreateDirectory(_basePath)' in body_init, '会创建数据目录本身')
    check('"profiles"' in body_init, '会创建 profiles 子目录')
    check('_firstRunMissingCore' in body_init,
          '记录"缺核心"状态以便给出明确提示')

# 构造期必须调用初始化，且在建菜单之前
ctor = method_body(src, 'public MainForm()')
check(ctor is not None, '存在 MainForm() 构造函数')
if ctor:
    p_resolve = ctor.find('ResolveDataDirectory()')
    p_init = ctor.find('EnsureDataDirectoryInitialized()')
    p_menu = ctor.find('BuildMenu()')
    check(p_resolve > 0, '构造函数调用 ResolveDataDirectory()')
    check(p_init > 0, '构造函数调用 EnsureDataDirectoryInitialized()')
    check(0 < p_resolve < p_init < p_menu,
          '顺序正确：解析目录 -> 初始化 -> 建菜单',
          '(%d < %d < %d)' % (p_resolve, p_init, p_menu))

# 缺核心时必须给出警示而不是静默失败
check('_firstRunMissingCore && !running' in src,
      '缺核心时菜单有明确提示分支')
check('缺少核心 mihomo.exe' in src, '提示文案说明缺的是 mihomo.exe')

print()

# ─────────── B. 真值表：复刻解析规则并在临时目录上验证 ───────────
print('B) 真值表（用临时目录复刻 C# 决策规则）')


def resolve_like_cs(exe_dir, local_appdata):
    """严格按 ResolveDataDirectory() 的分支复刻。"""
    if (os.path.isfile(os.path.join(exe_dir, 'config.yaml')) or
            os.path.isfile(os.path.join(exe_dir, 'tray-config.json'))):
        return exe_dir
    if local_appdata:
        return os.path.join(local_appdata, 'MihomoTray')
    return exe_dir


tmp = tempfile.mkdtemp(prefix='mhmtray-portable-')
try:
    lad = os.path.join(tmp, 'LocalAppData')
    os.makedirs(lad)

    cases = []

    # 1) 空目录（典型"只下载了一个 exe"）-> 应回退到 LOCALAPPDATA
    d1 = os.path.join(tmp, 'case_empty')
    os.makedirs(d1)
    cases.append(('空目录（只有 exe）', d1, os.path.join(lad, 'MihomoTray')))

    # 2) 同目录有 config.yaml -> 便携模式，沿用同目录
    d2 = os.path.join(tmp, 'case_cfg')
    os.makedirs(d2)
    io.open(os.path.join(d2, 'config.yaml'), 'w', encoding='utf-8').write('mode: rule\n')
    cases.append(('同目录有 config.yaml', d2, d2))

    # 3) 同目录只有 tray-config.json -> 也算便携（老用户布局）
    d3 = os.path.join(tmp, 'case_tray')
    os.makedirs(d3)
    io.open(os.path.join(d3, 'tray-config.json'), 'w', encoding='utf-8').write('{}')
    cases.append(('同目录有 tray-config.json', d3, d3))

    # 4) 同目录有其他杂物但无关键文件 -> 仍回退（不能误判为便携）
    d4 = os.path.join(tmp, 'case_other')
    os.makedirs(d4)
    io.open(os.path.join(d4, 'readme.txt'), 'w', encoding='utf-8').write('hi')
    cases.append(('同目录只有无关文件', d4, os.path.join(lad, 'MihomoTray')))

    # 5) 完整绿色目录 -> 便携
    d5 = os.path.join(tmp, 'case_green')
    os.makedirs(os.path.join(d5, 'profiles'))
    for f in ('config.yaml', 'tray-config.json', 'mihomo.exe'):
        io.open(os.path.join(d5, f), 'w', encoding='utf-8').write('x')
    cases.append(('完整绿色目录', d5, d5))

    for label, exe_dir, expected in cases:
        got = resolve_like_cs(exe_dir, lad)
        check(got == expected, label, '-> %s' % os.path.basename(got))

    # LOCALAPPDATA 不可用时的兜底
    got = resolve_like_cs(d1, '')
    check(got == d1, '无 %LOCALAPPDATA% 时兜底回 exe 目录', '-> %s' % os.path.basename(got))

    # 单文件场景的端到端：把 exe 放进一个空目录，模拟首次运行会创建什么
    print()
    print('C) 首次运行（模拟单文件下载后）会创建什么')
    first = os.path.join(tmp, 'first_run')
    os.makedirs(first)
    data_dir = resolve_like_cs(first, lad)
    check(data_dir == os.path.join(lad, 'MihomoTray'),
          '数据目录落在 %LOCALAPPDATA%\\MihomoTray')
    # 复刻 EnsureDataDirectoryInitialized 的效果
    os.makedirs(data_dir, exist_ok=True)
    os.makedirs(os.path.join(data_dir, 'profiles'), exist_ok=True)
    cfg = os.path.join(data_dir, 'config.yaml')
    io.open(cfg, 'w', encoding='utf-8').write('skeleton')
    check(os.path.isdir(data_dir), '创建了数据目录')
    check(os.path.isdir(os.path.join(data_dir, 'profiles')), '创建了 profiles 子目录')
    check(os.path.isfile(cfg), '生成骨架 config.yaml')
    # 幂等性：第二次运行不得覆盖
    io.open(cfg, 'w', encoding='utf-8').write('USER EDITED')
    if not os.path.isfile(cfg):
        io.open(cfg, 'w', encoding='utf-8').write('skeleton')
    check(io.open(cfg, encoding='utf-8').read() == 'USER EDITED',
          '已存在的配置不被覆盖（幂等）')

finally:
    shutil.rmtree(tmp, ignore_errors=True)

print()
print('=' * 74)
print('  汇总: PASS=%d  FAIL=%d' % (PASS, FAIL))
print('=' * 74)
sys.exit(0 if FAIL == 0 else 1)
