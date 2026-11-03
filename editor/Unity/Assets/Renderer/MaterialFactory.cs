// IR 머티리얼 → Unity Material (PLAN 3.1).
// 셰이더 결정: 빌드 때 만든 GUID→셰이더 이름 표 → 셰이더 팩 → IR이 알려준 이름 → 잠긴 셰이더의 원본. 모두 없으면 "미지원 셰이더"로 표시하고
// 다른 셰이더로 값을 옮기지 않는다 (표시용 대체 셰이더는 _Color/_MainTex 만 읽음). 원본 값은 Host Document에 있다.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Viewer
{
    public sealed class MaterialFactory
    {
        const string UnsupportedShader = "Hidden/VrcProto/Unsupported";
        readonly Dictionary<string, string> _shaderByGuid = new Dictionary<string, string>();
        readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
        readonly Dictionary<string, bool> _supported = new Dictionary<string, bool>();
        readonly Dictionary<Shader, JNode> _schemaCache = new Dictionary<Shader, JNode>();
        readonly TextureLoader _textures = new TextureLoader();
        /// <summary>장면 로드 때 텍스처 미리 준비(Prefetch)·진행 표시에 쓴다.</summary>
        public TextureLoader Textures => _textures;
        IrScene _scene;
        Action<string> _warn = _ => { };
        Material _missing;

        public MaterialFactory()
        {
            var table = Resources.Load<TextAsset>("VrcProtoShaderTable");
            if (table != null)
                foreach (var line in table.text.Split('\n'))
                {
                    var p = line.Trim().Split('\t');
                    if (p.Length == 2) _shaderByGuid[p[0]] = p[1];
                }
            else Debug.LogWarning("[VrcProto] 셰이더 표(VrcProtoShaderTable)가 빌드에 없습니다");
            LoadShaderPacks();
        }

        /// <summary>스튜디오 소품용: 셰이더 표·팩은 같이 쓰고 머티리얼·텍스처만 따로 (팩 번들을 두 번 열 수 없다).</summary>
        MaterialFactory(MaterialFactory shared)
        {
            _shaderByGuid = shared._shaderByGuid; _packByName = shared._packByName; _packByGuid = shared._packByGuid; _packShaderSet = shared._packShaderSet;
            _aliasGuids = shared._aliasGuids; _variants = shared._variants; _packs = shared._packs; PackLog = shared.PackLog;
        }

        public MaterialFactory ForProps() => new MaterialFactory(this);

        // ---------------- 셰이더 팩 ----------------
        // 빌드에 없는 셰이더는 셰이더 팩(AssetBundle)으로 더한다. 팩 하나 = <이름>.bundle + <이름>.txt(GUID\t셰이더 이름)
        //   [+ <이름>.variants.txt (미리 컴파일한 키워드 조합 목록)].
        //  · 기본 팩: 렌더러와 함께 배포 (StreamingAssets/ShaderPacks — Poiyomi·MToon 등 재배포 가능한 것만, 미리 컴파일)
        //  · 사용자 팩: Host의 "셰이더 팩 추가"·자동 제안이 PC의 같은 버전 Unity로 만든다 (렌더러는 셰이더 소스를 컴파일하지 못함)
        // GUID는 팩마다 그 번들 안의 셰이더로 바로 잇는다 (버전이 다른 같은 이름의 셰이더가 여러 팩에 있을 수 있음). 사용자 팩이 기본 팩보다 우선.
        // 필요할 때만 연다 (2026-10-20): 시작할 때 팩을 모두 열어 셰이더를 올리면 920 MB쯤을 차지했다 (기본 팩 Poiyomi 8.0~10.0 + MToon).
        //   시작할 때는 표(.txt)와 이름 목록 캐시만 읽고, 머티리얼이 그 팩의 셰이더를 찾을 때 번들을 연다.
        //   이름 목록 캐시(%LOCALAPPDATA%\ALEdit\shaderpack-names): 번들 안 셰이더 이름 전부 — 처음 본 번들은 한 번 열어 이름을 적고 바로 닫는다.
        sealed class Pack
        {
            public string File, Label; public AssetBundle Ab; public bool Tried;
            public readonly Dictionary<string, Shader> ByName = new Dictionary<string, Shader>();
        }
        readonly Dictionary<string, Pack> _packByName = new Dictionary<string, Pack>();                    // 셰이더 이름 → 그 이름을 맡은 팩 (나중 팩이 우선)
        readonly Dictionary<string, (Pack Pack, string Name)> _packByGuid = new Dictionary<string, (Pack, string)>();
        readonly HashSet<Shader> _packShaderSet = new HashSet<Shader>();
        readonly HashSet<string> _aliasGuids = new HashSet<string>();
        readonly Dictionary<Shader, VariantSets> _variants = new Dictionary<Shader, VariantSets>();
        readonly List<Pack> _packs = new List<Pack>();
        public readonly List<string> PackLog = new List<string>();

        public static string BundledPackDir => System.IO.Path.Combine(Application.streamingAssetsPath, "ShaderPacks");
        static string NamesCacheDir => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "shaderpack-names");

        /// <summary>미리 컴파일한 팩의 키워드 조합. 머티리얼 키워드에 딱 맞는 조합이 없으면 가장 가까운 것을 고른다.</summary>
        sealed class VariantSets
        {
            public readonly HashSet<string> Features = new HashSet<string>();   // shader_feature 로 선언된 키워드
            public readonly HashSet<string> Enum = new HashSet<string>();       // 그중 열거형 묶음(한 pragma에 여럿)에 든 것
            public readonly HashSet<string> Ignore = new HashSet<string>();     // 미리보기에 영향이 없는 기능: 끄고 근사로 치지 않음
            public readonly List<string[]> Mandatory = new List<string[]>();     // 끄기 없는 묶음: 아무것도 안 켜면 첫 키워드
            public readonly List<HashSet<string>> Sets = new List<HashSet<string>>();
            public bool IsMandatory(string k) { foreach (var g in Mandatory) if (Array.IndexOf(g, k) >= 0) return true; return false; }
        }

        public static string ShaderPackDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("VRCPROTO_SHADERPACKS");
                return !string.IsNullOrEmpty(env) ? env
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "shaderpacks");
            }
        }

        void LoadShaderPacks()
        {
            LoadPackDir(BundledPackDir, "기본 셰이더 팩");
            LoadPackDir(ShaderPackDir, "셰이더 팩"); // 나중에 불러온 것이 우선 (사용자가 자기 Unity로 만든 정확한 팩)
        }

        void LoadPackDir(string dir, string label)
        {
            if (!System.IO.Directory.Exists(dir)) return;
            foreach (var file in System.IO.Directory.GetFiles(dir, "*.bundle"))
            {
                var pack = new Pack { File = file, Label = label };
                var names = PackNames(pack);
                if (names == null) continue;
                _packs.Add(pack);
                foreach (var n in names) _packByName[n] = pack;
                var have = new HashSet<string>(names);
                var table = System.IO.Path.ChangeExtension(file, ".txt");
                if (System.IO.File.Exists(table))
                    foreach (var line in System.IO.File.ReadAllLines(table))
                    {
                        var p = line.Trim().Split('\t');
                        // 빌드에 들어 있는 셰이더(lilToon·Unity 기본)가 우선
                        if (p.Length < 2 || _shaderByGuid.ContainsKey(p[0]) || !have.Contains(p[1])) continue;
                        _packByGuid[p[0]] = (pack, p[1]);
                        // 3번째 칸 "alias": 다른 셰이더(같은 프로퍼티)의 GUID를 이 셰이더로 대신 그림
                        if (p.Length > 2 && p[2] == "alias") _aliasGuids.Add(p[0]); else _aliasGuids.Remove(p[0]);
                    }
                PackLog.Add($"{label} {System.IO.Path.GetFileNameWithoutExtension(file)}: 셰이더 {names.Count}개 (쓸 때 열기)");
            }
        }

        /// <summary>번들 안 셰이더 이름들 (이 PC에서 쓸 수 있는 것만). 캐시가 없으면 번들을 한 번 열어 이름을 적고 닫는다. 열 수 없으면 null.</summary>
        List<string> PackNames(Pack pack)
        {
            var fi = new System.IO.FileInfo(pack.File);
            var cache = System.IO.Path.Combine(NamesCacheDir, $"{System.IO.Path.GetFileNameWithoutExtension(pack.File)}_{fi.Length:x}_{fi.LastWriteTimeUtc.Ticks:x}_{SystemInfo.graphicsDeviceType}.txt");
            try { if (System.IO.File.Exists(cache)) return new List<string>(System.IO.File.ReadAllLines(cache)); } catch { }
            var ab = AssetBundle.LoadFromFile(pack.File);
            if (ab == null) { PackLog.Add($"{pack.Label}을 열지 못함 (다른 Unity 버전으로 만들었을 수 있음): " + System.IO.Path.GetFileName(pack.File)); return null; }
            var names = new List<string>();
            foreach (var s in ab.LoadAllAssets<Shader>())
                if (s.isSupported) names.Add(s.name);
                else PackLog.Add($"이 PC에서 쓸 수 없는 셰이더: {s.name}");
            ab.Unload(true);   // 이름만 적고 닫는다 (셰이더 메모리를 돌려줌)
            try { System.IO.Directory.CreateDirectory(NamesCacheDir); System.IO.File.WriteAllLines(cache, names); } catch { }
            return names;
        }

        /// <summary>팩 번들을 열어 셰이더를 올린다 (한 번만). 변형 목록도 이때 읽는다.</summary>
        void OpenPack(Pack pack)
        {
            if (pack.Tried) return;
            pack.Tried = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            pack.Ab = AssetBundle.LoadFromFile(pack.File);
            if (pack.Ab == null) { Debug.LogWarning($"[VrcProto] {pack.Label}을 열지 못함: {pack.File}"); return; }
            foreach (var s in pack.Ab.LoadAllAssets<Shader>())
                if (s.isSupported) { pack.ByName[s.name] = s; _packShaderSet.Add(s); }
            var variants = System.IO.Path.ChangeExtension(pack.File, ".variants.txt");
            if (System.IO.File.Exists(variants)) LoadVariants(variants, pack.ByName);
            Debug.Log($"[VrcProto] {pack.Label} 열기 {System.IO.Path.GetFileNameWithoutExtension(pack.File)}: 셰이더 {pack.ByName.Count}개 ({sw.ElapsedMilliseconds} ms)");
        }

        Shader PackShader(Pack pack, string name)
        {
            OpenPack(pack);
            return pack.ByName.TryGetValue(name, out var s) ? s : null;
        }

        void LoadVariants(string file, Dictionary<string, Shader> byName)
        {
            foreach (var line in System.IO.File.ReadAllLines(file))
            {
                var p = line.TrimEnd('\r').Split('\t');
                if (p.Length < 2) continue;
                bool features = p[0] == "#features", ignore = p[0] == "#ignore";
                if (!byName.TryGetValue(features || ignore ? p[1] : p[0], out var s)) continue;
                if (!_variants.TryGetValue(s, out var v)) _variants[s] = v = new VariantSets();
                if (ignore) { if (p.Length > 2) v.Ignore.UnionWith(p[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)); continue; }
                var words = (features ? (p.Length > 2 ? p[2] : "") : p[1]).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!features) { v.Sets.Add(new HashSet<string>(words)); continue; }
                foreach (var g in words)
                {
                    var ks = g.TrimStart('!').Split('|');
                    v.Features.UnionWith(ks);
                    if (ks.Length > 1) v.Enum.UnionWith(ks);
                    if (g.StartsWith("!")) v.Mandatory.Add(ks);
                }
            }
        }

        /// <summary>빌드에 들어 있는 셰이더 → 셰이더 팩 순으로 찾는다.</summary>
        Shader FindShader(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var s = Shader.Find(name);
            return s != null ? s : _packByName.TryGetValue(name, out var pack) ? PackShader(pack, name) : null;
        }

        /// <summary>셰이더 GUID(또는 "guid:fileID") → 셰이더. 빌드 표 → 팩 표.</summary>
        Shader FindShaderByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (_shaderByGuid.TryGetValue(guid, out var name)) { var s = FindShader(name); if (s != null) return s; }
            return _packByGuid.TryGetValue(guid, out var p) ? PackShader(p.Pack, p.Name) : null;
        }

        public IEnumerable<string> KnownShaderNames()
        {
            var seen = new HashSet<string>();
            // Hidden/ 은 렌더러 내부용 (미지원 표시·보조 표시) — 셰이더 고르기 목록에 내지 않는다
            // Shader.Find로 확인하지 않는다 (2026-10-23): 표는 빌드가 Always Included에 넣은 셰이더만 적고, Find가 셰이더 82개를 모두 올려 "준비됨"이 0.45초 늦었다.
            // 셰이더는 머티리얼이 실제로 쓸 때 올라간다
            foreach (var n in _shaderByGuid.Values) if (!n.StartsWith("Hidden/") && seen.Add(n)) yield return n;
            foreach (var n in _packByName.Keys) if (!n.StartsWith("Hidden/") && seen.Add(n)) yield return n;
        }

        /// <summary>머티리얼을 모두 내린다. keepTextures면 텍스처는 남긴다 (장면을 다시 불러올 때 — 새 장면에 없는 것은 TextureLoader.EndScene이 내린다).</summary>
        public void Clear(bool keepTextures = false)
        {
            foreach (var m in _mats.Values) if (m != null) UnityEngine.Object.Destroy(m);
            _mats.Clear(); _supported.Clear(); _approx.Clear(); _approxVariant.Clear(); _wanted.Clear(); _lockedKeywords.Clear();
            _texWant.Clear();
            if (!keepTextures) _textures.Clear();
        }

        // 텍스처는 준비되면 넣는다 (2026-10-23: 장면이 먼저 보이고 텍스처는 뒤따른다). 그 사이 같은 자리에 다른 텍스처를 넣었으면 늦게 온 것은 버린다
        readonly Dictionary<(Material, string), string> _texWant = new Dictionary<(Material, string), string>();
        void SetTex(Material m, string prop, IrTexture t)
        {
            var key = (m, prop);
            if (t == null) { _texWant.Remove(key); m.SetTexture(prop, null); return; }
            var want = t.Guid;
            _texWant[key] = want;
            m.SetTexture(prop, _textures.Request(t, _warn, ready =>
            {
                if (m != null && _texWant.TryGetValue(key, out var g) && g == want) m.SetTexture(prop, ready);
            }));
        }

        public void Begin(IrScene scene, Action<string> warn) { _scene = scene; _warn = warn; }

        // 원본과 다르게 보일 수 있는 머티리얼 (잠긴 셰이더를 원본 셰이더로 대신 그림 / 미리 컴파일한 키워드 조합 중 가까운 것으로 그림).
        // Host는 이 머티리얼도 "원본 프로젝트 셰이더로 맞추기" 제안에 넣는다.
        readonly Dictionary<string, string> _approx = new Dictionary<string, string>();
        readonly Dictionary<string, HashSet<string>> _wanted = new Dictionary<string, HashSet<string>>();   // 머티리얼이 원래 켠 키워드
        readonly Dictionary<string, List<string>> _lockedKeywords = new Dictionary<string, List<string>>();

        static readonly System.Text.RegularExpressions.Regex TagRe =
            new System.Text.RegularExpressions.Regex(@"^\s*(OriginalShaderGUID|OriginalShader|OriginalKeywords):[ \t]*(.*?)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// 머티리얼 셰이더 결정. GUID → IR이 알려준 이름 → 잠긴 셰이더의 원본.
        /// Poiyomi(Thry) 잠금은 머티리얼마다 프로젝트 안에 셰이더를 새로 만들고 .mat 태그에 원본 셰이더·키워드를 남긴다
        /// (OriginalShaderGUID / OriginalShader / OriginalKeywords). 그 셰이더가 없으면 원본 셰이더로 대신 그린다.
        /// </summary>
        Shader ResolveShader(IrMaterial ir, string guid)
        {
            _approx.Remove(guid); _lockedKeywords.Remove(guid);
            var s = FindShaderByGuid(ir.ShaderGuid);
            if (s != null)
            {
                if (_aliasGuids.Contains(ir.ShaderGuid) && !_shaderByGuid.ContainsKey(ir.ShaderGuid)) _approx[guid] = $"비슷한 셰이더({s.name})로 대신 표시";
                return s;
            }
            if (!string.IsNullOrEmpty(ir.ShaderName) && (s = FindShader(ir.ShaderName)) != null)
            {
                if (_packShaderSet.Contains(s)) _approx[guid] = "같은 이름의 다른 버전 셰이더로 표시";
                return s;
            }
            if (string.IsNullOrEmpty(ir.RawYaml) || !ir.RawYaml.Contains("OriginalShader")) return null;
            string origGuid = null, origName = null, origKw = null;
            foreach (System.Text.RegularExpressions.Match t in TagRe.Matches(ir.RawYaml))
                switch (t.Groups[1].Value)
                {
                    case "OriginalShaderGUID": origGuid = t.Groups[2].Value; break;
                    case "OriginalShader": origName = t.Groups[2].Value; break;
                    case "OriginalKeywords": origKw = t.Groups[2].Value; break;
                }
            s = FindShaderByGuid(origGuid) ?? FindShader(origName) ?? FindByLooseName(origName);
            if (s == null) return null;
            _approx[guid] = $"잠긴 셰이더 대신 원본 셰이더({s.name})로 표시";
            if (origKw != null) _lockedKeywords[guid] = new List<string>(origKw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return s;
        }

        /// 버전 표기가 다른 같은 셰이더: ".poiyomi/Poiyomi 8.0/Poiyomi Toon" == ".poiyomi/Old Versions/8.0/Poiyomi Toon"
        static readonly System.Text.RegularExpressions.Regex LooseRe = new System.Text.RegularExpressions.Regex(@"/(Old Versions/|Poiyomi )(?=\d)");
        Shader FindByLooseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string Norm(string n) => LooseRe.Replace(n, "/");
            var key = Norm(name);
            foreach (var kv in _packByName) if (Norm(kv.Key) == key) return PackShader(kv.Value, kv.Key);
            return null;
        }

        public Material Get(string guid)
        {
            if (_mats.TryGetValue(guid, out var m)) return m;
            var ir = _scene?.FindMaterial(guid);
            if (ir == null)
            {
                _warn($"머티리얼 {guid}가 IR에 없음");
                if (_missing == null) _missing = new Material(Shader.Find(UnsupportedShader)) { name = "(missing)", color = Color.magenta };
                return _missing;
            }
            var shader = ResolveShader(ir, guid);
            bool supported = shader != null;
            if (!supported)
            {
                _warn($"미지원 셰이더: {ir.Name} (셰이더 GUID {ir.ShaderGuid})");
                shader = Shader.Find(UnsupportedShader);
            }
            else if (_approx.TryGetValue(guid, out var why)) _warn($"{ir.Name}: {why}");
            m = new Material(shader) { name = ir.Name };
            _mats[guid] = m; _supported[guid] = supported;
            ApplyOriginal(m, ir, supported);
            return m;
        }

        /// <summary>
        /// 원하는 키워드를 적용. 미리 컴파일한 팩(.variants.txt가 있는 셰이더)은 딱 맞는 조합이 없으면
        /// 머티리얼 키워드 안에서 가장 많이 맞는 조합을 고른다 (켜기/끄기 기능 키워드 2점, 열거형 1점).
        /// 셰이더에 없는 조합을 켜 두면 Unity가 임의의 가까운 변형을 고르므로 직접 고른다.
        /// </summary>
        void ApplyKeywords(string guid, Material m)
        {
            if (!_wanted.TryGetValue(guid, out var want)) return;
            if (!_variants.TryGetValue(m.shader, out var v)) { m.shaderKeywords = new List<string>(want).ToArray(); return; }
            var feat = new HashSet<string>(want); feat.IntersectWith(v.Features); feat.ExceptWith(v.Ignore);
            foreach (var g in v.Mandatory) if (!Array.Exists(g, feat.Contains)) feat.Add(g[0]); // Unity와 같은 해석
            HashSet<string> best = null; int bestScore = -1;
            foreach (var set in v.Sets)
            {
                if (set.SetEquals(feat)) { best = set; bestScore = int.MaxValue; break; }
                // 머티리얼에 없는 기능을 켜는 조합은 안 된다. 끄기 없는 묶음(조명 방식 등)은 다른 값이어도 되지만 점수가 없다
                int score = 0; bool ok = true;
                foreach (var k in set)
                {
                    if (feat.Contains(k)) score += v.Enum.Contains(k) ? 1 : 2;
                    else if (!v.IsMandatory(k)) { ok = false; break; }
                }
                if (!ok) continue;
                if (score > bestScore) { best = set; bestScore = score; }
            }
            var kws = new List<string>();
            foreach (var k in want) if (!v.Features.Contains(k)) kws.Add(k);
            if (best != null) kws.AddRange(best);
            m.shaderKeywords = kws.ToArray();
            if (bestScore == int.MaxValue) { _approxVariant.Remove(guid); return; }
            var missing = new List<string>(feat); if (best != null) missing.RemoveAll(best.Contains);
            _approxVariant[guid] = "미리 컴파일한 기능 조합에 없어 일부 기능 없이 표시: " + string.Join(" ", missing);
        }
        readonly Dictionary<string, string> _approxVariant = new Dictionary<string, string>();

        /// <summary>원본과 다르게 보일 수 있는 이유 (없으면 null)</summary>
        string ApproxReason(string guid)
            => _approx.TryGetValue(guid, out var a) ? a : _approxVariant.TryGetValue(guid, out var b) ? b : null;

        /// <summary>IR 원본 값 전부를 적용 (.mat 의 m_SavedProperties 와 같은 의미).</summary>
        void ApplyOriginal(Material m, IrMaterial ir, bool supported)
        {
            foreach (var kv in ir.Floats) if (m.HasProperty(kv.Key)) m.SetFloat(kv.Key, kv.Value);
            foreach (var kv in ir.Colors) SetColorOrVector(m, kv.Key, kv.Value);
            foreach (var kv in ir.TexEnvs)
            {
                if (!m.HasProperty(kv.Key)) continue;
                SetTex(m, kv.Key, kv.Value.Texture == null ? null : _scene.FindTexture(kv.Value.Texture));
                m.SetTextureScale(kv.Key, new Vector2(kv.Value.Scale[0], kv.Value.Scale[1]));
                m.SetTextureOffset(kv.Key, new Vector2(kv.Value.Offset[0], kv.Value.Offset[1]));
            }
            if (supported)
            {
                _wanted[ir.Guid] = new HashSet<string>(_lockedKeywords.TryGetValue(ir.Guid, out var lk) ? lk : ir.Keywords);
                ApplyKeywords(ir.Guid, m);
                m.renderQueue = ir.RenderQueue; // -1 = 셰이더 기본
            }
        }

        static void SetColorOrVector(Material m, string prop, float[] v)
        {
            if (!m.HasProperty(prop)) return;
            var vec = new Vector4(v[0], v.Length > 1 ? v[1] : 0, v.Length > 2 ? v[2] : 0, v.Length > 3 ? v[3] : 1);
            int i = m.shader.FindPropertyIndex(prop);
            // Color 형 프로퍼티는 .mat에 감마 값으로 저장되고 SetColor가 Linear 공간 변환을 한다 (Unity 에디터와 같은 동작)
            if (i >= 0 && m.shader.GetPropertyType(i) == ShaderPropertyType.Color) m.SetColor(prop, vec);
            else m.SetVector(prop, vec);
        }

        // ---------------- 편집 명령 ----------------
        public void SetProp(string guid, string prop, string kind, JNode value)
        {
            if (!_mats.TryGetValue(guid, out var m) || !m.HasProperty(prop)) return;
            switch (kind)
            {
                case Msg.KindFloat: m.SetFloat(prop, value.AsFloat); break;
                case Msg.KindColor: case Msg.KindVector: SetColorOrVector(m, prop, value.ToFloatArray()); break;
                case Msg.KindTexture:
                    SetTex(m, prop, value.IsNull ? null : _scene?.FindTexture(value.AsString));
                    break;
                case Msg.KindTexST:
                    {
                        var st = value.ToFloatArray();
                        m.SetTextureScale(prop, new Vector2(st[0], st[1]));
                        m.SetTextureOffset(prop, new Vector2(st[2], st[3]));
                        break;
                    }
            }
        }

        public void SetKeyword(string guid, string kw, bool on)
        {
            if (!_mats.TryGetValue(guid, out var m)) return;
            if (!_wanted.TryGetValue(guid, out var want)) { if (on) m.EnableKeyword(kw); else m.DisableKeyword(kw); return; }
            if (on) want.Add(kw); else want.Remove(kw);
            ApplyKeywords(guid, m);
        }

        public void SetRenderQueue(string guid, int rq) { if (_mats.TryGetValue(guid, out var m) && _supported[guid]) m.renderQueue = rq; }

        /// <summary>셰이더 교체 (lilToon 렌더링 모드 전환). 원본 값을 다시 적용하고 새 스키마를 돌려준다. Host가 이어서 덮어쓰기를 보낸다.</summary>
        public JNode SetShader(string guid, string shaderName)
        {
            if (!_mats.TryGetValue(guid, out var m)) return null;
            var ir = _scene?.FindMaterial(guid);
            Shader s = FindShader(shaderName);
            if (s == null)
            {
                // 빈 이름 = 원본 셰이더로 (원본이 미지원이었던 경우)
                if (ir != null) s = ResolveShader(ir, guid);
                if (s == null) { s = Shader.Find(UnsupportedShader); _supported[guid] = false; }
                else _supported[guid] = true;
            }
            else { _supported[guid] = true; _approx.Remove(guid); }
            if (m.shader != s)
            {
                m.shader = s;
                if (ir != null) ApplyOriginal(m, ir, _supported[guid]);
            }
            return Report(guid, m);
        }

        /// <summary>장면의 머티리얼 전부. 스키마는 셰이더 이름별로 schemas에 한 번만 넣는다 (Poiyomi는 셰이더 하나에 프로퍼티가 천 개가 넘는다).</summary>
        public JNode ReportAll(JNode schemas)
        {
            var arr = JNode.Arr();
            foreach (var kv in _mats) arr.Add(Report(kv.Key, kv.Value, schemas));
            return arr;
        }

        /// schemas가 null이면 스키마를 머티리얼에 바로 넣는다 (materialInfo)
        JNode Report(string guid, Material m, JNode schemas = null)
        {
            bool sup = _supported.TryGetValue(guid, out var s) && s;
            var approx = sup ? ApproxReason(guid) : null;
            var o = JNode.Obj().Set("guid", guid).Set("shader", sup ? m.shader.name : "").Set("supported", sup)
                .Set("approx", approx != null).Set("approxReason", approx ?? "");
            if (schemas == null) o.Set("schema", sup ? Schema(m.shader) : JNode.Arr());
            else if (sup && !schemas.Has(m.shader.name)) schemas.Set(m.shader.name, Schema(m.shader));
            return o;
        }

        /// <summary>셰이더 프로퍼티 스키마 (런타임 Shader API). Host가 이것으로 편집 UI를 만든다.</summary>
        JNode Schema(Shader s)
        {
            if (_schemaCache.TryGetValue(s, out var cached)) return cached;
            var arr = JNode.Arr();
            int n = s.GetPropertyCount();
            for (int i = 0; i < n; i++)
            {
                var p = new ShaderPropInfo { Name = s.GetPropertyName(i), Description = s.GetPropertyDescription(i) };
                var flags = s.GetPropertyFlags(i);
                p.Hidden = (flags & ShaderPropertyFlags.HideInInspector) != 0;
                p.HDR = (flags & ShaderPropertyFlags.HDR) != 0;
                p.Normal = (flags & ShaderPropertyFlags.Normal) != 0;
                p.NoScaleOffset = (flags & ShaderPropertyFlags.NoScaleOffset) != 0;
                p.Attributes.AddRange(s.GetPropertyAttributes(i));
                switch (s.GetPropertyType(i))
                {
                    case ShaderPropertyType.Color:
                        p.Type = "Color"; var c = s.GetPropertyDefaultVectorValue(i); p.Default = new[] { c.x, c.y, c.z, c.w }; break;
                    case ShaderPropertyType.Vector:
                        p.Type = "Vector"; var v = s.GetPropertyDefaultVectorValue(i); p.Default = new[] { v.x, v.y, v.z, v.w }; break;
                    case ShaderPropertyType.Range:
                        p.Type = "Range"; var r = s.GetPropertyRangeLimits(i); p.Min = r.x; p.Max = r.y;
                        p.Default = new[] { s.GetPropertyDefaultFloatValue(i), 0, 0, 0 }; break;
                    case ShaderPropertyType.Float:
                        p.Type = "Float"; p.Default = new[] { s.GetPropertyDefaultFloatValue(i), 0, 0, 0 }; break;
                    case ShaderPropertyType.Int:
                        p.Type = "Int"; p.Default = new[] { (float)s.GetPropertyDefaultIntValue(i), 0, 0, 0 }; break;
                    case ShaderPropertyType.Texture:
                        p.Type = "Texture"; p.DefaultTexture = s.GetPropertyTextureDefaultName(i); p.TexDim = s.GetPropertyTextureDimension(i).ToString(); break;
                }
                arr.Add(p.ToJson());
            }
            _schemaCache[s] = arr;
            return arr;
        }
    }
}
