"""탄젠트 오차: 위치·노멀·UV0 가 같은 정점끼리 짝지어 탄젠트 xyz 오차와 w 부호 일치를 잰다.
사용: python tan_error.py <unity_dump> <sim_dump> [--only=이름]"""
import json, os, sys, glob, math
from collections import defaultdict

dump = int(next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--dump=")), "0"))
worst = []
only = next((a.split("=", 1)[1] for a in sys.argv if a.startswith("--only=")), None)


def key(m, i):
    v, n, u = m["vertices"], m["normals"], m["uv0"]
    return (round(v[i*3], 4), round(v[i*3+1], 4), round(v[i*3+2], 4),
            round(n[i*3], 2), round(n[i*3+1], 2), round(n[i*3+2], 2),
            round(u[i*2], 4), round(u[i*2+1], 4))


errs, wbad, total, unmatched = [], 0, 0, 0
per_mesh = []
for up in glob.glob(os.path.join(sys.argv[1], "*.json")):
    sp = os.path.join(sys.argv[2], os.path.basename(up))
    if not os.path.exists(sp):
        continue
    U = json.load(open(up, encoding="utf-8")); S = json.load(open(sp, encoding="utf-8"))
    smap = {m["name"]: m for m in S.get("meshes", [])}
    for um in U.get("meshes", []):
        if only and only not in U["path"] + ":" + um["name"]:
            continue
        sm = smap.get(um["name"])
        if not sm or "tangents" not in um or "tangents" not in sm:
            continue
        idx = defaultdict(list)
        for i in range(len(sm["vertices"]) // 3):
            idx[key(sm, i)].append(i)
        ut, st = um["tangents"], sm["tangents"]
        me, mw = [], 0
        for i in range(len(um["vertices"]) // 3):
            c = idx.get(key(um, i))
            if not c:
                unmatched += 1
                continue
            best = min(c, key=lambda j: sum((ut[i*4+k] - st[j*4+k]) ** 2 for k in range(3)) + (0 if ut[i*4+3] == st[j*4+3] else 9))
            e = math.sqrt(sum((ut[i*4+k] - st[best*4+k]) ** 2 for k in range(3)))
            me.append(e); total += 1
            if dump: worst.append((e, um["name"], i, best, [round(x, 4) for x in ut[i*4:i*4+4]], [round(x, 4) for x in st[best*4:best*4+4]], [round(x, 4) for x in um["vertices"][i*3:i*3+3]], [round(x, 4) for x in um["uv0"][i*2:i*2+2]]))
            if (ut[i*4+3] > 0) != (st[best*4+3] > 0):
                mw += 1
        errs += me; wbad += mw
        if me:
            me.sort()
            per_mesh.append((U["path"].split("/")[-1] + ":" + um["name"], len(me), me[len(me)//2], me[int(len(me)*.9)], me[-1], mw))
errs.sort()
print("짝지은 정점", total, "짝 없음", unmatched, "w 부호 다름", wbad)
if errs:
    q = lambda p: errs[min(len(errs)-1, int(len(errs)*p))]
    print("xyz 오차 50/90/99/max = %.4g / %.4g / %.4g / %.4g" % (q(.5), q(.9), q(.99), errs[-1]))
    print("오차 < 1e-4 비율 %.4f" % (sum(1 for e in errs if e < 1e-4) / len(errs)))
for r in per_mesh:
    print("  %s n=%d med=%.3g p90=%.3g max=%.3g wbad=%d" % r)

worst.sort(key=lambda x: -x[0])
for w in worst[:dump]:
    print(w)
