import io, sys, re

def lex(s):
    """返回 [(char, line)]，已剔除注释与字符串/字符字面量。"""
    out=[]; i=0; ln=1; instr=False; incom=False; inchr=False; esc=False
    while i < len(s):
        c = s[i]
        if c == '\n':
            ln += 1
            if incom: incom = False
        if incom:
            i += 1; continue
        if instr:
            if esc: esc = False
            elif c == '\\': esc = True
            elif c == '"': instr = False
            i += 1; continue
        if inchr:
            if esc: esc = False
            elif c == '\\': esc = True
            elif c == "'": inchr = False
            i += 1; continue
        if c == '/' and i + 1 < len(s) and s[i+1] == '/':
            incom = True; i += 2; continue
        if c == '/' and i + 1 < len(s) and s[i+1] == '*':
            j = s.find('*/', i+2)
            ln += s[i:j+2].count('\n') if j > 0 else 0
            i = (j + 2) if j > 0 else len(s); continue
        if c == '"': instr = True; i += 1; continue
        if c == "'": inchr = True; i += 1; continue
        if c in '{}()[]': out.append((c, ln))
        i += 1
    return out

def check(path):
    s = io.open(path, encoding='utf-8').read()
    toks = lex(s)
    pairs = {'}':'{', ')':'(', ']':'['}
    stack=[]; errs=[]
    for ch, ln in toks:
        if ch in '{([':
            stack.append((ch, ln))
        else:
            if not stack:
                errs.append('line %d: 多余的 %s' % (ln, ch)); continue
            op, ol = stack.pop()
            if op != pairs[ch]:
                errs.append('line %d: %s 与 line %d 的 %s 不匹配' % (ln, ch, ol, op))
    for op, ln in stack:
        errs.append('line %d: %s 未闭合' % (ln, op))
    return errs, toks

if __name__ == '__main__':
    bad = 0
    for p in sys.argv[1:]:
        errs, toks = check(p)
        if errs:
            bad += 1
            print('[FAIL] %s' % p)
            for e in errs[:25]: print('   ', e)
        else:
            print('[ OK ] %s  (%d 个括号标记全部配对)' % (p, len(toks)))
    sys.exit(1 if bad else 0)
