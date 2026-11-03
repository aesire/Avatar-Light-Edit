// Unity 메시 에셋(.asset, 클래스 43 텍스트 직렬화) → SimMesh (2026-10-23).
// UniVRM·메시 굽기 도구 등이 만든 메시다. FBX와 달리 Unity가 저장한 결과가 그대로 있으므로 계산하지 않고 읽기만 한다:
//   정점 데이터 m_VertexData._typelessdata, 인덱스 m_IndexBuffer, 서브메시 m_SubMeshes, 블렌드쉐이프 m_Shapes, m_BindPose.
// 정점 데이터: 채널 14개(위치·노멀·탄젠트·색·UV0~7·뼈 가중치·뼈 번호)가 stream마다 정점 단위로 섞여 있고(interleaved),
// stream은 0번부터 차례로 이어지며 각 stream의 시작은 16바이트에 맞춘다 (Body.baked: 40·32·24바이트 × 7251 + 8 = m_DataSize).
// 읽지 않는 것 (오류로 알림): 압축 메시(m_MeshCompression ≠ 0), 바깥 데이터(m_StreamData.path), 바이너리 직렬화 에셋, 삼각형이 아닌 서브메시.
// 큰 메시는 수십 MB·백만 줄이 넘어서 일반 YAML 파서 대신 이 형식만 줄 단위로 읽는다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ImportSim
{
    public static class MeshAsset
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        /// <summary>파일 안의 메시 (fileID → 메시). 읽지 못한 메시는 errors에 이유를 남긴다.</summary>
        public static Dictionary<long, SimMesh> Parse(byte[] data, List<string> errors)
        {
            var result = new Dictionary<long, SimMesh>();
            var text = Encoding.UTF8.GetString(data);
            if (!text.StartsWith("%YAML")) { errors.Add("바이너리로 저장된 에셋은 읽지 않음"); return result; }
            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("--- !u!43 &")) continue;
                var head = lines[i].Substring(11);
                int sp = head.IndexOf(' ');
                long id = long.Parse(sp < 0 ? head : head.Substring(0, sp), C);
                int end = i + 1;
                while (end < lines.Length && !lines[end].StartsWith("--- ")) end++;
                try
                {
                    var (m, err) = ParseOne(lines, i + 1, end);
                    if (m != null) result[id] = m; else errors.Add($"메시 {id}: {err}");
                }
                catch (Exception e) { errors.Add($"메시 {id}: {e.Message}"); }
                i = end - 1;
            }
            return result;
        }

        // ---------------- 메시 하나 ----------------

        sealed class Channel { public int Stream, Offset, Format, Dim; }
        sealed class Shape { public int First, Count; public bool Normals, Tangents; }

        static (SimMesh?, string?) ParseOne(string[] lines, int start, int end)
        {
            // 맨 위 칸 ("  m_이름: 값") 의 줄 번호
            var top = new Dictionary<string, int>();
            for (int i = start; i < end; i++)
            {
                var l = lines[i];
                if (l.Length > 3 && l[0] == ' ' && l[1] == ' ' && l[2] != ' ' && l[2] != '-')
                {
                    int c = l.IndexOf(':');
                    if (c > 2) top[l.Substring(2, c - 2)] = i;
                }
            }
            int Sec(string k) => top.TryGetValue(k, out var i) ? i : -1;
            string TopVal(string k) => Sec(k) is int i && i >= 0 ? Val(lines[i]) : "";
            int SecEnd(int from) { int i = from + 1; while (i < end && (lines[i].StartsWith("   ") || lines[i].StartsWith("  -"))) i++; return i; }

            if (TopVal("m_MeshCompression") is var mc && mc.Length > 0 && mc != "0") return (null, "압축 메시는 읽지 않음 (m_MeshCompression " + mc + ")");
            int sd = Sec("m_StreamData");
            if (sd >= 0)
                for (int i = sd + 1; i < SecEnd(sd); i++)
                    if (lines[i].TrimStart().StartsWith("path:") && Val(lines[i]).Length > 0) return (null, "바깥 데이터 파일을 쓰는 메시는 읽지 않음");

            var m = new SimMesh { Name = TopVal("m_Name") };

            // 정점 데이터
            int vd = Sec("m_VertexData");
            if (vd < 0) return (null, "m_VertexData 없음");
            int vdEnd = SecEnd(vd), vcount = 0, version = 3;
            var channels = new List<Channel>();
            byte[] vdata = Array.Empty<byte>();
            for (int i = vd + 1; i < vdEnd; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("serializedVersion:")) version = Int(Val(t));
                else if (t.StartsWith("m_VertexCount:")) vcount = Int(Val(t));
                else if (t.StartsWith("- stream:"))
                {
                    var ch = new Channel { Stream = Int(Val(t)) };
                    for (int k = i + 1; k < vdEnd && !lines[k].TrimStart().StartsWith("-") && lines[k].StartsWith("      "); k++)
                    {
                        var u = lines[k].TrimStart();
                        if (u.StartsWith("offset:")) ch.Offset = Int(Val(u));
                        else if (u.StartsWith("format:")) ch.Format = Int(Val(u));
                        else if (u.StartsWith("dimension:")) ch.Dim = Int(Val(u)) & 0xF;
                    }
                    channels.Add(ch);
                }
                else if (t.StartsWith("_typelessdata:")) vdata = Hex(Val(t));
            }
            if (version < 3)
                foreach (var ch in channels) ch.Format = ch.Format switch { 2 => 2, 3 => 6, 4 => 10, var f => f };   // 예전 형식: Float·Float16·Color(UNorm8)·Byte·UInt32
            if (channels.Count < 14) channels.AddRange(Enumerable.Range(0, 14 - channels.Count).Select(_ => new Channel()));

            // stream마다 한 정점의 크기 → stream 시작 위치 (16바이트 정렬)
            int streams = channels.Where(c => c.Dim > 0).Select(c => c.Stream + 1).DefaultIfEmpty(0).Max();
            var stride = new int[streams]; var streamStart = new int[streams];
            foreach (var c in channels) if (c.Dim > 0) stride[c.Stream] = Math.Max(stride[c.Stream], c.Offset + Size(c.Format) * c.Dim);
            for (int s = 0, pos = 0; s < streams; s++) { streamStart[s] = pos; pos += stride[s] * vcount; pos = (pos + 15) & ~15; }
            if (streams > 0 && streamStart[streams - 1] + stride[streams - 1] * vcount > vdata.Length) return (null, "정점 데이터가 짧음");

            double Read(Channel c, int v, int k)
            {
                int at = streamStart[c.Stream] + v * stride[c.Stream] + c.Offset + k * Size(c.Format);
                return c.Format switch
                {
                    0 => BitConverter.ToSingle(vdata, at),
                    1 => Half(BitConverter.ToUInt16(vdata, at)),
                    2 => vdata[at] / 255.0,
                    3 => Math.Max(-1, (sbyte)vdata[at] / 127.0),
                    4 => BitConverter.ToUInt16(vdata, at) / 65535.0,
                    5 => Math.Max(-1, BitConverter.ToInt16(vdata, at) / 32767.0),
                    6 => vdata[at],
                    7 => (sbyte)vdata[at],
                    8 => BitConverter.ToUInt16(vdata, at),
                    9 => BitConverter.ToInt16(vdata, at),
                    10 => BitConverter.ToUInt32(vdata, at),
                    11 => BitConverter.ToInt32(vdata, at),
                    _ => 0,
                };
            }
            Vec3 V3(Channel c, int v) => new Vec3(Read(c, v, 0), c.Dim > 1 ? Read(c, v, 1) : 0, c.Dim > 2 ? Read(c, v, 2) : 0);

            var pos3 = channels[0]; if (pos3.Dim == 0) return (null, "정점 위치 없음");
            for (int v = 0; v < vcount; v++) m.Vertices.Add(V3(pos3, v));
            if (channels[1].Dim > 0) for (int v = 0; v < vcount; v++) m.Normals.Add(V3(channels[1], v));
            if (channels[2].Dim == 4)
            {
                m.Tangents = new List<float[]>(vcount);
                for (int v = 0; v < vcount; v++) m.Tangents.Add(new[] { (float)Read(channels[2], v, 0), (float)Read(channels[2], v, 1), (float)Read(channels[2], v, 2), (float)Read(channels[2], v, 3) });
            }
            if (channels[3].Dim > 0)
            {
                var c = channels[3];
                m.Colors = new List<double[]>(vcount);
                for (int v = 0; v < vcount; v++) m.Colors.Add(Enumerable.Range(0, 4).Select(k => k < c.Dim ? Read(c, v, k) : 1.0).ToArray());
            }
            int lastUv = Enumerable.Range(4, 8).Where(ci => channels[ci].Dim > 0).DefaultIfEmpty(3).Max();
            for (int ci = 4; ci <= lastUv; ci++)
            {
                var uv = new List<(double, double)>();
                var c = channels[ci];
                if (c.Dim > 0) for (int v = 0; v < vcount; v++) uv.Add((Read(c, v, 0), c.Dim > 1 ? Read(c, v, 1) : 0));
                m.Uvs.Add(uv);
            }
            Channel bw = channels[12], bi = channels[13];
            if (bi.Dim > 0)
            {
                m.BoneIndices = new List<int[]>(vcount); m.BoneWeights = new List<double[]>(vcount);
                for (int v = 0; v < vcount; v++)
                {
                    var idx = new int[4]; var w = new double[4];
                    for (int k = 0; k < 4; k++)
                    {
                        if (k < bi.Dim) idx[k] = (int)Read(bi, v, k);
                        // 뼈 하나뿐이면 가중치 채널이 없을 수 있다 (가중치 1)
                        w[k] = bw.Dim == 0 ? (k == 0 ? 1 : 0) : k < bw.Dim ? Read(bw, v, k) : 0;
                    }
                    m.BoneIndices.Add(idx); m.BoneWeights.Add(w);
                }
            }

            // 인덱스·서브메시
            bool wide = TopVal("m_IndexFormat") == "1";
            var ib = Hex(TopVal("m_IndexBuffer"));
            int sm = Sec("m_SubMeshes");
            if (sm >= 0)
            {
                int smEnd = SecEnd(sm);
                for (int i = sm + 1; i < smEnd; i++)
                {
                    if (!lines[i].StartsWith("  - ")) continue;
                    int firstByte = 0, count = 0, topo = 0, baseVertex = 0;
                    for (int k = i; k < smEnd && (k == i || !lines[k].StartsWith("  - ")); k++)
                    {
                        var u = lines[k].Substring(4);
                        if (u.StartsWith("firstByte:")) firstByte = Int(Val(u));
                        else if (u.StartsWith("indexCount:")) count = Int(Val(u));
                        else if (u.StartsWith("topology:")) topo = Int(Val(u));
                        else if (u.StartsWith("baseVertex:")) baseVertex = Int(Val(u));
                    }
                    if (topo != 0) return (null, $"삼각형이 아닌 서브메시 (topology {topo})");
                    var tri = new List<int>(count);
                    for (int k = 0; k < count; k++)
                    {
                        int at = firstByte + k * (wide ? 4 : 2);
                        if (at + (wide ? 4 : 2) > ib.Length) return (null, "인덱스 데이터가 짧음");
                        tri.Add((wide ? (int)BitConverter.ToUInt32(ib, at) : BitConverter.ToUInt16(ib, at)) + baseVertex);
                    }
                    m.Submeshes.Add(tri);
                }
            }

            // 바인드포즈: e{행}{열}
            int bp = Sec("m_BindPose");
            if (bp >= 0)
            {
                int bpEnd = SecEnd(bp);
                Mat4? cur = null;
                for (int i = bp + 1; i < bpEnd; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("- ")) { cur = new Mat4(); m.Bindposes.Add(cur); t = t.Substring(2); }
                    if (cur != null && t.Length > 4 && t[0] == 'e' && char.IsDigit(t[1]) && char.IsDigit(t[2]) && t[3] == ':')
                        cur.M[t[1] - '0', t[2] - '0'] = Num(Val(t));
                }
            }

            ReadShapes(lines, Sec("m_Shapes"), SecEnd, m);
            return (m, null);
        }

        /// m_Shapes: vertices(델타 목록) · shapes(프레임마다 vertices 구간) · channels(이름 · 프레임 구간) · fullWeights(프레임 무게)
        static void ReadShapes(string[] lines, int sec, Func<int, int> secEnd, SimMesh m)
        {
            if (sec < 0) return;
            int end = secEnd(sec);
            var verts = new List<(int index, Vec3 v, Vec3 n, Vec3 t)>();
            var shapes = new List<Shape>();
            var chans = new List<(string name, int first, int count)>();
            var weights = new List<float>();
            string part = "";
            for (int i = sec + 1; i < end; i++)
            {
                var l = lines[i];
                if (l.StartsWith("    ") && !l.StartsWith("     ") && !l.StartsWith("    -")) { part = l.Trim().Split(':')[0]; continue; }
                var t = l.TrimStart();
                bool item = t.StartsWith("- ");
                if (item) t = t.Substring(2);
                switch (part)
                {
                    case "vertices":
                        if (item) verts.Add((0, default, default, default));
                        if (verts.Count == 0) break;
                        var cur = verts[verts.Count - 1];
                        if (t.StartsWith("vertex:")) cur.v = Flow(Val(t));
                        else if (t.StartsWith("normal:")) cur.n = Flow(Val(t));
                        else if (t.StartsWith("tangent:")) cur.t = Flow(Val(t));
                        else if (t.StartsWith("index:")) cur.index = Int(Val(t));
                        verts[verts.Count - 1] = cur;
                        break;
                    case "shapes":
                        if (item) shapes.Add(new Shape());
                        if (shapes.Count == 0) break;
                        var s = shapes[shapes.Count - 1];
                        if (t.StartsWith("firstVertex:")) s.First = Int(Val(t));
                        else if (t.StartsWith("vertexCount:")) s.Count = Int(Val(t));
                        else if (t.StartsWith("hasNormals:")) s.Normals = Val(t) == "1";
                        else if (t.StartsWith("hasTangents:")) s.Tangents = Val(t) == "1";
                        break;
                    case "channels":
                        if (item) chans.Add(("", 0, 0));
                        if (chans.Count == 0) break;
                        var c = chans[chans.Count - 1];
                        if (t.StartsWith("name:")) c.name = Val(t);
                        else if (t.StartsWith("frameIndex:")) c.first = Int(Val(t));
                        else if (t.StartsWith("frameCount:")) c.count = Int(Val(t));
                        chans[chans.Count - 1] = c;
                        break;
                    case "fullWeights":
                        if (item) weights.Add((float)Num(t));
                        break;
                }
            }
            foreach (var (name, first, count) in chans)
            {
                var bs = new SimBlendShape { Name = name };
                for (int f = first; f < first + count && f < shapes.Count; f++)
                {
                    var s = shapes[f];
                    var fr = new SimBlendShapeFrame { Weight = f < weights.Count ? weights[f] : 100 };
                    for (int k = s.First; k < s.First + s.Count && k < verts.Count; k++)
                    {
                        var (index, v, n, t) = verts[k];
                        fr.DeltaVertices[index] = v;
                        if (s.Normals) fr.DeltaNormals[index] = n;
                        if (s.Tangents) fr.DeltaTangents[index] = t;
                    }
                    bs.Frames.Add(fr);
                }
                m.BlendShapes.Add(bs);
            }
        }

        // ---------------- 값 읽기 ----------------

        static string Val(string line)
        {
            int c = line.IndexOf(':');
            return c < 0 ? "" : line.Substring(c + 1).Trim();
        }

        static int Int(string s) => int.TryParse(s, NumberStyles.Integer, C, out var v) ? v : 0;
        static double Num(string s) => double.TryParse(s, NumberStyles.Float, C, out var v) ? v : 0;

        /// {x: 1, y: 2, z: 3}
        static Vec3 Flow(string s)
        {
            double x = 0, y = 0, z = 0;
            foreach (var part in s.Trim('{', '}').Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                var v = Num(kv[1].Trim());
                switch (kv[0].Trim()) { case "x": x = v; break; case "y": y = v; break; case "z": z = v; break; }
            }
            return new Vec3(x, y, z);
        }

        static byte[] Hex(string s)
        {
            var r = new byte[s.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = (byte)((Nib(s[i * 2]) << 4) | Nib(s[i * 2 + 1]));
            return r;
            static int Nib(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
        }

        /// VertexAttributeFormat의 한 성분 크기 (바이트)
        static int Size(int format) => format switch { 0 or 10 or 11 => 4, 1 or 4 or 5 or 8 or 9 => 2, _ => 1 };

        static double Half(ushort h)
        {
            int sign = h >> 15, exp = (h >> 10) & 0x1F, man = h & 0x3FF;
            double v = exp == 0 ? man / 1024.0 * Math.Pow(2, -14) : exp == 31 ? (man == 0 ? double.PositiveInfinity : double.NaN) : (1 + man / 1024.0) * Math.Pow(2, exp - 15);
            return sign == 1 ? -v : v;
        }
    }
}
