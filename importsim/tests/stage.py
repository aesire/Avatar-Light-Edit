"""검증용 Unity 프로젝트에 프리팹 + 의존성(prefab/fbx/mat/asset)만 원래 경로로 복사한다.
스크립트·DLL·셰이더·텍스처는 복사하지 않는다 (실행 코드 배제, 구조 검증에 불필요).

python -I -X utf8 stage.py <unity 프로젝트> [--sample N] [--seed S] <소스...>
소스: .unitypackage 또는 Assets 폴더. 패키지 프리팹은 전부, 폴더는 --sample 개를 고른다.
"""
import os
import random
import re
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "proto"))
from unitypackage import UnityPackage  # noqa: E402

KEEP = (".prefab", ".fbx", ".mat", ".asset")
REF = re.compile(rb"guid: ([0-9a-f]{32})")
GUID = re.compile(r"^guid: ([0-9a-f]{32})", re.M)


def main():
    args = sys.argv[1:]
    proj = args.pop(0)
    sample, seed = 0, 7
    while args and args[0].startswith("--"):
        opt = args.pop(0)
        if opt == "--sample":
            sample = int(args.pop(0))
        elif opt == "--seed":
            seed = int(args.pop(0))
    index = {}        # guid -> (path, meta, loader)
    roots = []
    for src in args:
        if os.path.isdir(src):
            base = os.path.dirname(os.path.abspath(src).rstrip("\\/"))
            prefabs = []
            for d, _, names in os.walk(src):
                for n in names:
                    if not n.endswith(".meta"):
                        continue
                    f = os.path.join(d, n[:-5])
                    if not os.path.isfile(f) or not f.lower().endswith(KEEP):
                        continue
                    try:
                        meta = open(os.path.join(d, n), encoding="utf-8").read()
                    except OSError:
                        continue
                    m = GUID.search(meta)
                    if not m:
                        continue
                    rel = os.path.relpath(f, base).replace("\\", "/")
                    index[m.group(1)] = (rel, meta, (lambda p=f: open(p, "rb").read()))
                    if f.endswith(".prefab"):
                        prefabs.append(m.group(1))
            random.Random(seed).shuffle(prefabs)
            # PrefabInstance 를 가진 프리팹 위주로 고른다
            chosen = [g for g in prefabs if b"PrefabInstance:" in index[g][2]()][:sample]
            roots += chosen
        else:
            pkg = UnityPackage.open(src)
            keep = [a for a in pkg.assets.values() if a.pathname.lower().endswith(KEEP)]
            data = pkg.read([a.guid for a in keep])
            for a in keep:
                if a.guid in data:
                    index[a.guid] = (a.pathname, a.meta, (lambda b=data[a.guid]: b))
                    if a.pathname.endswith(".prefab"):
                        roots.append(a.guid)

    todo, done = list(roots), set()
    while todo:
        g = todo.pop()
        if g in done or g not in index:
            continue
        done.add(g)
        path, meta, load = index[g]
        if path.endswith(".prefab"):
            todo += [m.decode() for m in REF.findall(load())]
        todo += [m.decode() for m in REF.findall(meta.encode())]   # FBX externalObjects 등
    total = 0
    for g in done:
        path, meta, load = index[g]
        dst = os.path.join(proj, path)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        b = load()
        total += len(b)
        open(dst, "wb").write(b)
        open(dst + ".meta", "w", encoding="utf-8", newline="").write(meta)
    print(f"루트 프리팹 {len(roots)}개, 복사 {len(done)}개 ({total / 1e6:.1f} MB)")
    for g in roots:
        print("ROOT", g, index[g][0])


if __name__ == "__main__":
    main()
