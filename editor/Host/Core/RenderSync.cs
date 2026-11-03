// Document → Unity 명령 변환. Unity는 상태를 갖지 않는 렌더 서버이므로
// 여기서 보내는 명령은 항상 "현재 유효값"이다 (멱등). 재기동 복원도 같은 경로를 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public interface IRenderSink { void Send(JNode msg); bool IsConnected { get; } }

    public sealed class RenderSync
    {
        readonly Document _doc;
        readonly IRenderSink _sink;

        public RenderSync(Document doc, IRenderSink sink)
        {
            _doc = doc; _sink = sink;
            _doc.Changed += OnChanged;
            _doc.Previewed += (a, v) => Send(a, v);
        }

        void OnChanged(IReadOnlyList<Addr> addrs)
        {
            // 셰이더가 바뀐 머티리얼은 셰이더 → 그 머티리얼의 나머지 덮어쓰기 순으로 다시 보낸다
            var shaderChanged = new HashSet<string>(addrs.Where(a => a.Kind == AddrKind.MatShader).Select(a => a.Target));
            foreach (var guid in shaderChanged) SendMaterial(guid);
            foreach (var a in addrs)
                if (!(a.IsMaterial && shaderChanged.Contains(a.Target))) Send(a, _doc.Get(a));
        }

        void SendMaterial(string guid)
        {
            var sh = Addr.MatShader(guid);
            Send(sh, _doc.Get(sh));
            foreach (var kv in _doc.Overrides.Where(k => k.Key.IsMaterial && k.Key.Target == guid && k.Key.Kind != AddrKind.MatShader).ToList())
                Send(kv.Key, kv.Value);
        }

        /// <summary>sceneLoaded 직후 호출: 원본과 다른 값 전부를 보낸다.</summary>
        public void SendAllOverrides()
        {
            var env = Addr.Environment;
            Send(env, _doc.Get(env));
            // 셰이더를 먼저 (프로퍼티가 새 셰이더에 맞게), 더한 텍스처는 그보다 먼저 (머티리얼이 가리키기 전에 렌더러가 알게)
            foreach (var kv in _doc.Overrides.OrderBy(k => k.Key.Kind == AddrKind.TexAsset ? -1 : k.Key.Kind == AddrKind.MatShader ? 0 : 1).ToList())
                if (kv.Key.Kind != AddrKind.Environment) Send(kv.Key, kv.Value);
        }

        void Send(Addr a, JNode v)
        {
            if (!_sink.IsConnected) return;
            var m = ToMessage(a, v);
            if (m != null) _sink.Send(m);
        }

        public static JNode? ToMessage(Addr a, JNode v)
        {
            switch (a.Kind)
            {
                case AddrKind.MatFloat: return MatProp(a, Msg.KindFloat, v);
                case AddrKind.MatColor: return MatProp(a, Msg.KindColor, v);
                case AddrKind.MatVector: return MatProp(a, Msg.KindVector, v);
                case AddrKind.MatTex: return MatProp(a, Msg.KindTexture, v);
                case AddrKind.MatTexST: return MatProp(a, Msg.KindTexST, v);
                case AddrKind.MatKeyword: return Msg.Make(Msg.SetMatKeyword).Set("mat", a.Target).Set("keyword", a.Name).Set("on", v.AsBool);
                case AddrKind.MatRenderQueue: return Msg.Make(Msg.SetMatRenderQueue).Set("mat", a.Target).Set("value", v.AsInt);
                case AddrKind.MatShader: return Msg.Make(Msg.SetMatShader).Set("mat", a.Target).Set("shader", v.AsString);
                case AddrKind.BlendShape: return Msg.Make(Msg.SetBlendShape).Set("renderer", a.Target).Set("name", a.Name).Set("weight", v.AsFloat);
                case AddrKind.NodeActive: return Msg.Make(Msg.SetNodeActive).Set("node", int.Parse(a.Target)).Set("active", v.AsBool);
                case AddrKind.RendererEnabled: return Msg.Make(Msg.SetRendererEnabled).Set("renderer", a.Target).Set("enabled", v.AsBool);
                case AddrKind.Environment: return Msg.Make(Msg.SetEnvironment).Set("providers", v);
                case AddrKind.Pose: return Msg.Make(Msg.SetPose).Set("pose", v);
                case AddrKind.MeshEdit: return Msg.Make(Msg.SetMeshEdits).Set("mesh", a.Target).Set("ops", v);
                case AddrKind.NodeTransform: return Msg.Make(Msg.SetNodeTransform).Set("node", int.Parse(a.Target)).Set("trs", v);
                case AddrKind.AvatarTransform: return Msg.Make(Msg.SetAvatarTransform).Set("trs", v);
                case AddrKind.FaceMap: return Msg.Make(Msg.FaceTracking).Set("map", v);
                case AddrKind.TexAsset: return v.IsNull ? null : Msg.Make(Msg.AddTexture).Set("key", a.Target).Set("file", v["file"]).Set("path", v["path"]).Set("like", v["like"]);
                case AddrKind.Expression:
                    {
                        var arr = JNode.Arr();
                        foreach (var (r, s, w) in FaceExpression.Shapes(v)) arr.Add(JNode.Obj().Set("renderer", r).Set("shape", s).Set("weight", w));
                        return Msg.Make(Msg.SetExpression).Set("shapes", arr);
                    }
            }
            return null;
        }

        static JNode MatProp(Addr a, string kind, JNode v) =>
            Msg.Make(Msg.SetMatProp).Set("mat", a.Target).Set("prop", a.Name).Set("kind", kind).Set("value", v);
    }
}
