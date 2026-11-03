"""importsim --geometry-out 과 Unity GeometryDumper 결과 비교.
Unity 는 정점·삼각형 순서를 최적화하므로 정점을 (위치, 노멀, UV0) 허용 오차 최근접으로 매칭한다.

python -I -X utf8 compare_geo.py <Unity 폴더> <sim 폴더> [-v] [--only=경로일부 ...]
"""
import glob
import json
import math
import os
import sys
from collections import Counter, defaultdict

VERBOSE = "-v" in sys.argv
USE_TANGENT_MATCH = "--no-tangent-match" not in sys.argv
ONLY = [a.split("=", 1)[1] for a in sys.argv if a.startswith("--only=")]


def close(a, b, tol):
    return all(abs(x - y) <= tol * max(1.0, abs(x), abs(y)) for x, y in zip(a, b))


def qclose(a, b, tol=2e-4):
    return close(a, b, tol) or close(a, [-x for x in b], tol)


class Stats:
    def __init__(self):
        self.c = Counter()
        self.notes = defaultdict(list)

    def add(self, name, ok, note=None):
        self.c[name + (" OK" if ok else " NG")] += 1
        if not ok and note and len(self.notes[name]) < 6:
            self.notes[name].append(note)


def cmp_nodes(u, s, st, model):
    sm = {n["path"]: n for n in s["nodes"]}
    for n in u["nodes"]:
        o = sm.get(n["path"])
        if o is None:
            st.add("노드 존재", False, f"{model}: {n['path']}")
            continue
        st.add("localPosition", close(n["p"], o["p"], 2e-4), f"{model}:{n['path']} U{n['p']} S{o['p']}")
        st.add("localRotation", qclose(n["r"], o["r"]), f"{model}:{n['path']} U{n['r']} S{o['r']}")
        st.add("localScale", close(n["s"], o["s"], 2e-4), f"{model}:{n['path']} U{n['s']} S{o['s']}")
        if "bones" in n:
            st.add("m_Bones", n["bones"] == o.get("bones"), f"{model}:{n['path']} U{n['bones'][:4]} S{(o.get('bones') or [])[:4]}")
            st.add("m_RootBone", n["rootBone"] == o.get("rootBone"), f"{model}:{n['path']} U{n['rootBone']} S{o.get('rootBone')}")


def match_vertices(u, s):
    """Unity 정점 i -> sim 정점 j. 같은 값의 sim 정점이 이미 쓰였으면 재사용하고 dup 로 센다."""
    V = lambda m, i: m["vertices"][i * 3:i * 3 + 3]
    N = lambda m, i: m["normals"][i * 3:i * 3 + 3]
    UV = lambda m, i: m["uv0"][i * 2:i * 2 + 2] if "uv0" in m else []
    use_t = "tangents" in u and "tangents" in s and USE_TANGENT_MATCH
    T = lambda m, i: m["tangents"][i * 4:i * 4 + 4]
    ext = max([abs(x) for x in u["vertices"]] + [1e-3])
    ptol = max(2e-5, ext * 2e-6)
    step = ptol * 4
    cells = defaultdict(list)
    for j in range(s["vertexCount"]):
        cells[tuple(math.floor(x / step) for x in V(s, j))].append(j)
    used = set()
    mapping, dup, miss = [None] * u["vertexCount"], 0, 0

    def deltas(m):
        out = defaultdict(list)
        fid = 0
        for b in m.get("blendShapes", []):
            for f in b["frames"]:
                d = f["dv"]
                for k in range(0, len(d), 4):
                    out[d[k]].append((fid, d[k + 1:k + 4]))
                fid += 1
        return out
    du_all, ds_all = None, None

    def shape_dist(i, j):
        nonlocal du_all, ds_all
        if du_all is None:
            du_all, ds_all = deltas(u), deltas(s)
        a = {k: v for k, v in du_all.get(i, [])}
        b = {k: v for k, v in ds_all.get(j, [])}
        return sum(sum((x - y) ** 2 for x, y in zip(a.get(k, [0, 0, 0]), b.get(k, [0, 0, 0]))) for k in set(a) | set(b))
    for i in range(u["vertexCount"]):
        p, n, t = V(u, i), N(u, i), UV(u, i)
        c0 = [math.floor(x / step) for x in p]
        cands = [j for dx in (-1, 0, 1) for dy in (-1, 0, 1) for dz in (-1, 0, 1)
                 for j in cells.get((c0[0] + dx, c0[1] + dy, c0[2] + dz), ())]
        good = [j for j in cands
                if all(abs(a - b) <= ptol for a, b in zip(p, V(s, j)))
                and all(abs(a - b) <= 2e-3 for a, b in zip(n, N(s, j)))
                and all(abs(a - b) <= 2e-4 for a, b in zip(t, UV(s, j)))
                and (not use_t or (T(u, i)[3] == T(s, j)[3] and all(abs(a - b) <= 2e-3 for a, b in zip(T(u, i)[:3], T(s, j)[:3]))))]
        free = [j for j in good if j not in used]
        if len(free) > 1:
            # 같은 자리에 겹친 정점(윗·아랫입술 등)은 위치 차이와 블렌드쉐이프 델타가 가장 가까운 쪽과 짝짓는다
            free.sort(key=lambda j: sum((a - b) ** 2 for a, b in zip(p, V(s, j))) + shape_dist(i, j))
        if free:
            mapping[i] = free[0]
            used.add(free[0])
        elif good:
            mapping[i] = good[0]
            dup += 1
        else:
            miss += 1
    return mapping, dup, miss, s["vertexCount"] - len(used)


