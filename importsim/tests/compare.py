"""importsim 출력(--out)과 Unity 정답(PrefabDumper)을 프리팹 단위로 비교.

python -I -X utf8 compare.py <정답 폴더> <importsim 출력 폴더> [-v]
"""
import glob
import json
import os
import sys
from collections import Counter


def ref(r):
    # 프로젝트에 없는 에셋을 가리키면 Unity 에서는 None 으로 보인다
    if not r or not r.get("fileID") or r.get("asset") == "(없음)":
        return None
    return (r.get("guid"), int(r["fileID"]))


def close(x, y, tol=2e-4):
    return all(abs(i - j) <= tol * max(1.0, abs(i), abs(j)) for i, j in zip(x, y))


def cmp_node(a, b, path, diffs):
    p = f"{path}/{a['name']}"
    if a["name"] != b["name"]:
        diffs.append(("이름", p, a["name"], b["name"]))
    if a["active"] != b["active"]:
        diffs.append(("active", p, a["active"], b["active"]))
    ta = [c["type"] for c in a["components"]]
    tb = [c["type"] for c in b["components"]]
    if ta != tb:
        diffs.append(("컴포넌트 목록", p, ta, tb))
    else:
        for ca, cb in zip(a["components"], b["components"]):
            t = ca["type"]
            if "enabled" in ca and "enabled" in cb and ca["enabled"] != cb["enabled"]:
                diffs.append(("enabled", f"{p}:{t}", ca["enabled"], cb["enabled"]))
            if "mesh" in ca and ref(ca.get("mesh")) != ref(cb.get("mesh")):
                diffs.append(("mesh", f"{p}:{t}", ref(ca.get("mesh")), ref(cb.get("mesh"))))
            if "materials" in ca:
                ma = [ref(x) for x in ca["materials"]]
                mb = [ref(x) for x in cb.get("materials", [])]
                if ma != mb:
                    diffs.append(("materials", f"{p}:{t}", ma, mb))
            if "blendShapes" in ca:
                wa = [round(x["weight"], 3) for x in ca["blendShapes"]]
                wb = [round(x["weight"], 3) for x in cb.get("blendShapes", [])]
                if wa != wb:
                    diffs.append(("blendShape 값", f"{p}:{t}", wa[:12], wb[:12]))
                na = [x["name"] for x in ca["blendShapes"]]
                nb = [x["name"] for x in cb.get("blendShapes", [])]
                if na != nb and nb and any(nb):
                    diffs.append(("blendShape 이름", f"{p}:{t}", na[:6], nb[:6]))
            if "bones" in ca and ca["bones"] != cb.get("bones"):
                diffs.append(("m_Bones", f"{p}:{t}", ca["bones"][:4], (cb.get("bones") or [])[:4]))
            if "rootBone" in ca and ca["rootBone"] != cb.get("rootBone"):
                diffs.append(("m_RootBone", f"{p}:{t}", ca["rootBone"], cb.get("rootBone")))
            if "avatar" in ca and ref(ca.get("avatar")) != ref(cb.get("avatar")):
                diffs.append(("avatar", f"{p}:{t}", ref(ca.get("avatar")), ref(cb.get("avatar"))))
    if "p" in a and "p" in b:
        if not close(a["p"], b["p"]):
            diffs.append(("localPosition", p, a["p"], b["p"]))
        if not (close(a["r"], b["r"]) or close(a["r"], [-x for x in b["r"]])):
            diffs.append(("localRotation", p, a["r"], b["r"]))
        if not close(a["s"], b["s"]):
            diffs.append(("localScale", p, a["s"], b["s"]))
    ka = [c["name"] for c in a["children"]]
    kb = [c["name"] for c in b["children"]]
    if ka != kb:
        diffs.append(("자식 목록", p, ka, kb))
        bm = {c["name"]: c for c in b["children"]}
        for c in a["children"]:
            if c["name"] in bm:
                cmp_node(c, bm[c["name"]], p, diffs)
    else:
        for x, y in zip(a["children"], b["children"]):
            cmp_node(x, y, p, diffs)


def count(n):
    return 1 + sum(count(c) for c in n["children"])


def main():
    truth_dir, sim_dir = sys.argv[1], sys.argv[2]
    verbose = "-v" in sys.argv
    perfect = 0
    kinds = Counter()
    nodes = 0
    rows = []
    for f in sorted(glob.glob(os.path.join(truth_dir, "*.json"))):
        t = json.load(open(f, encoding="utf-8"))
        sf = os.path.join(sim_dir, os.path.basename(f))
        if not os.path.exists(sf):
            rows.append(("없음", t["path"], []))
            continue
        s = json.load(open(sf, encoding="utf-8"))
        diffs = []
        if s["root"] is None:
            diffs.append(("루트 없음", "", "", ""))
        else:
            cmp_node(t["root"], s["root"], "", diffs)
        nodes += count(t["root"])
        if not diffs:
            perfect += 1
        for d in diffs:
            kinds[d[0]] += 1
        rows.append(("OK" if not diffs else "DIFF", t["path"], diffs))
    for st, path, diffs in rows:
        if st != "OK":
            print(f"{st:4} {path}  ({len(diffs)}건)")
            for d in diffs[: (20 if verbose else 3)]:
                print(f"       [{d[0]}] {d[1]}\n          Unity: {d[2]}\n          sim  : {d[3]}")
    print(f"\n프리팹 {len(rows)}개 중 완전 일치 {perfect}개, GameObject {nodes}개 비교")
    print("차이 종류:", dict(kinds))


if __name__ == "__main__":
    main()
