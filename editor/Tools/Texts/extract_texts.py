# 에디터 화면 글 표 만들기: 코드(AvHost · Host/Shared · Host/Core)의 한글이 든 문자열을 모두 뽑아 editor/texts/ko.tsv · en.tsv · ja.tsv로 쓴다.
# 다시 돌리면 이미 적어 둔 "바꿀 글"(번역)은 그대로 두고, 새 원문을 더하고, 코드에서 사라진 원문의 바꿀 글은 맨 아래로 모은다.
# ko.tsv = 한국어 다듬기(비워 두면 원문), en.tsv = 영어, ja.tsv = 일본어 (2026-11-02). 번역이 빈 줄은 한국어 원문으로 보인다.
#   python -I Tools/Texts/extract_texts.py            (editor 폴더에서)
# 표 형식은 AvHost/Texts.cs 맨 위 설명과 같다. $"…{값}…" 문자열은 {0} {1} … 틀로 적는다.
import io, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
EDITOR = os.path.normpath(os.path.join(HERE, '..', '..'))
LANGS = ['ko', 'en', 'ja']
SOURCES = ['AvHost', os.path.join('Host', 'Shared'), os.path.join('Host', 'Core')]
HANGUL = re.compile('[\uac00-\ud7a3]')

HEADER = """# 에디터 화면 글 표 (UTF-8, 칸은 탭으로 나눔)
#   원문 <탭> 바꿀 글 <탭> 위치
# · 바꿀 글을 적은 줄만 바뀐다. 비워 두면 원문 그대로. 원문 칸은 고치지 않는다 (코드와 맞춰 찾는 열쇠).
# · {0} {1} … 은 코드가 넣는 값이다. 바꿀 글에도 같은 번호로 넣는다 (순서는 바꿔도 된다).
# · "{0}개"처럼 원문이 거의 값뿐인 짧은 틀은 다른 글("렌더러 3개" 등)에도 맞을 수 있다. 그런 줄은 꼭 필요할 때만 바꾼다.
# · 줄바꿈은 \\n, 탭은 \\t, 역슬래시는 \\\\ 로 적는다.
# · 테스트 모드(run_test.sh / run_test.cmd)로 띄워 두면 저장할 때마다 바로 화면에 반영된다.
# · 표를 새로 뽑기: python -I Tools/Texts/extract_texts.py (적어 둔 바꿀 글은 남는다)
# · 문자열을 + 로 이어 붙인 글은 조각마다 따로 나오지만, 화면에는 이어진 글 하나로 보여 표로는 바꿀 수 없다 — 그런 글은 코드의 조각을 직접 고친다.
# · 용어 기준은 editor/NOTES.md "화면 글 용어 기준" 절을 따른다.
"""

ESC = {'n': '\n', 't': '\t', 'r': '\r', '0': '\0', '\\': '\\', '"': '"', "'": "'", 'a': '\a', 'b': '\b', 'f': '\f', 'v': '\v'}


def scan(src):
    """C# 소스에서 (줄 번호, 문자열 값) — 주석·문자 리터럴은 건너뛰고, 보간 문자열의 {식}은 {0} {1} …로."""
    i, n, line = 0, len(src), 1
    out = []
    while i < n:
        c = src[i]
        if c == '\n':
            line += 1; i += 1; continue
        if src.startswith('//', i):
            j = src.find('\n', i); i = n if j < 0 else j; continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2); j = n if j < 0 else j + 2
            line += src.count('\n', i, j); i = j; continue
        if c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == '\\' else 1
            i = j + 1; continue
        m = re.match(r'(\$@|@\$|\$|@)?"', src[i:i + 3])
        if m:
            prefix = m.group(1) or ''
            interp, verbatim = '$' in prefix, '@' in prefix
            j = i + len(m.group(0))
            start_line = line
            buf, hole = [], 0
            while j < n:
                ch = src[j]
                if verbatim and ch == '"':
                    if j + 1 < n and src[j + 1] == '"': buf.append('"'); j += 2; continue
                    break
                if not verbatim and ch == '"': break
                if not verbatim and ch == '\\' and j + 1 < n:
                    e = src[j + 1]
                    if e == 'u' and j + 5 < n: buf.append(chr(int(src[j + 2:j + 6], 16))); j += 6; continue
                    buf.append(ESC.get(e, e)); j += 2; continue
                if interp and ch == '{':
                    if j + 1 < n and src[j + 1] == '{': buf.append('{'); j += 2; continue
                    depth, k = 1, j + 1
                    while k < n and depth > 0:   # 식 안의 문자열·괄호를 건너뛴다
                        if src[k] == '"':
                            k += 1
                            while k < n and src[k] != '"': k += 2 if src[k] == '\\' else 1
                        elif src[k] == '{': depth += 1
                        elif src[k] == '}': depth -= 1
                        k += 1
                    buf.append('{%d}' % hole); hole += 1
                    j = k; continue
                if interp and ch == '}' and j + 1 < n and src[j + 1] == '}': buf.append('}'); j += 2; continue
                if ch == '\n': line += 1
                buf.append(ch); j += 1
            out.append((start_line, ''.join(buf)))
            i = j + 1; continue
        i += 1
    return out


def esc(s): return s.replace('\\', '\\\\').replace('\t', '\\t').replace('\r', '').replace('\n', '\\n')


