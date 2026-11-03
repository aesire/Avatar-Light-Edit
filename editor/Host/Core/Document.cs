// Original(IR, 읽기 전용) + Changeset(덮어쓰기 연산) = Document. PLAN.md 1장 원칙.
// - 원본과 같은 값으로 설정하면 덮어쓰기를 지운다 → "원본과 다른 값"이 항상 정확하다.
// - 모든 Set은 Undo 단위(Step)로 기록된다. Group()으로 여러 Set을 한 단계로 묶는다.
// - Preview()는 기록하지 않고 렌더러에만 보낸다 (슬라이더 드래그 중).
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    /// <summary>Unity가 보고한 머티리얼의 현재 셰이더와 프로퍼티 스키마.</summary>
    public sealed class MaterialSchema
    {
        public string Guid = "", Shader = "";
        public bool Supported;
        /// <summary>같은 셰이더를 쓰는 머티리얼끼리 같은 목록을 나눠 쓴다 (읽기 전용으로 다룬다).</summary>
        public List<ShaderPropInfo> Props = new List<ShaderPropInfo>();
        Dictionary<string, ShaderPropInfo>? _byName;

        public ShaderPropInfo? Find(string name)
        {
            if (_byName == null)
            {
                _byName = new Dictionary<string, ShaderPropInfo>(Props.Count);
                foreach (var p in Props) if (!_byName.ContainsKey(p.Name)) _byName[p.Name] = p;
            }
            return _byName.TryGetValue(name, out var v) ? v : null;
        }

        /// <summary>materialInfo 한 개 (schema를 직접 가짐) 또는 sceneLoaded의 materials 항목 (schemas 표에서 셰이더 이름으로 찾음).</summary>
        public static MaterialSchema FromJson(JNode n, JNode? schemas = null, Dictionary<string, List<ShaderPropInfo>>? parsed = null)
        {
            var s = new MaterialSchema { Guid = n["guid"].AsString, Shader = n["shader"].AsString, Supported = n["supported"].AsBool };
            if (n.Has("schema")) { s.Props = ParseProps(n["schema"]); return s; }
            if (schemas == null || !s.Supported) return s;
            if (parsed != null && parsed.TryGetValue(s.Shader, out var shared)) { s.Props = shared; return s; }
            s.Props = ParseProps(schemas[s.Shader]);
            if (parsed != null) parsed[s.Shader] = s.Props;
            return s;
        }

        /// <summary>sceneLoaded의 머티리얼 전부. 스키마는 셰이더마다 한 번만 읽는다.</summary>
        public static List<MaterialSchema> ReadAll(JNode sceneLoaded)
        {
            var parsed = new Dictionary<string, List<ShaderPropInfo>>();
            var list = new List<MaterialSchema>();
            foreach (var m in sceneLoaded["materials"].Items) list.Add(FromJson(m, sceneLoaded["schemas"], parsed));
            return list;
        }

        static List<ShaderPropInfo> ParseProps(JNode arr)
        {
            var l = new List<ShaderPropInfo>(arr.Count);
            foreach (var p in arr.Items) l.Add(ShaderPropInfo.FromJson(p));
            return l;
        }
    }

    public sealed class Document
    {
        sealed class Change { public Addr Addr; public JNode? Before, After; } // null = 덮어쓰기 없음(원본)
        sealed class Step { public string Label = ""; public List<Change> Changes = new List<Change>(); }

        public IrScene Original { get; }
        public string IrPath { get; }
        readonly Dictionary<Addr, JNode> _overrides = new Dictionary<Addr, JNode>();
        readonly Dictionary<string, MaterialSchema> _schemas = new Dictionary<string, MaterialSchema>();
        readonly Dictionary<string, string> _originalShader = new Dictionary<string, string>();
        readonly List<Step> _undo = new List<Step>(), _redo = new List<Step>();
        Step? _group; int _groupDepth;
        // 저장한 시점의 실행 취소 맨 위 단계 (null = 빈 기록). 실행 취소로 그 시점에 돌아오면 다시 깨끗하다
        Step? _savedTop; bool _forceDirty;

        public const int MaxUndo = 500;

        /// <summary>값이 바뀐 주소들 (Set / Undo / Redo / Load). UI 갱신과 렌더러 동기화가 구독한다.</summary>
        public event Action<IReadOnlyList<Addr>>? Changed;
        /// <summary>기록하지 않는 미리보기 값.</summary>
        public event Action<Addr, JNode>? Previewed;
        public event Action? SchemasChanged;

        public Document(IrScene original, string irPath) { Original = original; IrPath = irPath; }

        public IReadOnlyDictionary<Addr, JNode> Overrides => _overrides;
        public bool IsDirty => _forceDirty || Top != _savedTop || _group != null && _group.Changes.Count > 0;
        public void MarkSaved() { _savedTop = Top; _forceDirty = false; }
        /// <summary>값은 그대로지만 저장할 것이 생김 (아이템을 바꿔 IR을 다시 만든 뒤: 세션의 IR 경로가 바뀐다).</summary>
        public void MarkUnsaved() => _forceDirty = true;
        Step? Top => _undo.Count > 0 ? _undo[_undo.Count - 1] : null;

        // ---------------- 스키마 (Unity 보고) ----------------
        public void SetSchema(MaterialSchema s, bool isInitialLoad) => SetSchemas(new[] { s }, isInitialLoad);

        /// <summary>여러 머티리얼을 한 번에 (sceneLoaded). 변경 알림은 한 번만.</summary>
        public void SetSchemas(IEnumerable<MaterialSchema> list, bool isInitialLoad)
        {
            foreach (var s in list)
            {
                _schemas[s.Guid] = s;
                if (isInitialLoad || !_originalShader.ContainsKey(s.Guid)) _originalShader[s.Guid] = s.Shader;
            }
            SchemasChanged?.Invoke();
        }
        public MaterialSchema? Schema(string matGuid) => _schemas.TryGetValue(matGuid, out var s) ? s : null;
        /// <summary>렌더러가 스키마를 한 번이라도 보냈는지 (장면을 불러온 뒤)</summary>
        public bool HasSchemas => _schemas.Count > 0;

        // ---------------- 값 조회 ----------------
        public JNode Get(Addr a) => _overrides.TryGetValue(a, out var v) ? v : GetOriginal(a);
        public bool IsModified(Addr a) => _overrides.ContainsKey(a);
        public bool IsMaterialModified(string guid) => _overrides.Keys.Any(k => k.IsMaterial && k.Target == guid);

        public static JNode DefaultEnvironment() =>
            JNode.Arr().Add(JNode.Obj().Set("provider", "UnityLighting").Set("version", 1).Set("params", JNode.Obj().Set("preset", "bright")));

        public JNode GetOriginal(Addr a)
        {
            switch (a.Kind)
            {
                case AddrKind.Environment: return DefaultEnvironment();
                case AddrKind.Pose: return JNode.Null;
                case AddrKind.Expression: return JNode.Null;
                case AddrKind.FaceMap: return JNode.Obj();
                case AddrKind.Toggles: case AddrKind.ExpressionKeys: case AddrKind.ShapeKeys: return JNode.Arr();
                case AddrKind.MeshEdit: return JNode.Arr();
                case AddrKind.AvatarTransform: return JNode.FloatArray(Addr.Identity);
                case AddrKind.NodeTransform:
                    {
                        int id = int.Parse(a.Target);
                        if (id < 0 || id >= Original.Nodes.Count) return JNode.FloatArray(new float[] { 0, 0, 0, 0, 0, 0, 1, 1, 1, 1 });
                        var n = Original.Nodes[id];
                        return JNode.FloatArray(n.Position.Concat(n.Rotation).Concat(n.Scale).ToArray());
                    }
                case AddrKind.NodeActive:
                    {
                        int id = int.Parse(a.Target);
                        return id >= 0 && id < Original.Nodes.Count ? JNode.From(Original.Nodes[id].Active) : JNode.From(true);
                    }
                case AddrKind.RendererEnabled: return JNode.From(Original.FindRenderer(a.Target)?.Enabled ?? true);
                case AddrKind.BlendShape:
                    {
                        var r = Original.FindRenderer(a.Target);
                        var m = r == null ? null : Original.FindMesh(r.Mesh);
                        if (r == null || m == null) return JNode.From(0);
                        int i = m.BlendShapeIndex(a.Name);
                        return JNode.From(i >= 0 && i < r.BlendShapeWeights.Count ? r.BlendShapeWeights[i] : 0f);
                    }
            }

            var mat = Original.FindMaterial(a.Target);
            var prop = Schema(a.Target)?.Find(a.Name);
            switch (a.Kind)
            {
                case AddrKind.MatShader:
                    return _originalShader.TryGetValue(a.Target, out var sh) ? JNode.From(sh) : JNode.From(mat?.ShaderName ?? "");
                case AddrKind.MatRenderQueue: return JNode.From(mat?.RenderQueue ?? -1);
                case AddrKind.MatKeyword: return JNode.From(mat != null && mat.Keywords.Contains(a.Name));
                case AddrKind.MatFloat:
                    if (mat != null && mat.Floats.TryGetValue(a.Name, out var f)) return JNode.From(f);
                    return JNode.From(prop?.Default[0] ?? 0f);
                case AddrKind.MatColor:
                case AddrKind.MatVector:
                    if (mat != null && mat.Colors.TryGetValue(a.Name, out var c)) return JNode.FloatArray(Pad4(c));
                    return JNode.FloatArray(prop?.Default ?? new float[] { 0, 0, 0, 0 });
                case AddrKind.MatTex:
                    if (mat != null && mat.TexEnvs.TryGetValue(a.Name, out var t)) return JNode.From(t.Texture);
                    return JNode.Null;
                case AddrKind.MatTexST:
                    if (mat != null && mat.TexEnvs.TryGetValue(a.Name, out var t2))
                        return JNode.FloatArray(new[] { t2.Scale[0], t2.Scale[1], t2.Offset[0], t2.Offset[1] });
                    return JNode.FloatArray(new float[] { 1, 1, 0, 0 });
            }
            return JNode.Null;
        }

        /// <summary>텍스처 키 → IR 텍스처, 없으면 이 문서에서 더한 텍스처(Addr.TexAsset, 임포트 설정은 like를 따름). 둘 다 없으면 null.</summary>
        public IrTexture? Texture(string? key)
        {
            if (key == null) return null;
            if (Original.FindTexture(key) is IrTexture t) return t;
            var v = Get(Addr.TexAsset(key));
            if (v.IsNull) return null;
            var like = Original.FindTexture(v["like"].AsString);
            return new IrTexture { Guid = key, Path = v["path"].AsString, File = v["file"].AsString, Importer = like?.Importer ?? new IrTextureImporter() };
        }

        static float[] Pad4(float[] c) => c.Length >= 4 ? c : c.Concat(Enumerable.Repeat(c.Length == 3 ? 1f : 0f, 4 - c.Length)).ToArray();

        // ---------------- 편집 ----------------
        public void Preview(Addr a, JNode value) => Previewed?.Invoke(a, value);

        public void Set(Addr a, JNode value, string label = "")
        {
            JNode? before = _overrides.TryGetValue(a, out var b) ? b : null;
            JNode? after = Values.Equal(value, GetOriginal(a)) ? null : value;
            if (before == null && after == null) { Previewed?.Invoke(a, Get(a)); return; } // 미리보기를 원래대로 되돌림
            if (before != null && after != null && Values.Equal(before, after)) { Previewed?.Invoke(a, Get(a)); return; }

            Write(a, after);
            using (Group(label))
            {
                // 같은 그룹 안에서 같은 주소를 여러 번 바꾸면 하나로 합친다
                var existing = _group!.Changes.Find(c => c.Addr.Equals(a));
                if (existing != null) existing.After = after;
                else _group.Changes.Add(new Change { Addr = a, Before = before, After = after });
                if (_group.Label.Length == 0) _group.Label = label;
            }
            _redo.Clear();
            Raise(new[] { a });
        }

        public void Reset(Addr a, string label = "") => Set(a, GetOriginal(a), label.Length > 0 ? label : "리셋");

        /// <summary>조건에 맞는 덮어쓰기를 한 단계로 전부 지운다 (머티리얼 전체 리셋 등).</summary>
        public void ResetWhere(Func<Addr, bool> pred, string label)
        {
            var keys = _overrides.Keys.Where(pred).ToList();
            if (keys.Count == 0) return;
            using (Group(label)) foreach (var k in keys) Set(k, GetOriginal(k), label);
        }

        void Write(Addr a, JNode? v)
        {
            if (v == null) _overrides.Remove(a); else _overrides[a] = v;
        }

        public IDisposable Group(string label)
        {
            if (_groupDepth++ == 0) _group = new Step { Label = label };
            return new GroupScope(this);
        }

        sealed class GroupScope : IDisposable
        {
            Document? _d; public GroupScope(Document d) { _d = d; }
            public void Dispose()
            {
                var d = _d; _d = null; if (d == null) return;
                if (--d._groupDepth > 0) return;
                var step = d._group!; d._group = null;
                step.Changes.RemoveAll(c => c.Before == null && c.After == null || c.Before != null && c.After != null && Values.Equal(c.Before, c.After));
                if (step.Changes.Count == 0) return;
                d._undo.Add(step);
                if (d._undo.Count > MaxUndo) d._undo.RemoveAt(0);
            }
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public string? UndoLabel => _undo.Count > 0 ? _undo[_undo.Count - 1].Label : null;
        public string? RedoLabel => _redo.Count > 0 ? _redo[_redo.Count - 1].Label : null;

        public void Undo()
        {
            if (_undo.Count == 0 || _groupDepth > 0) return;
            var s = _undo[_undo.Count - 1]; _undo.RemoveAt(_undo.Count - 1);
            for (int i = s.Changes.Count - 1; i >= 0; i--) Write(s.Changes[i].Addr, s.Changes[i].Before);
            _redo.Add(s);
            Replay(s.Changes.Select(c => c.Addr).ToList());
        }

        public void Redo()
        {
            if (_redo.Count == 0 || _groupDepth > 0) return;
            var s = _redo[_redo.Count - 1]; _redo.RemoveAt(_redo.Count - 1);
            foreach (var c in s.Changes) Write(c.Addr, c.After);
            _undo.Add(s);
            Replay(s.Changes.Select(c => c.Addr).ToList());
        }

        void Raise(IReadOnlyList<Addr> addrs) => Changed?.Invoke(addrs);

        void Replay(IReadOnlyList<Addr> addrs)
        {
            Replaying = true;
            try { Raise(addrs); } finally { Replaying = false; }
        }

        /// <summary>Changed가 실행 취소·다시 실행·불러오기에서 온 것이면 true (Changed 안에서 따라 바꾸는 값을 또 기록하지 않게).</summary>
        public bool Replaying { get; private set; }

        /// <summary>
        /// 방금 기록한 단계에 값 하나를 덧붙인다 (Changed 안에서, 바뀐 값을 따라가는 값 — 예: 자동 Blink Fix). 실행 취소하면 함께 돌아간다.
        /// 그룹 안이면 그 그룹에, 기록이 없으면 새 단계로.
        /// </summary>
        public void Amend(Addr a, JNode value)
        {
            if (Replaying) return;
            if (_groupDepth > 0 || _undo.Count == 0) { Set(a, value); return; }
            JNode? before = _overrides.TryGetValue(a, out var b) ? b : null;
            JNode? after = Values.Equal(value, GetOriginal(a)) ? null : value;
            if (before == null && after == null || before != null && after != null && Values.Equal(before, after)) return;
            Write(a, after);
            var step = _undo[_undo.Count - 1];
            var existing = step.Changes.Find(c => c.Addr.Equals(a));
            if (existing != null) existing.After = after; else step.Changes.Add(new Change { Addr = a, Before = before, After = after });
            Raise(new[] { a });
        }

        // ---------------- 직렬화 (Changeset = 덮어쓰기 연산 목록) ----------------
        public JNode ChangesetToJson()
        {
            var arr = JNode.Arr();
            // 셰이더 교체를 먼저 적어 두면 재생 시 스키마가 맞는다
            foreach (var kv in _overrides.OrderBy(k => k.Key.Kind == AddrKind.MatShader ? 0 : 1))
                arr.Add(JNode.Obj().Set("op", "set").Set("a", kv.Key.ToString()).Set("v", kv.Value));
            return arr;
        }

        /// <summary>
        /// 실행 취소 기록 없이 덮어쓰기를 통째로 바꾸고 앞의 Changeset을 돌려준다 (비교 시트: 변형을 잠깐 그렸다가 되돌린다).
        /// 돌려받은 것으로 다시 부르면 원래대로 — 실행 취소 기록·저장 상태는 그대로 맞는다. Changed는 불러오기처럼 Replaying으로 나간다.
        /// </summary>
        public JNode SwapChangeset(JNode ops)
        {
            var before = ChangesetToJson();
            var touched = new List<Addr>(_overrides.Keys);
            _overrides.Clear();
            foreach (var op in ops.Items)
            {
                if (op["op"].AsString != "set") continue;
                var a = Addr.Parse(op["a"].AsString);
                _overrides[a] = op["v"];
                touched.Add(a);
            }
            Replay(touched.Distinct().ToList());
            return before;
        }

        // ---------------- 변형 (비교 시트, PLAN 1 "Variants : Document 분기") ----------------
        // 지금 상태의 Changeset을 이름을 붙여 저장해 둔 것. 같은 IR 위의 값(머티리얼·블렌드쉐이프·토글·자세·표정·환경 등)만 — 아이템을 붙이고 빼면 IR이 바뀌어 맞지 않는다.
        // Kind "shapes" = 블렌드쉐이프 세팅 (ShapeSets.cs, 2026-10-23): 블렌드쉐이프 값만 + 얼굴 사진(PNG base64). 빈 Kind = 예전 비교 시트의 변형 (읽고 그대로 남김)
        public sealed class Variant
        {
            public string Name = "", Kind = "", Id = "", Photo = "", Time = "";
            public JNode Changeset = JNode.Arr();

            public JNode ToJson()
            {
                var j = JNode.Obj().Set("name", Name).Set("changeset", Changeset);
                if (Kind.Length > 0) j.Set("kind", Kind);
                if (Id.Length > 0) j.Set("id", Id);
                if (Photo.Length > 0) j.Set("photo", Photo);
                if (Time.Length > 0) j.Set("time", Time);
                return j;
            }

            public static Variant FromJson(JNode v) => new Variant
            {
                Name = v["name"].AsString, Kind = v["kind"].AsString, Id = v["id"].AsString, Photo = v["photo"].AsString, Time = v["time"].AsString,
                Changeset = v["changeset"].Clone(),
            };
        }
        public readonly List<Variant> Variants = new List<Variant>();

        public JNode VariantsToJson()
        {
            var arr = JNode.Arr();
            foreach (var v in Variants) arr.Add(v.ToJson());
            return arr;
        }

        public void LoadVariants(JNode arr)
        {
            Variants.Clear();
            foreach (var v in arr.Items) if (v["changeset"].Kind == JKind.Array) Variants.Add(Variant.FromJson(v));
        }

        /// <summary>저장된 Changeset을 적용한다. Undo 기록은 비운다 (세션 불러오기).</summary>
        public void LoadChangeset(JNode ops)
        {
            var touched = new List<Addr>(_overrides.Keys);
            _overrides.Clear();
            foreach (var op in ops.Items)
            {
                if (op["op"].AsString != "set") continue;
                var a = Addr.Parse(op["a"].AsString);
                _overrides[a] = op["v"];
                touched.Add(a);
            }
            _undo.Clear(); _redo.Clear();
            _savedTop = null; _forceDirty = false;
            Replay(touched.Distinct().ToList());
        }
    }
}
