"""블렌드쉐이프 델타 노멀(Calculate 모드 근사)의 오차 분포. 정점은 compare_geo 와 같은 방식으로 매칭.

python -I -X utf8 dn_error.py <Unity 폴더> <sim 폴더>
"""
import glob
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_geo import match_vertices  # noqa: E402

FIELD = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--field=")), "dn")
errs, zero = [], []
extra_meshes = 0
for f in sorted(glob.glob(os.path.join(sys.argv[1], "*.json"))):
    u = json.load(open(f, encoding="utf-8"))
    sf = os.path.join(sys.argv[2], os.path.basename(f))
    if not os.path.exists(sf):
        continue
    s = json.load(open(sf, encoding="utf-8"))
    sm = {m["name"]: m for m in s["meshes"]}
    for m in u["meshes"]:
        o = sm.get(m["name"])
        if not o or not m["blendShapes"]:
            continue
        if not any(fr[FIELD] for b in m["blendShapes"] for fr in b["frames"]):
            # Unity 에 델타가 없는 메시: sim 도 없어야 한다
            if any(fr.get(FIELD) for b in o["blendShapes"] for fr in b["frames"]):
                extra_meshes += 1
            continue
        # 같은 (위치, 노멀, UV, 탄젠트) 를 가진 sim 정점들이 후보. 분리된 정점끼리 엇갈려 짝짓지 않도록 가장 가까운 값을 쓴다
        def key(mm, i):
            k = tuple(round(x * 1e4) for x in mm["vertices"][i * 3:i * 3 + 3]) + tuple(round(x * 1e3) for x in mm["normals"][i * 3:i * 3 + 3])
            if "uv0" in mm:
                k += tuple(round(x * 1e4) for x in mm["uv0"][i * 2:i * 2 + 2])
            return k
        cand = {}
        for j in range(o["vertexCount"]):
            cand.setdefault(key(o, j), []).append(j)
        mapping = match_vertices(m, o)[0]
        for bu, bs in zip(m["blendShapes"], o["blendShapes"]):
            for fu, fs in zip(bu["frames"], bs["frames"]):
                du = {fu[FIELD][k]: fu[FIELD][k + 1:k + 4] for k in range(0, len(fu[FIELD]), 4)}
                ds = {fs.get(FIELD, [])[k]: fs[FIELD][k + 1:k + 4] for k in range(0, len(fs.get(FIELD, [])), 4)}
                for i, j in enumerate(mapping):
                    if j is None:
                        continue
                    a = du.get(i, [0, 0, 0])
                    js = cand.get(key(m, i), [j])
                    e = min(math.dist(a, ds.get(jj, [0, 0, 0])) for jj in js)
                    if a == [0, 0, 0] and all(ds.get(jj, [0, 0, 0]) == [0, 0, 0] for jj in js):
                        continue
                    errs.append(e)
                    zero.append(math.dist(a, [0, 0, 0]))
errs.sort()
zero.sort()
n = len(errs)
q = lambda xs, p: round(xs[min(len(xs) - 1, int(len(xs) * p))], 4)
print(f"[{FIELD}] 비교 {n}개, Unity 에는 없는데 sim 에만 있는 메시 {extra_meshes}개")
print("근사 오차    50/75/90/95/99%:", [q(errs, p) for p in (.5, .75, .9, .95, .99)])
print("0으로 둘 때  50/75/90/95/99%:", [q(zero, p) for p in (.5, .75, .9, .95, .99)])
