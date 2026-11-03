# NuGet 패키지와 의존성을 내려받아 net8.0용 관리 DLL과 win-x64 네이티브 DLL만 펼친다 (.NET SDK 없이 csc로 빌드하기 위함).
# 사용: python -I fetch_nuget.py <출력 폴더> <패키지>=<버전> ...
import sys, os, io, zipfile, urllib.request, re, xml.etree.ElementTree as ET

out = sys.argv[1]
pkgdir = os.path.join(out, 'nupkg'); libdir = os.path.join(out, 'lib'); natdir = os.path.join(out, 'native')
for d in (pkgdir, libdir, natdir): os.makedirs(d, exist_ok=True)
TFMS = ['net8.0', 'net7.0', 'net6.0', 'net5.0', 'netcoreapp3.1', 'netstandard2.1', 'netstandard2.0']
# .NET 8 런타임에 이미 들어 있는 패키지는 건너뛴다
SKIP = re.compile(r'^(System\.|Microsoft\.CSharp|Microsoft\.Win32\.|NETStandard\.Library|Microsoft\.NETCore\.|Microsoft\.Bcl\.AsyncInterfaces$|Avalonia\.BuildServices$)', re.I)

def ver_floor(rng):
    m = re.match(r'^[\[\(]?\s*([^,\]\)]+)', rng.strip())
    return m.group(1).strip() if m else rng

seen = {}
def fetch(pid, ver):
    key = pid.lower()
    if key in seen or SKIP.match(pid): return
    seen[key] = ver
    url = f'https://api.nuget.org/v3-flatcontainer/{key}/{ver.lower()}/{key}.{ver.lower()}.nupkg'
    path = os.path.join(pkgdir, f'{key}.{ver}.nupkg')
    if not os.path.exists(path):
        print('get', pid, ver, flush=True)
        urllib.request.urlretrieve(url, path)
    z = zipfile.ZipFile(path)
    names = z.namelist()
    # 관리 DLL
    for tfm in TFMS:
        dlls = [n for n in names if n.lower().startswith(f'lib/{tfm}/') and n.lower().endswith('.dll')]
        if dlls:
            for n in dlls: open(os.path.join(libdir, os.path.basename(n)), 'wb').write(z.read(n))
            break
    # 네이티브 DLL
    for n in names:
        if n.lower().startswith('runtimes/win-x64/native/') and n.lower().endswith('.dll'):
            open(os.path.join(natdir, os.path.basename(n)), 'wb').write(z.read(n))
    # 의존성
    nuspec = next(n for n in names if n.endswith('.nuspec'))
    root = ET.fromstring(z.read(nuspec))
    ns = {'n': root.tag.split('}')[0].strip('{')}
    groups = root.findall('.//n:dependencies/n:group', ns)
    chosen = None
    for tfm in TFMS:
        for g in groups:
            tf = (g.get('targetFramework') or '').lower().replace('.netstandard', 'netstandard').replace('.netcoreapp', 'netcoreapp')
            if tf in (tfm, tfm.replace('net', 'net', 1)) or tf == '.' + tfm or tf.replace('.', '') == tfm.replace('.', ''):
                chosen = g; break
        if chosen is not None: break
    deps = chosen.findall('n:dependency', ns) if chosen is not None else root.findall('.//n:dependencies/n:dependency', ns)
    for d in deps:
        fetch(d.get('id'), ver_floor(d.get('version')))

for spec in sys.argv[2:]:
    pid, ver = spec.split('=')
    fetch(pid, ver)
print('packages:', len(seen))
print('lib:', sorted(os.listdir(libdir)))
print('native:', sorted(os.listdir(natdir)))
