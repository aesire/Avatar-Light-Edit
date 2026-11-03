using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ImportSim
{
    /// <summary>해석된 프리팹을 사람이 읽을 수 있는 트리 JSON 으로.</summary>
    public static class Export
    {
        public static string PrefabJson(SimFile f, AssetDb db, Resolver r)
        {
            var w = new JsonWriter();
            w.Begin('{');
            w.Key("path").Str(f.Path); w.Key("guid").Str(f.Guid);
            w.Key("warnings").Begin('[');
            foreach (var s in f.Warnings) w.Str(s);
            w.End(']');
            w.Key("root");
            var root = f.Root;
            if (root == null) w.Raw("null"); else Node(w, f, db, r, root);
            w.End('}');
            return w.ToString();
        }

        static void Node(JsonWriter w, SimFile f, AssetDb db, Resolver r, SimObject go)
        {
            w.Begin('{');
            w.Key("name").Str(S(go.Props, "m_Name"));
            w.Key("active").Raw(S(go.Props, "m_IsActive") == "0" ? "false" : "true");
            w.Key("components").Begin('[');
            SimObject? tr = null;
            foreach (var c in L(go.Props, "m_Component"))
            {
                var comp = f.Get(Ref.FileId((c as Map)?.GetValueOrDefault("component")));
                if (comp == null) continue;
                if (comp.ClassId == 4 || comp.ClassId == 224) { tr = comp; continue; }
                w.Begin('{');
                w.Key("type").Str(comp.Type);
                if (comp.Props.ContainsKey("m_Enabled")) w.Key("enabled").Raw(S(comp.Props, "m_Enabled") == "0" ? "false" : "true");
                if (comp.Props.TryGetValue("m_Mesh", out var mesh)) { w.Key("mesh"); RefJson(w, mesh, db); }
                if (comp.Props.ContainsKey("m_Materials"))
                {
                    w.Key("materials").Begin('[');
                    foreach (var m in L(comp.Props, "m_Materials")) RefJson(w, m, db);
                    w.End(']');
                }
                if (comp.ClassId == 137)
                {
                    // Unity는 가중치 배열을 메시의 블렌드쉐이프 개수에 맞춘다 (모자라면 0)
                    var weights = L(comp.Props, "m_BlendShapeWeights");
                    var names = r.BlendShapes(comp.Props.GetValueOrDefault("m_Mesh"));
                    int n = names?.Count ?? weights.Count;
                    w.Key("blendShapes").Begin('[');
                    for (int i = 0; i < n; i++)
                    {
                        w.Begin('{');
                        w.Key("name").Str(names != null ? names[i] : "");
                        w.Key("weight").Raw(Num(i < weights.Count ? weights[i] as string : "0"));
                        w.End('}');
                    }
                    w.End(']');
                }
                if (comp.Props.TryGetValue("m_Avatar", out var av)) { w.Key("avatar"); RefJson(w, av, db); }
                if (comp.ClassId == 137)
                {
                    w.Key("rootBone").Str(TransformPath(f, Ref.Local(comp.Props, "m_RootBone")));
                    w.Key("bones").Begin('[');
                    foreach (var b in L(comp.Props, "m_Bones")) w.Str(TransformPath(f, Ref.Guid(b) == null ? Ref.FileId(b) : 0));
                    w.End(']');
                }
                if (comp.Props.TryGetValue("m_Script", out var sc)) { w.Key("script"); RefJson(w, sc, db); }
                w.End('}');
            }
            w.End(']');
            if (tr != null)
            {
                w.Key("p").Raw(Vec(tr.Props, "m_LocalPosition", "x", "y", "z"));
                w.Key("r").Raw(Vec(tr.Props, "m_LocalRotation", "x", "y", "z", "w"));
                w.Key("s").Raw(Vec(tr.Props, "m_LocalScale", "x", "y", "z"));
            }
            w.Key("children").Begin('[');
            if (tr != null)
                foreach (var ch in L(tr.Props, "m_Children"))
                    if (f.Get(Ref.FileId(ch)) is SimObject ct && f.Get(Ref.Local(ct.Props, "m_GameObject")) is SimObject cg)
                        Node(w, f, db, r, cg);
            w.End(']');
            w.End('}');
        }

        static void RefJson(JsonWriter w, object? r, AssetDb db)
        {
            var g = Ref.Guid(r);
            long id = Ref.FileId(r);
            if (id == 0) { w.Raw("null"); return; }
            w.Begin('{');
            w.Key("fileID").Raw(id.ToString(CultureInfo.InvariantCulture));
            if (g != null)
            {
                w.Key("guid").Str(g);
                var a = db.Get(g);
                w.Key("asset").Str(a?.Path ?? (Ref.IsBuiltin(g) ? "Builtin" : "(없음)"));
            }
            w.End('}');
        }

        static string S(Map m, string k) => m.GetValueOrDefault(k) as string ?? "";

        /// 프리팹 루트 기준 경로 (루트 자신은 "", 없으면 "(없음)")
        static string TransformPath(SimFile f, long trId)
        {
            if (trId == 0 || f.Get(trId) == null) return "(없음)";
            var parts = new List<string>();
            for (var t = f.Get(trId); t != null && Ref.Local(t.Props, "m_Father") != 0; t = f.Get(Ref.Local(t.Props, "m_Father")))
                parts.Insert(0, f.Get(Ref.Local(t.Props, "m_GameObject")) is SimObject g ? S(g.Props, "m_Name") : "?");
            return string.Join("/", parts);
        }

        static string Vec(Map props, string key, params string[] comps)
        {
            var m = props.GetValueOrDefault(key) as Map ?? new Map();
            return "[" + string.Join(",", comps.Select(c => double.TryParse(m.GetValueOrDefault(c) as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? ((float)v).ToString("R", CultureInfo.InvariantCulture) : "0")) + "]";
        }
        static List<object?> L(Map m, string k) => m.GetValueOrDefault(k) as List<object?> ?? new List<object?>();
        static string Num(string? s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("R", CultureInfo.InvariantCulture) : "0";
    }

    sealed class JsonWriter
    {
        readonly StringBuilder sb = new StringBuilder();
        readonly Stack<bool> first = new Stack<bool>();
        bool afterKey;

        void Sep()
        {
            if (afterKey) { afterKey = false; return; }
            if (first.Count > 0) { if (!first.Peek()) sb.Append(','); first.Pop(); first.Push(false); }
        }
        public JsonWriter Begin(char c) { Sep(); sb.Append(c); first.Push(true); return this; }
        public JsonWriter End(char c) { first.Pop(); sb.Append(c); return this; }
        public JsonWriter Key(string k) { Sep(); Esc(k); sb.Append(':'); afterKey = true; return this; }
        public JsonWriter Str(string s) { Sep(); Esc(s); return this; }
        public JsonWriter Raw(string s) { Sep(); sb.Append(s); return this; }
        void Esc(string s)
        {
            sb.Append('"');
            foreach (var ch in s)
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch); break;
                }
            sb.Append('"');
        }
        public override string ToString() => sb.ToString();
    }
}