def cmp_mesh(u, s, st, model):
    tag = f"{model}:{u['name']}"
    st.add("정점 수", u["vertexCount"] == s["vertexCount"], f"{tag} U{u['vertexCount']} S{s['vertexCount']}")
    mapping, dup, miss, s_unused = match_vertices(u, s)
    st.c["정점 전체 (Unity)"] += u["vertexCount"]
    st.c["정점 일치"] += u["vertexCount"] - dup - miss
    st.c["정점 Unity만 중복(분리)"] += dup
    st.c["정점 대응 없음"] += miss
    st.c["정점 sim만 남음(병합 안 됨)"] += s_unused
    if miss:
        st.notes["정점 대응 없음"].append(f"{tag} {miss}/{u['vertexCount']}") if len(st.notes["정점 대응 없음"]) < 6 else None
    if "tangents" in u:
        st.add("탄젠트 존재", "tangents" in s, tag)
        if "tangents" in s:
            ok = bad = 0
            for i, j in enumerate(mapping):
                if j is None:
                    continue
                a, b = u["tangents"][i * 4:i * 4 + 4], s["tangents"][j * 4:j * 4 + 4]
                if a[3] == b[3] and all(abs(x - y) <= 2e-3 for x, y in zip(a[:3], b[:3])):
                    ok += 1
                else:
                    bad += 1
                    if len(st.notes["탄젠트"]) < 6:
                        st.notes["탄젠트"].append(f"{tag} v{i} U{[round(x, 4) for x in a]} S{[round(x, 4) for x in b]}")
            st.c["탄젠트 일치"] += ok
            st.c["탄젠트 불일치"] += bad
    for ch in ("uv1", "uv2", "uv3", "colors"):
        st.add(f"{ch} 존재", (ch in u) == (ch in s), f"{tag} U{ch in u} S{ch in s}")

    # 삼각형 (감김 방향 유지)
    st.add("서브메시 수", len(u["submeshes"]) == len(s["submeshes"]), f"{tag} U{len(u['submeshes'])} S{len(s['submeshes'])}")
    for si, (a, b) in enumerate(zip(u["submeshes"], s["submeshes"])):
        def canon(t):
            r = t.index(min(t))
            return tuple(t[r:] + t[:r])
        tb = Counter(canon(list(b["triangles"][i:i + 3])) for i in range(0, len(b["triangles"]), 3))
        ok = flip = 0
        n = len(a["triangles"]) // 3
        for i in range(0, len(a["triangles"]), 3):
            m = [mapping[a["triangles"][i + k]] for k in range(3)]
            if None in m:
                continue
            if tb[canon(m)] > 0:
                tb[canon(m)] -= 1
                ok += 1
            elif tb[canon([m[0], m[2], m[1]])] > 0:
                flip += 1
        st.c["삼각형 일치"] += ok
        st.c["삼각형 전체"] += n
        st.c["삼각형 감김 반대"] += flip
        if ok != n and len(st.notes["삼각형"]) < 6:
            st.notes["삼각형"].append(f"{tag}[{si}] 일치 {ok}/{n} (sim {len(b['triangles']) // 3}, 감김 반대 {flip})")

    # 스킨
    if "boneWeights" in u:
        bw_u, bw_s = u["boneWeights"], s.get("boneWeights") or []
        ok = bad = 0
        for i, j in enumerate(mapping):
            if j is None or not bw_s:
                continue
            pu = sorted((int(bw_u[i * 8 + k * 2]), round(bw_u[i * 8 + k * 2 + 1], 3)) for k in range(4) if bw_u[i * 8 + k * 2 + 1] > 0)
            ps = sorted((int(bw_s[j * 8 + k * 2]), round(bw_s[j * 8 + k * 2 + 1], 3)) for k in range(4) if bw_s[j * 8 + k * 2 + 1] > 0)
            same = len(pu) == len(ps) and all(x[0] == y[0] and abs(x[1] - y[1]) <= 0.002 for x, y in zip(pu, ps))
            ok += same
            bad += not same
            if not same and len(st.notes["본 가중치"]) < 6:
                st.notes["본 가중치"].append(f"{tag} v{i} U{pu} S{ps}")
        st.c["본 가중치 OK"] += ok
        st.c["본 가중치 NG"] += bad
        bu, bs = u["bindposes"], s.get("bindposes") or []
        nb = len(bu) // 16
        st.add("바인드포즈 수", len(bu) == len(bs), f"{tag} U{nb} S{len(bs) // 16}")
        for b in range(min(nb, len(bs) // 16)):
            st.add("바인드포즈", close(bu[b * 16:b * 16 + 16], bs[b * 16:b * 16 + 16], 5e-4),
                   f"{tag} bone{b} U{[round(x, 4) for x in bu[b * 16:b * 16 + 16]]} S{[round(x, 4) for x in bs[b * 16:b * 16 + 16]]}")

    # 블렌드쉐이프
    su = [b["name"] for b in u["blendShapes"]]
    ss = [b["name"] for b in s["blendShapes"]]
    st.add("블렌드쉐이프 이름·순서", su == ss, f"{tag} U{su[:5]} S{ss[:5]}")
    if su == ss and u["blendShapes"]:
        ext = max([abs(x) for x in u["vertices"]] + [1e-3])
        for bu_, bs_ in zip(u["blendShapes"], s["blendShapes"]):
            st.add("프레임 수·가중치", [f["weight"] for f in bu_["frames"]] == [f["weight"] for f in bs_["frames"]],
                   f"{tag}:{bu_['name']} U{[f['weight'] for f in bu_['frames']]} S{[f['weight'] for f in bs_['frames']]}")
            for fu, fs in zip(bu_["frames"], bs_["frames"]):
                for field, label, tol in (("dv", "델타 정점", max(2e-5, ext * 4e-6)), ("dn", "델타 노멀", 2e-2)):
                    du = {fu[field][k]: fu[field][k + 1:k + 4] for k in range(0, len(fu[field]), 4)}
                    ds = {fs[field][k]: fs[field][k + 1:k + 4] for k in range(0, len(fs[field]), 4)}
                    # Unity 정점마다: 매칭된 sim 정점의 값 (없으면 0)
                    ok = total = 0
                    for i, j in enumerate(mapping):
                        if j is None:
                            continue
                        a, b = du.get(i, [0, 0, 0]), ds.get(j, [0, 0, 0])
                        if a == [0, 0, 0] and b == [0, 0, 0]:
                            continue
                        total += 1
                        ok += all(abs(x - y) <= tol for x, y in zip(a, b))
                    st.c[f"{label} 일치"] += ok
                    st.c[f"{label} 전체"] += total
                    if ok != total and len(st.notes[label]) < 6:
                        st.notes[label].append(f"{tag}:{bu_['name']} 일치 {ok}/{total}")


def main():
    udir, sdir = sys.argv[1], sys.argv[2]
    st = Stats()
    for f in sorted(glob.glob(os.path.join(udir, "*.json"))):
        u = json.load(open(f, encoding="utf-8"))
        if ONLY and not any(o in u["path"] for o in ONLY):
            continue
        sf = os.path.join(sdir, os.path.basename(f))
        if not os.path.exists(sf):
            st.add("모델 존재", False, u["path"])
            continue
        s = json.load(open(sf, encoding="utf-8"))
        model = os.path.basename(u["path"])
        cmp_nodes(u, s, st, model)
        sm = {m["name"]: m for m in s["meshes"]}
        for m in u["meshes"]:
            if m["name"] not in sm:
                st.add("메시 존재", False, f"{model}:{m['name']}")
                continue
            cmp_mesh(m, sm[m["name"]], st, model)
    for k in sorted(st.c):
        print(f"{k:30} {st.c[k]}")
    for k, notes in st.notes.items():
        print(f"\n[{k}]")
        for n in notes[: (6 if VERBOSE else 3)]:
            print("   ", n)


if __name__ == "__main__":
    main()
