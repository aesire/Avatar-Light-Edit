// 아이템(의상·헤어·악세서리)을 더하거나 빼서 IR을 다시 만든 뒤, 지금까지의 Changeset을 새 IR에 옮긴다.
// 머티리얼(GUID)·렌더러(컴포넌트 fileID) 주소는 그대로 쓰고, 노드 번호는 노드 source(파일 guid + GameObject fileID)로 다시 찾는다.
// 새 IR에 없는 대상(뺀 아이템의 노드·렌더러·머티리얼)의 덮어쓰기는 버린다.
using System.Collections.Generic;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public static class Recompose
    {
        public static JNode RemapChangeset(JNode changeset, IrScene from, IrScene to)
        {
            var nodeBySource = new Dictionary<string, int>();
            foreach (var n in to.Nodes) if (n.Source != null) nodeBySource[Key(n.Source)] = n.Id;
            var arr = JNode.Arr();
            foreach (var op in changeset.Items)
            {
                if (op["op"].AsString != "set") continue;
                Addr a;
                try { a = Addr.Parse(op["a"].AsString); } catch (System.FormatException) { continue; }
                switch (a.Kind)
                {
                    case AddrKind.NodeActive:
                    case AddrKind.NodeTransform:
                        {
                            if (!int.TryParse(a.Target, out var old) || old < 0 || old >= from.Nodes.Count || from.Nodes[old].Source == null) continue;
                            if (!nodeBySource.TryGetValue(Key(from.Nodes[old].Source!), out var now)) continue;
                            // 옮긴 자리는 같은 부모 기준일 때만 뜻이 같다 (붙일 곳을 바꾸면 버린다)
                            if (a.Kind == AddrKind.NodeTransform && ParentKey(from, old) != ParentKey(to, now)) continue;
                            a = a.Kind == AddrKind.NodeActive ? Addr.NodeActive(now) : Addr.NodeTransform(now);
                            break;
                        }
                    case AddrKind.BlendShape:
                    case AddrKind.RendererEnabled:
                        if (to.FindRenderer(a.Target) == null) continue;
                        break;
                    case AddrKind.Toggles:   // 묶음 안의 노드 번호를 새 IR로 (없어진 노드는 묶음에서 뺀다)
                        {
                            var list = JNode.Arr();
                            foreach (var t in op["v"].Items)
                            {
                                var nodes = new List<int>();
                                foreach (var x in t["nodes"].Items)
                                {
                                    int old = x.AsInt;
                                    if (old < 0 || old >= from.Nodes.Count || from.Nodes[old].Source == null) continue;
                                    if (nodeBySource.TryGetValue(Key(from.Nodes[old].Source!), out var now)) nodes.Add(now);
                                }
                                list.Add(ToggleSet.Make(t["name"].AsString, nodes));
                            }
                            arr.Add(JNode.Obj().Set("op", "set").Set("a", a.ToString()).Set("v", list));
                            continue;
                        }
                    case AddrKind.ExpressionKeys:
                    case AddrKind.ShapeKeys:   // 렌더러 id는 다시 만들어도 같다
                    case AddrKind.Environment:
                    case AddrKind.AvatarTransform:
                    case AddrKind.Expression:   // 렌더러 id(컴포넌트 fileID)는 다시 만들어도 같다
                    case AddrKind.FaceMap:
                    case AddrKind.Pose:       // 머슬 값이라 아바타 노드와 무관
                        break;
                    case AddrKind.MeshEdit:   // 메시 id = 메시 에셋 guid:fileID. 정점 번호가 맞아야 하므로 정점 수까지 같을 때만
                        {
                            var m0 = from.FindMesh(a.Target); var m1 = to.FindMesh(a.Target);
                            if (m0 == null || m1 == null || m0.VertexCount != m1.VertexCount) continue;
                            break;
                        }
                    default:
                        if (a.IsMaterial && to.FindMaterial(a.Target) == null) continue;
                        break;
                }
                arr.Add(JNode.Obj().Set("op", "set").Set("a", a.ToString()).Set("v", op["v"]));
            }
            return arr;
        }

        static string Key(SourceRef s) => s.Guid + ":" + s.FileId;

        static string ParentKey(IrScene s, int node)
        {
            int p = s.Nodes[node].Parent;
            return p < 0 ? "" : s.Nodes[p].Source is SourceRef r ? Key(r) : s.Nodes[p].Name;
        }
    }
}
