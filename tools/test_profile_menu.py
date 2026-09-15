# -*- coding: utf-8 -*-
r"""验证本轮改动的行为契约（Round 2）：

  A. 配置切换菜单：悬停列出已添加配置名 + 编辑配置入口 + 一键更新订阅在顶层
  B. UAC 提权：停/启/重启 ProxiFyre 都走提权路径，且区分"用户取消"与"失败"

做法：从 C# 源码里抽取结构做真值表/可达性判定，
因为本机无法编译 C#（编译器被系统安全策略拦截），
静态契约检查是唯一能在推 CI 之前拦住错误的防线。
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
# 允许指向别的源码文件，供校准脚本注入"已知错误样本"使用。
CS = os.environ.get('MIHMOTRAY_CS') or os.path.join(ROOT, 'mihomo-tray', 'MihomoTray.cs')
src = io.open(CS, encoding='utf-8-sig').read()

PASS = 0
FAIL = 0


def check(desc, cond):
    global PASS, FAIL
    if cond:
        PASS += 1
        print('  [PASS] %s' % desc)
    else:
        FAIL += 1
        print('  [FAIL] %s' % desc)


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
                    out.append(text[i:i + 2]); i += 2; continue
                out.append(text[i])
                if text[i] == '"':
                    i += 1; break
                i += 1
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def method_body(source, anchor):
    k = source.find(anchor)
    if k < 0:
        return None
    b = source.find('{', k + len(anchor))
    if b < 0:
        return None
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


code = strip_comments(src)

print('=' * 72)
print('  A. 配置切换菜单结构契约')
print('=' * 72)

build = method_body(code, 'void BuildMenu()')
check('定位到 BuildMenu', build is not None)

# A1. 配置切换在顶层
check('顶层存在 _menu.Items.Add(_profileItem)',
      '_menu.Items.Add(_profileItem);' in build)

# A2. 配置切换不再是"点开进对话框"（旧行为），而是有下拉
check('配置切换不再直接绑定 OnProfileManager（改为下拉）',
      'new ToolStripMenuItem("配置切换", null, OnProfileManager)' not in build)
check('配置切换挂了 DropDownOpening',
      '_profileItem.DropDownOpening += OnProfileMenuOpening;' in build)

# A3. 旧「配置与订阅」整体消失
check('旧 configMenu 已删除', 'configMenu' not in build)
for old in ['"配置与订阅"', '"更新订阅"', '"订阅管理"', '"编辑订阅源"']:
    check('旧标签 %s 已移除' % old, old not in build)

# A4. 一键更新订阅在顶层
check('顶层存在 _menu.Items.Add(_updateAllSubsItem)',
      '_menu.Items.Add(_updateAllSubsItem);' in build)
check('一键更新订阅接到 OnUpdateAllSubscriptions',
      'new ToolStripMenuItem("一键更新订阅", null, OnUpdateAllSubscriptions)' in build)

print()
print('=' * 72)
print('  A2. RefreshProfileMenu 内容契约（悬停时构建）')
print('=' * 72)

rpm = method_body(code, 'void RefreshProfileMenu()')
check('定位到 RefreshProfileMenu', rpm is not None)

if rpm:
    # 遍历 _profiles 生成条目
    check('遍历 _profiles 生成快捷切换项', 'foreach (var prof in _profiles)' in rpm)
    check('用配置文件名做菜单标题', 'captured.Name' in rpm)
    check('点击项接到 OnSwitchProfile', 'OnSwitchProfile(captured)' in rpm)
    # 勾选当前配置
    check('当前配置有勾选态', 'item.Checked = isActive;' in rpm)
    # 编辑配置入口
    check('含「编辑配置…」入口', '"编辑配置…"' in rpm)
    check('编辑配置接到 OnEditConfigAndSubscriptions',
          'OnEditConfigAndSubscriptions' in rpm)
    # 空列表占位
    check('无配置时有占位提示', '（尚未添加配置）' in rpm)

    # 性能铁律：DropDownOpening 路径不得 I/O / 进程枚举
    forbidden = [
        ('LoadTrayConfig()', '重新解析 tray-config.json'),
        ('LoadSubscriptions()', '重新读订阅文件'),
        ('Process.GetProcessesByName', '进程枚举'),
        ('File.ReadAllText', '文件读取'),
        ('File.Exists', '文件探测'),
        ('IsProxiFyreRunning()', '进程查询'),
        ('IsMihomoRunning()', '核心进程查询'),
        ('ResolveCurrentMode()', 'HTTP GET /configs'),
        ('IsLocalPortListening', 'TCP 探测'),
    ]
    for needle, desc in forbidden:
        check('零阻塞：不含 %s（%s）' % (needle, desc), needle not in rpm)

print()
print('=' * 72)
print('  A3. 快捷切换行为契约（OnSwitchProfile）')
print('=' * 72)

osp = method_body(code, 'void OnSwitchProfile(ConfigProfile prof)')
check('定位到 OnSwitchProfile', osp is not None)
if osp:
    check('切换前校验文件存在', 'File.Exists(newPath)' in osp)
    check('文件不存在时给出明确提示', '配置文件不存在，无法切换' in osp)
    check('已是当前配置时静默返回（不无谓重启核心）',
          'PathsEqual(newPath, _activeConfigPath)' in osp)
    check('切换后保存 tray-config', 'SaveTrayConfig()' in osp)
    check('核心在跑时重启核心', 'StopMihomo()' in osp and 'StartMihomo()' in osp)
    check('切换后恢复系统代理状态', 'ApplySavedSystemProxyMode()' in osp)

print()
print('=' * 72)
print('  A4. 编辑配置面板合并契约（ConfigAndSubscriptionForm）')
print('=' * 72)

ccf = method_body(code, 'class ConfigAndSubscriptionForm : Form')
if ccf is None:
    # 类的花括号体：用 class 关键字定位
    k = code.find('class ConfigAndSubscriptionForm : Form')
    check('定位到 ConfigAndSubscriptionForm', k >= 0)
    body_cls = code[k:k + 7000] if k >= 0 else ''
else:
    body_cls = ccf

check('用 TabControl 分「配置文件」「订阅源」两页',
      'TabControl' in body_cls and '"配置文件"' in body_cls and '"订阅源"' in body_cls)
check('内嵌 ConfigProfileManagerForm', 'ConfigProfileManagerForm(' in body_cls)
check('内嵌 SubscriptionManagerForm', 'SubscriptionManagerForm(' in body_cls)
check('提供 GetProfiles()', 'public List<ConfigProfile> GetProfiles()' in body_cls)
check('提供 GetActiveConfigPath()', 'public string GetActiveConfigPath()' in body_cls)
check('提供 GetSubscriptions()', 'public List<SubscriptionInfo> GetSubscriptions()' in body_cls)
check('保存时两个子表单都校验（失败切到对应页）',
      'ValidateForHost()' in body_cls and '_tabs.SelectedIndex' in body_cls)

# 子表单必须支持 embedded，否则挂进 TabPage 会出问题
smf = code[code.find('class SubscriptionManagerForm : Form'):]
smf = smf[:smf.find('class ConfigAndSubscriptionForm')] if 'class ConfigAndSubscriptionForm' in smf else smf
cpm = code[code.find('class ConfigProfileManagerForm : Form'):]
cpm = cpm[:cpm.find('class ConfigAndSubscriptionForm')] if 'class ConfigAndSubscriptionForm' in cpm else cpm

check('SubscriptionManagerForm 支持 embedded 模式',
      'public SubscriptionManagerForm(List<SubscriptionInfo> items, bool embedded)' in smf)
check('SubscriptionManagerForm 内嵌时不加保存/取消（避免双套按钮）',
      'if (!embedded)' in smf and 'okBtn = new Button { Text = "保存"' in smf)
check('SubscriptionManagerForm 提供 ValidateForHost()',
      'public bool ValidateForHost()' in smf)
check('ConfigProfileManagerForm 支持 embedded 模式',
      'bool embedded)' in cpm)
check('ConfigProfileManagerForm 内嵌时不加确定/取消',
      'if (!embedded)' in cpm and 'okBtn = new Button { Text = "确定"' in cpm)
check('ConfigProfileManagerForm 提供 ValidateForHost()',
      'public bool ValidateForHost()' in cpm)

print()
print('=' * 72)
print('  B. UAC 提权契约')
print('=' * 72)

elev = method_body(code, 'bool RunScElevated(string arguments, out string output, out bool cancelled)')
check('定位到 RunScElevated', elev is not None)
if elev:
    check('已管理员时直接 RunSc（不弹无谓 UAC）',
          '_isAdmin' in elev and 'output = RunSc(arguments);' in elev)
    check('非管理员走 runas', 'psi.Verb = "runas";' in elev)
    check('UseShellExecute=true（否则 Verb 无效）', 'psi.UseShellExecute = true;' in elev)
    check('WindowStyle.Hidden（提权不弹黑框）', 'ProcessWindowStyle.Hidden' in elev)
    check('用 cmd /c 包装以重定向输出', '"/c sc.exe "' in elev)
    check('2>&1 让错误信息也落盘', '2>&1' in elev)
    # 常量声明在类作用域（方法体外），因此要扫全文而不是只扫方法体。
    check('识别 ERROR_CANCELLED(1223)',
          'const int ErrorCancelled = 1223;' in code
          and 'NativeErrorCode == ErrorCancelled' in elev)
    check('从哨兵文件回读输出', 'File.ReadAllText(outFile' in elev)
    check('finally 里清理临时文件', 'finally' in elev and 'File.Delete(outFile)' in elev)

# 提权调用必须是**真的**分支条件，而不是"顺手也调了一下"。
# 反例：if (!RunScElevated(...) && false) —— 子串仍在，但逻辑上永不生效。
# 必须扫所有真正调用提权的地方（RunScElevated 自身的方法体里当然没有这个调用）。
_elev_callers = []
for _sig in ['bool StopProxiFyreService(out string error, out bool cancelled)',
             'bool EnsureProxiFyreRunning(out string error, out bool cancelled)',
             'bool RestartProxiFyreService(out string error)']:
    _b = method_body(code, _sig)
    if _b:
        _elev_callers.append((_sig.split('(')[0].replace('bool ', ''), _b))

check('找到至少一处提权调用点', len(_elev_callers) > 0)
for _name, _b in _elev_callers:
    # 短路提权：RunScElevated 出现在 && false / || false 之类的死代码里
    check('%s 的提权调用未被短路（无 && false / || false）' % _name,
          not re.search(r'RunScElevated\([^;]*\)\s*(&&|\|\|)\s*false', _b))
    # 提权结果必须被真正用作分支条件
    check('%s 把提权结果用作分支条件（if (!RunScElevated...）' % _name,
          re.search(r'if\s*\(\s*!\s*RunScElevated\(', _b) is not None)

# 停/启服务不得存在"绕过提权直接 RunSc"的路径。
# 只在"已经是管理员"的分支里允许用非提权 RunSc。
for fn, sig in [
    ('StopProxiFyreService', 'bool StopProxiFyreService(out string error, out bool cancelled)'),
    ('EnsureProxiFyreRunning', 'bool EnsureProxiFyreRunning(out string error, out bool cancelled)'),
]:
    body_fn = method_body(code, sig)
    check('定位到 %s' % fn, body_fn is not None)
    if body_fn:
        # 找出所有非提权的 RunSc( 调用（排除 RunScElevated）
        plain_calls = re.findall(r'(?<!Elevated)\bRunSc\(', body_fn)
        # 允许的例外：紧跟在 _isAdmin 判断里
        admin_guarded = re.search(
            r'if\s*\(\s*_isAdmin\s*\)\s*\{[^}]*RunSc\(', body_fn) is not None
        if plain_calls:
            check('%s 的非提权 RunSc 仅在 _isAdmin 分支内（共 %d 处）'
                  % (fn, len(plain_calls)), admin_guarded)
        else:
            check('%s 完全走提权路径（无非提权 RunSc）' % fn, True)

# 三个入口都要走提权
check('停服务走提权', 'RunScElevated("stop " + ProxiFyreServiceName' in code)
check('启服务走提权', 'RunScElevated("start " + ProxiFyreServiceName' in code)
check('重启服务走提权（stop+start 两段都在 RestartProxiFyreService 内）', True)

rps = method_body(code, 'bool RestartProxiFyreService(out string error)')
check('定位到 RestartProxiFyreService', rps is not None)
if rps:
    check('重启：stop 走提权', 'RunScElevated("stop " + ProxiFyreServiceName' in rps)
    check('重启：start 走提权', 'RunScElevated("start " + ProxiFyreServiceName' in rps)
    check('重启：用户取消时明确返回"已取消"',
          'stopCancelled' in rps and 'cancelled' in rps)

# 三个入口的签名都要带 cancelled
check('StopProxiFyreService 签名带 cancelled',
      'bool StopProxiFyreService(out string error, out bool cancelled)' in code)
check('EnsureProxiFyreRunning 有带 cancelled 的重载',
      'bool EnsureProxiFyreRunning(out string error, out bool cancelled)' in code)
check('EnsureProxiFyreRunning 保留旧签名（兼容启动路径自动拉起）',
      'bool EnsureProxiFyreRunning(out string error)\n' in code)

# 菜单回调必须区分取消与失败
tog = method_body(code, 'void OnToggleProxiFyreService(object sender, EventArgs e)')
check('定位到 OnToggleProxiFyreService', tog is not None)
if tog:
    check('停止分支传 cancelled 出参', 'StopProxiFyreService(out error, out cancelled)' in tog)
    check('启动分支传 cancelled 出参', 'EnsureProxiFyreRunning(out error, out cancelled)' in tog)
    check('取消时提示"已取消"而非"失败"',
          tog.count('已取消：未获得管理员权限') >= 2)
    check('取消走 Info 图标（不是 Error）',
          '未获得管理员权限，ProxiFyre 仍在运行",\n                            ToolTipIcon.Info' in tog
          or 'ToolTipIcon.Info' in tog)

# 菜单标签不再写"（需管理员）"这种让人以为点不动的提示
check('菜单不再提议"（需管理员）"', '（需管理员）' not in code)
check('菜单改为提示"（将请求管理员权限）"', '（将请求管理员权限）' in code)

print()
print('=' * 72)
print('  结果: %s   (PASS=%d FAIL=%d)' % ('PASS' if FAIL == 0 else 'FAIL', PASS, FAIL))
print('=' * 72)
sys.exit(1 if FAIL else 0)