def unesc(s):
    r, i = [], 0
    while i < len(s):
        if s[i] == '\\' and i + 1 < len(s):
            c = s[i + 1]; r.append('\n' if c == 'n' else '\t' if c == 't' else c); i += 2
        else: r.append(s[i]); i += 1
    return ''.join(r)


# 환경 탭 글 (2026-11-03): 환경 페이지의 효과 이름·항목 이름·고르기 이름은 렌더러(Unity)의 환경 Provider 스키마(JSON 문자열)에서 온다.
# ParamSchema가 있는 렌더러 파일에서 JSON의 "label"·"group"·"optionLabels" 값과, Label => "…"·group을 만드는 줄의 짧은 글("조명"·"볼륨")을 뽑는다.
RENDERER = os.path.join('Unity', 'Assets', 'Renderer')
JSON_LABEL = re.compile(r'"(?:label|group)":\s*"([^"]*)"')
JSON_OPTIONS = re.compile(r'"optionLabels":\s*\[([^\]]*)\]')


def schema_labels(s):
    """스키마 JSON 글 → 그 안의 label·group·optionLabels 값 (한글이 든 것만, 차례대로)"""
    out = [m.group(1) for m in JSON_LABEL.finditer(s)]
    for m in JSON_OPTIONS.finditer(s): out += re.findall(r'"([^"]*)"', m.group(1))
    return [x for x in dict.fromkeys(out) if HANGUL.search(x) and '{' not in x]


def env_schema_rows():
    folder = os.path.join(EDITOR, RENDERER)
    if not os.path.isdir(folder): return
    for f in sorted(os.listdir(folder)):
        if not f.endswith('.cs'): continue
        src = io.open(os.path.join(folder, f), encoding='utf-8-sig').read()
        if 'ParamSchema' not in src: continue
        src_lines = src.split('\n')
        rows, have = [], set()
        def add(s, line):
            if HANGUL.search(s) and s not in have and '{' not in s: have.add(s); rows.append((s, line))
        for line, s in scan(src):
            if '"label"' in s or '"optionLabels"' in s:
                for x in schema_labels(s): add(x, line)
        # Label => "…" 줄과 group을 만드는 줄 — 보간 식 안의 글("볼륨"·"조명")은 scan이 건너뛰어 줄에서 직접 찾는다
        for n, l in enumerate(src_lines, 1):
            if re.search(r'\bLabel\s*=>|""group""', l):
                for s in re.findall(r'(?<!")"([^"]*)"(?!")', l): add(s, n)
        if rows: yield ((RENDERER + '/' + f).replace('\\', '/'), rows)


def main():
    for lang in LANGS: write(lang)


def write(lang):
    OUT = os.path.join(EDITOR, 'texts', lang + '.tsv')
    old = {}
    if os.path.exists(OUT):
        for l in io.open(OUT, encoding='utf-8-sig').read().split('\n'):
            if not l or l == '#' or l.startswith('# '): continue   # 주석은 '# ' (원문이 #RRGGBB로 시작하는 줄이 있다)
            c = l.split('\t')
            if len(c) >= 2 and c[1]: old[unesc(c[0])] = c[1]
    seen, groups = {}, []
    for d in SOURCES:
        folder = os.path.join(EDITOR, d)
        for f in sorted(os.listdir(folder)):
            if not f.endswith('.cs') or f == 'Texts.cs': continue
            rel = os.path.join(d, f).replace('\\', '/')
            rows = []
            for line, s in scan(io.open(os.path.join(folder, f), encoding='utf-8-sig').read()):
                if not HANGUL.search(s): continue
                # 환경 스키마 JSON(렌더러 없을 때의 기본 스키마 등)은 통째가 아니라 그 안의 이름들을 (2026-11-03)
                for x in (schema_labels(s) if '"label"' in s else [s]):
                    if x in seen: seen[x][1] += 1; continue
                    seen[x] = [rel, 0]
                    rows.append((x, line))
            if rows: groups.append((rel, rows))
    for rel, rows in env_schema_rows():
        rows = [(s, line) for s, line in rows if s not in seen]
        for s, _ in rows: seen[s] = [rel, 0]
        if rows: groups.append((rel, rows))
    lines =[HEADER + ('' if lang == 'ko' else '# · 이 표의 바꿀 글 = %s 번역. 비우면 한국어 원문이 보인다.\n' % {'en': '영어', 'ja': '일본어'}[lang])]
    total = filled = 0
    for rel, rows in groups:
        lines.append('# ==== %s ====' % rel)
        for s, line in rows:
            more = seen[s][1]
            rep = old.pop(s, '')
            total += 1; filled += bool(rep)
            lines.append('%s\t%s\t%s:%d%s' % (esc(s), rep, os.path.basename(rel), line, (' 외 %d곳' % more) if more else ''))
        lines.append('')
    if old:
        lines.append('# ==== 코드에서 사라진 원문 (바꿀 글을 옮겨 적은 뒤 지워도 된다) ====')
        for s, rep in old.items(): lines.append('%s\t%s\t(없음)' % (esc(s), rep))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    io.open(OUT, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines) + '\n')
    print('%s: 원문 %d줄 (바꿀 글 %d줄), 사라진 원문 %d줄' % (OUT, total, filled, len(old)))


if __name__ == '__main__':
    main()
