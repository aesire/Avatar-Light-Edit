using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ImportSim
{
    public sealed class FbxNode
    {
        public string Name = "";
        public List<object> Props = new List<object>();
        public List<FbxNode> Children = new List<FbxNode>();
        public FbxNode? Find(string name) => Children.Find(c => c.Name == name);
        public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

        /// Properties70 의 P 값 (이름 다음 4번째부터)
        public List<object>? P70(string name)
        {
            var p70 = Find("Properties70");
            if (p70 == null) return null;
            foreach (var p in p70.Children)
                if (p.Props.Count > 0 && p.Props[0] as string == name) return p.Props.GetRange(4, p.Props.Count - 4);
            return null;
        }

        public Vec3 P70Vec(string name, Vec3 def)
        {
            var v = P70(name);
            return v != null && v.Count >= 3 ? new Vec3(Num(v[0]), Num(v[1]), Num(v[2])) : def;
        }

        public double P70Num(string name, double def)
        {
            var v = P70(name);
            return v != null && v.Count >= 1 ? Num(v[0]) : def;
        }

        public static double Num(object o) => o switch { double d => d, long l => l, bool b => b ? 1 : 0, _ => 0 };

        /// 이 노드의 첫 속성이 배열이면 (int[] / double[] / long[])
        public T[]? Array<T>(string child) => Find(child)?.Props.Count > 0 ? Find(child)!.Props[0] as T[] : null;
    }

    public sealed class FbxObject
    {
        public long Id;
        public string Cls = "", Name = "", SubType = "";
        public FbxNode Node = null!;
    }

    /// <summary>최소 FBX 바이너리 파서. 구조(Objects/Connections)만 읽고, 배열은 지정한 노드만 해제한다.</summary>
    public sealed class FbxScene
    {
        public int Version;
        public FbxNode? GlobalSettings;
        public readonly Dictionary<long, FbxObject> Objects = new Dictionary<long, FbxObject>();
        public readonly List<(long child, long parent)> OO = new List<(long, long)>();
        /// 객체 → 속성 연결 (텍스처 → 머티리얼의 DiffuseColor 등)
        public readonly List<(long child, long parent, string prop)> OP = new List<(long, long, string)>();
        readonly Dictionary<long, List<long>> childrenOf = new Dictionary<long, List<long>>();

        static readonly byte[] Magic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0");
        /// 이 이름의 노드만 배열을 해제한다 (나머지는 길이만)
        static readonly HashSet<string> StructureArrays = new HashSet<string> { "Materials" };
        static readonly HashSet<string> GeometryArrays = new HashSet<string>
        {
            "Materials", "Vertices", "PolygonVertexIndex", "Normals", "NormalsIndex", "UV", "UVIndex", "Colors", "ColorIndex",
            "Indexes", "Weights", "Transform", "TransformLink", "FullWeights", "Smoothing",
        };

        public IReadOnlyList<long> ChildrenOf(long id) =>
            childrenOf.TryGetValue(id, out var l) ? l : (IReadOnlyList<long>)Array.Empty<long>();

        public IEnumerable<FbxObject> ChildObjects(long id, string cls)
        {
            foreach (var c in ChildrenOf(id))
                if (Objects.TryGetValue(c, out var o) && o.Cls == cls) yield return o;
        }

        /// geometry=false 면 구조만 (머티리얼 인덱스 배열만 해제), true 면 정점·스킨·셰이프 배열까지
        public static FbxScene Load(byte[] data, bool geometry = false)
        {
            for (int i = 0; i < Magic.Length; i++)
                if (data.Length <= i || data[i] != Magic[i])
                    throw new InvalidDataException("FBX 바이너리가 아님 (ASCII FBX는 미지원)");
            var s = new FbxScene { Version = BitConverter.ToInt32(data, 23) };
            var root = new Reader(data, s.Version >= 7500, geometry ? GeometryArrays : StructureArrays).ReadTop();

            s.GlobalSettings = root.Find("GlobalSettings");
            var objects = root.Find("Objects");
            if (objects != null)
                foreach (var n in objects.Children)
                    if (n.Props.Count >= 3 && n.Props[0] is long id)
                        s.Objects[id] = new FbxObject
                        {
                            Id = id, Cls = n.Name, Name = ObjName((string)n.Props[1]), SubType = (string)n.Props[2], Node = n
                        };
            var conns = root.Find("Connections");
            if (conns != null)
                foreach (var c in conns.Children)
                    if (c.Name == "C" && (string)c.Props[0] == "OO")
                    {
                        long child = (long)c.Props[1], parent = (long)c.Props[2];
                        s.OO.Add((child, parent));
                        if (!s.childrenOf.TryGetValue(parent, out var l)) s.childrenOf[parent] = l = new List<long>();
                        l.Add(child);
                    }
                    else if (c.Name == "C" && (string)c.Props[0] == "OP" && c.Props.Count >= 4)
                        s.OP.Add(((long)c.Props[1], (long)c.Props[2], c.Props[3] as string ?? ""));
            return s;
        }

        static string ObjName(string s)
        {
            int i = s.IndexOf("\0\u0001", StringComparison.Ordinal);
            return i >= 0 ? s.Substring(0, i) : s;
        }

        sealed class Reader
        {
            readonly byte[] d; readonly bool wide; readonly HashSet<string> arrayNodes; int pos;
            public Reader(byte[] data, bool wide, HashSet<string> arrayNodes) { d = data; this.wide = wide; this.arrayNodes = arrayNodes; pos = 27; }

            public FbxNode ReadTop()
            {
                var root = new FbxNode();
                while (pos < d.Length) { var n = ReadNode(); if (n == null) break; root.Children.Add(n); }
                return root;
            }

            long U() { long v = wide ? BitConverter.ToInt64(d, pos) : BitConverter.ToUInt32(d, pos); pos += wide ? 8 : 4; return v; }

            FbxNode? ReadNode()
            {
                long end = U(), nprops = U(); U();
                int nlen = d[pos++];
                if (end == 0) return null;
                var node = new FbxNode { Name = Encoding.UTF8.GetString(d, pos, nlen) };
                pos += nlen;
                bool arrays = arrayNodes.Contains(node.Name);
                for (long i = 0; i < nprops; i++) node.Props.Add(ReadProp(arrays));
                while (pos < end) { var c = ReadNode(); if (c == null) break; node.Children.Add(c); }
                pos = (int)end;
                return node;
            }

            object ReadProp(bool arrays)
            {
                char t = (char)d[pos++];
                switch (t)
                {
                    case 'Y': pos += 2; return (long)BitConverter.ToInt16(d, pos - 2);
                    case 'C': return d[pos++] != 0;
                    case 'I': pos += 4; return (long)BitConverter.ToInt32(d, pos - 4);
                    case 'F': pos += 4; return (double)BitConverter.ToSingle(d, pos - 4);
                    case 'D': pos += 8; return BitConverter.ToDouble(d, pos - 8);
                    case 'L': pos += 8; return BitConverter.ToInt64(d, pos - 8);
                    case 'S':
                    case 'R':
                        {
                            int n = BitConverter.ToInt32(d, pos); pos += 4;
                            object v = t == 'S' ? Encoding.UTF8.GetString(d, pos, n) : (object)new ArraySegment<byte>(d, pos, n);
                            pos += n; return v;
                        }
                    default:
                        {
                            int count = BitConverter.ToInt32(d, pos), enc = BitConverter.ToInt32(d, pos + 4), clen = BitConverter.ToInt32(d, pos + 8);
                            pos += 12;
                            int start = pos; pos += clen;
                            if (!arrays) return count;
                            int elem = t == 'd' || t == 'l' ? 8 : t == 'f' || t == 'i' ? 4 : 1;
                            var raw = enc == 1 ? Inflate(d, start, clen, count * elem) : new ArraySegment<byte>(d, start, clen).ToArray();
                            switch (t)
                            {
                                case 'i': { var a = new int[count]; Buffer.BlockCopy(raw, 0, a, 0, count * 4); return a; }
                                case 'l': { var a = new long[count]; Buffer.BlockCopy(raw, 0, a, 0, count * 8); return a; }
                                case 'd': { var a = new double[count]; Buffer.BlockCopy(raw, 0, a, 0, count * 8); return a; }
                                case 'f': { var f = new float[count]; Buffer.BlockCopy(raw, 0, f, 0, count * 4); var a = new double[count]; for (int k = 0; k < count; k++) a[k] = f[k]; return a; }
                                default: { var a = new int[count]; for (int k = 0; k < count; k++) a[k] = raw[k]; return a; }
                            }
                        }
                }
            }

            static byte[] Inflate(byte[] src, int off, int len, int outLen)
            {
                // zlib 헤더 2바이트를 건너뛰고 deflate 스트림으로 푼다
                using var ms = new MemoryStream(src, off + 2, len - 2);
                using var z = new DeflateStream(ms, CompressionMode.Decompress);
                var outBuf = new byte[outLen];
                int got = 0;
                while (got < outLen) { int r = z.Read(outBuf, got, outLen - got); if (r <= 0) break; got += r; }
                return outBuf;
            }
        }
    }
}
