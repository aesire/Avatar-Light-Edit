// 테스트용 IR 생성기: 임포터의 기하 출력이 준비되기 전까지 에디터를 끝까지 돌려 보기 위한 간단한 아바타 형태.
// 사용: dotnet bin/MakeSampleIr.dll <출력 폴더>
// 결과: sample.ir.json, sample.ir.bin, tex_*.png
// - 본 3개(Hips/Spine/Head)에 스킨된 몸통, 블렌드쉐이프가 있는 머리, 외곽선 lilToon 머리카락,
//   빌드에 없는 셰이더 GUID를 쓰는 악세서리(미지원 셰이더 표시 확인용)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

static class MakeSampleIr
{
    const string LilToon = "df12117ecd77c31469c224178886498e";        // lts.shader (lilToon 2.3.4)
    const string LilToonOutline = "efa77a80ca0344749b4f19fdd5891cbe"; // lts_o.shader
    const string Unknown = "0123456789abcdef0123456789abcdef";       // 빌드에 없는 셰이더

    static int Main(string[] args)
    {
        var dir = Path.GetFullPath(args.Length > 0 ? args[0] : "sample");
        Directory.CreateDirectory(dir);
        WritePng(Path.Combine(dir, "tex_skin.png"), 256, 256, (x, y) => { byte v = (byte)(230 - y / 16); return (255, v, (byte)(v - 20), 255); });
        WritePng(Path.Combine(dir, "tex_hair.png"), 256, 256, (x, y) => { int s = (x / 16 + y / 64) % 2 == 0 ? 0 : 25; return ((byte)(120 + s), (byte)(80 + s), (byte)(160 + s), 255); });
        WritePng(Path.Combine(dir, "tex_cloth.png"), 256, 256, (x, y) => ((x / 32 + y / 32) % 2 == 0 ? ((byte)40, (byte)60, (byte)110, (byte)255) : ((byte)230, (byte)230, (byte)240, (byte)255)));

        var root = JNode.Obj().Set("irVersion", IrScene.CurrentVersion).Set("name", "SampleAvatar").Set("buffers", "sample.ir.bin");
        var nodes = JNode.Arr(); var meshes = JNode.Arr(); var renderers = JNode.Arr();
        int Node(string name, int parent, float x, float y, float z)
        {
            int id = nodes.Count;
            nodes.Add(JNode.Obj().Set("id", id).Set("name", name).Set("parent", parent).Set("position", JNode.FloatArray(new[] { x, y, z }))
                .Set("source", JNode.Obj().Set("guid", "00000000000000000000000000000000").Set("fileID", (1000 + id).ToString())));
            return id;
        }
        int avatar = Node("SampleAvatar", -1, 0, 0, 0);
        int armature = Node("Armature", avatar, 0, 0, 0);
        int hips = Node("Hips", armature, 0, 0.85f, 0);
        int spine = Node("Spine", hips, 0, 0.25f, 0);     // 월드 1.10
        int head = Node("Head", spine, 0, 0.32f, 0);      // 월드 1.42
        var boneWorldY = new[] { 0.85f, 1.10f, 1.42f };
        var bones = new[] { hips, spine, head };

        using (var w = new IrBufferWriter(Path.Combine(dir, "sample.ir.bin")))
        {
            // 몸통: 타원체, 높이에 따라 Hips→Spine 가중치
            var body = Ellipsoid(new[] { 0f, 1.0f, 0f }, new[] { 0.17f, 0.42f, 0.12f }, 32, 24);
            var bodyW = SkinByHeight(body.pos, boneWorldY, new[] { 0, 1 });
            meshes.Add(MeshJson(w, "mesh_body", "Body", body, bodyW, boneWorldY, null));
            renderers.Add(RendererJson("r_body", Node("Body", avatar, 0, 0, 0), "mesh_body", bones, hips, new[] { "mat_body" }, new float[] { 0 }));

            // 머리: 구 + 블렌드쉐이프 (구분선, 표정, 비셈, Shrink)
            var hd = Ellipsoid(new[] { 0f, 1.55f, 0f }, new[] { 0.12f, 0.13f, 0.12f }, 32, 24);
            var shapes = new List<(string, float[])>
            {
                ("=== Face ===", new float[hd.pos.Length]),
                ("smile", Delta(hd.pos, (x, y, z) => z < 0 && y < 1.53f ? new[] { 0f, 0.015f * Math.Abs(x) / 0.12f, 0f } : null)),
                ("eye_close", Delta(hd.pos, (x, y, z) => z < -0.06f && y > 1.56f && y < 1.62f ? new[] { 0f, -(y - 1.56f) * 0.6f, 0f } : null)),
                ("--- Viseme ---", new float[hd.pos.Length]),
                ("vrc.v_aa", Delta(hd.pos, (x, y, z) => z < -0.05f && y < 1.50f ? new[] { 0f, -0.02f, 0f } : null)),
                ("vrc.v_oh", Delta(hd.pos, (x, y, z) => z < -0.05f && y < 1.50f ? new[] { -x * 0.1f, -0.01f, 0f } : null)),
                ("--- Shape ---", new float[hd.pos.Length]),
                ("head_big", Delta(hd.pos, (x, y, z) => new[] { x * 0.15f, (y - 1.55f) * 0.15f, z * 0.15f })),
            };
            var headW = SkinByHeight(hd.pos, boneWorldY, new[] { 2, 2 });
            meshes.Add(MeshJson(w, "mesh_head", "Head", hd, headW, boneWorldY, shapes));
            renderers.Add(RendererJson("r_head", Node("Head", avatar, 0, 0, 0), "mesh_head", bones, head, new[] { "mat_body" }, new float[shapes.Count]));

            // 머리카락: 머리보다 조금 큰 위쪽 반구, 외곽선 셰이더
            var hair = Ellipsoid(new[] { 0f, 1.57f, 0.01f }, new[] { 0.135f, 0.14f, 0.135f }, 32, 24, upperOnly: true);
            meshes.Add(MeshJson(w, "mesh_hair", "Hair", hair, SkinByHeight(hair.pos, boneWorldY, new[] { 2, 2 }), boneWorldY, null));
            renderers.Add(RendererJson("r_hair", Node("Hair", avatar, 0, 0, 0), "mesh_hair", bones, head, new[] { "mat_hair" }, new float[0]));

            // 치마: 아래로 넓어지는 원뿔대
            var skirt = Frustum(0.85f, 0.55f, 0.19f, 0.32f, 32, 8);
            meshes.Add(MeshJson(w, "mesh_skirt", "Skirt", skirt, SkinByHeight(skirt.pos, boneWorldY, new[] { 0, 0 }), boneWorldY, null));
            renderers.Add(RendererJson("r_skirt", Node("Skirt", avatar, 0, 0, 0), "mesh_skirt", bones, hips, new[] { "mat_cloth" }, new float[0]));

            // 악세서리: 미지원 셰이더
            var acc = Ellipsoid(new[] { 0.09f, 1.68f, -0.04f }, new[] { 0.03f, 0.03f, 0.03f }, 12, 8);
            meshes.Add(MeshJson(w, "mesh_acc", "Ribbon", acc, SkinByHeight(acc.pos, boneWorldY, new[] { 2, 2 }), boneWorldY, null));
            renderers.Add(RendererJson("r_acc", Node("Ribbon", avatar, 0, 0, 0), "mesh_acc", bones, head, new[] { "mat_poiyomi" }, new float[0]));
        }

        var mats = JNode.Arr();
        mats.Add(Mat("mat_body", "Assets/Sample/Body.mat", LilToon, "tex_skin", new (string, float)[] { ("_UseShadow", 1), ("_ShadowBorder", 0.5f), ("_Cull", 2) },
            new (string, float[])[] { ("_Color", new[] { 1f, 1f, 1f, 1f }), ("_ShadowColor", new[] { 0.82f, 0.7f, 0.75f, 1f }) }));
        mats.Add(Mat("mat_hair", "Assets/Sample/Hair.mat", LilToonOutline, "tex_hair", new (string, float)[] { ("_OutlineWidth", 0.08f), ("_UseRim", 1), ("_Cull", 0) },
            new (string, float[])[] { ("_Color", new[] { 1f, 1f, 1f, 1f }), ("_OutlineColor", new[] { 0.25f, 0.15f, 0.3f, 1f }) }));
        mats.Add(Mat("mat_cloth", "Assets/Sample/Cloth.mat", LilToon, "tex_cloth", new (string, float)[] { ("_Cull", 0) },
            new (string, float[])[] { ("_Color", new[] { 1f, 1f, 1f, 1f }) }));
        mats.Add(Mat("mat_poiyomi", "Assets/Sample/Ribbon.mat", Unknown, null, new (string, float)[] { ("_Glossiness", 0.5f) },
            new (string, float[])[] { ("_Color", new[] { 0.9f, 0.2f, 0.3f, 1f }) }));

        var texs = JNode.Arr();
        foreach (var t in new[] { "tex_skin", "tex_hair", "tex_cloth" })
            texs.Add(JNode.Obj().Set("guid", t).Set("path", $"Assets/Sample/{t}.png").Set("file", t + ".png")
                .Set("importer", JNode.Obj().Set("sRGB", true).Set("mipmaps", true).Set("maxSize", 2048).Set("wrap", 0).Set("filter", 1)));

        root.Set("nodes", nodes).Set("meshes", meshes).Set("renderers", renderers).Set("materials", mats).Set("textures", texs);
        File.WriteAllText(Path.Combine(dir, "sample.ir.json"), root.ToJson(indent: true));
        var check = IrScene.Load(Path.Combine(dir, "sample.ir.json"));
        Console.WriteLine($"wrote {dir}\\sample.ir.json  nodes={check.Nodes.Count} meshes={check.Meshes.Count} renderers={check.Renderers.Count} materials={check.Materials.Count}");
        return 0;
    }

    sealed class Geo { public float[] pos = null!, nrm = null!, tan = null!, uv = null!; public int[] idx = null!; }

    static Geo Ellipsoid(float[] c, float[] r, int seg, int rings, bool upperOnly = false)
    {
        var pos = new List<float>(); var nrm = new List<float>(); var tan = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
        // v=0 이 위(+Y). 위쪽 반구는 0 ~ rings/2+1
        int ringStart = 0, ringEnd = upperOnly ? rings / 2 + 1 : rings;
        for (int i = ringStart; i <= ringEnd; i++)
        {
            double v = (double)i / rings, th = Math.PI * (1 - v); // 아래(0)→위(π)
            for (int j = 0; j <= seg; j++)
            {
                double u = (double)j / seg, ph = 2 * Math.PI * u;
                double nx = Math.Sin(th) * Math.Cos(ph), ny = -Math.Cos(th), nz = Math.Sin(th) * Math.Sin(ph);
                pos.Add(c[0] + (float)(nx * r[0])); pos.Add(c[1] + (float)(ny * r[1])); pos.Add(c[2] + (float)(nz * r[2]));
                double ex = nx / r[0], ey = ny / r[1], ez = nz / r[2], l = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                nrm.Add((float)(ex / l)); nrm.Add((float)(ey / l)); nrm.Add((float)(ez / l));
                tan.Add((float)-Math.Sin(ph)); tan.Add(0); tan.Add((float)Math.Cos(ph)); tan.Add(-1);
                uv.Add((float)u); uv.Add((float)v);
            }
        }
        int rowLen = seg + 1, rowsN = ringEnd - ringStart + 1;
        for (int i = 0; i < rowsN - 1; i++)
            for (int j = 0; j < seg; j++)
            {
                int a = i * rowLen + j, b = a + 1, cc = a + rowLen, d = cc + 1;
                // Unity는 시계 방향이 앞면 (바깥에서 보아)
                idx.Add(a); idx.Add(cc); idx.Add(b);
                idx.Add(b); idx.Add(cc); idx.Add(d);
            }
        return new Geo { pos = pos.ToArray(), nrm = nrm.ToArray(), tan = tan.ToArray(), uv = uv.ToArray(), idx = idx.ToArray() };
    }

    static Geo Frustum(float yTop, float yBottom, float rTop, float rBottom, int seg, int rings)
    {
        var pos = new List<float>(); var nrm = new List<float>(); var tan = new List<float>(); var uv = new List<float>(); var idx = new List<int>();
        double slope = (rBottom - rTop) / (yTop - yBottom);
        for (int i = 0; i <= rings; i++)
        {
            float v = (float)i / rings, y = yBottom + (yTop - yBottom) * v, r = rBottom + (rTop - rBottom) * v;
            for (int j = 0; j <= seg; j++)
            {
                double ph = 2 * Math.PI * j / seg;
                pos.Add((float)(Math.Cos(ph) * r)); pos.Add(y); pos.Add((float)(Math.Sin(ph) * r));
                double l = Math.Sqrt(1 + slope * slope);
                nrm.Add((float)(Math.Cos(ph) / l)); nrm.Add((float)(slope / l)); nrm.Add((float)(Math.Sin(ph) / l));
                tan.Add((float)-Math.Sin(ph)); tan.Add(0); tan.Add((float)Math.Cos(ph)); tan.Add(-1);
                uv.Add((float)j / seg * 4); uv.Add(v);
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < seg; j++)
            {
                int a = i * (seg + 1) + j, b = a + 1, c = a + seg + 1, d = c + 1;
                idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
            }
        return new Geo { pos = pos.ToArray(), nrm = nrm.ToArray(), tan = tan.ToArray(), uv = uv.ToArray(), idx = idx.ToArray() };
    }

    static float[] Delta(float[] pos, Func<float, float, float, float[]?> f)
    {
        var d = new float[pos.Length];
        for (int i = 0; i < pos.Length; i += 3)
        {
            var r = f(pos[i], pos[i + 1], pos[i + 2]);
            if (r != null) { d[i] = r[0]; d[i + 1] = r[1]; d[i + 2] = r[2]; }
        }
        return d;
    }

    /// <summary>높이에 따라 boneRange[0]~[1] 사이 두 본을 선형 혼합.</summary>
    static (int[] idx, float[] w) SkinByHeight(float[] pos, float[] boneY, int[] range)
    {
        int n = pos.Length / 3; var bi = new int[n * 4]; var bw = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            int a = range[0], b = range[1];
            float t = a == b ? 0 : Math.Clamp((pos[v * 3 + 1] - boneY[a]) / (boneY[b] - boneY[a]), 0, 1);
            bi[v * 4] = a; bw[v * 4] = 1 - t; bi[v * 4 + 1] = b; bw[v * 4 + 1] = t;
        }
        return (bi, bw);
    }

    static JNode MeshJson(IrBufferWriter w, string id, string name, Geo g, (int[] idx, float[] w) skin, float[] boneY, List<(string, float[])>? shapes)
    {
        var bind = new List<float>();
        foreach (var y in boneY) bind.AddRange(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, -y, 0, 1 }); // 열 우선, 평행이동의 역
        var m = JNode.Obj().Set("id", id).Set("name", name).Set("vertexCount", g.pos.Length / 3)
            .Set("positions", w.Write(g.pos).ToJson()).Set("normals", w.Write(g.nrm).ToJson()).Set("tangents", w.Write(g.tan).ToJson())
            .Set("uv0", w.Write(g.uv).ToJson().Set("dim", 2))
            .Set("boneIndices", w.Write(skin.idx).ToJson()).Set("boneWeights", w.Write(skin.w).ToJson())
            .Set("bindposes", w.Write(bind.ToArray()).ToJson())
            .Set("submeshes", JNode.Arr().Add(JNode.Obj().Set("indices", w.Write(g.idx).ToJson())));
        var bs = JNode.Arr();
        if (shapes != null)
            foreach (var (n, d) in shapes)
                bs.Add(JNode.Obj().Set("name", n).Set("frames", JNode.Arr().Add(JNode.Obj().Set("weight", 100).Set("dPos", w.Write(d).ToJson()))));
        m.Set("blendShapes", bs);
        return m;
    }

    static JNode RendererJson(string id, int node, string mesh, int[] bones, int root, string[] mats, float[] weights)
    {
        var b = JNode.Arr(); foreach (var x in bones) b.Add(x);
        var m = JNode.Arr(); foreach (var x in mats) m.Add(x);
        return JNode.Obj().Set("id", id).Set("node", node).Set("type", "SkinnedMeshRenderer").Set("mesh", mesh)
            .Set("bones", b).Set("rootBone", root).Set("materials", m).Set("blendShapeWeights", JNode.FloatArray(weights)).Set("enabled", true);
    }

    static JNode Mat(string guid, string path, string shader, string? tex, (string, float)[] floats, (string, float[])[] colors)
    {
        var f = JNode.Obj(); foreach (var (k, v) in floats) f.Set(k, v);
        var c = JNode.Obj(); foreach (var (k, v) in colors) c.Set(k, JNode.FloatArray(v));
        var t = JNode.Obj();
        if (tex != null) t.Set("_MainTex", JNode.Obj().Set("texture", tex).Set("scale", JNode.FloatArray(new[] { 1f, 1f })).Set("offset", JNode.FloatArray(new[] { 0f, 0f })));
        return JNode.Obj().Set("guid", guid).Set("path", path).Set("shaderGuid", shader).Set("floats", f).Set("colors", c).Set("texEnvs", t)
            .Set("keywords", JNode.Arr()).Set("renderQueue", -1);
    }

    // ---- 최소 PNG 작성기 (RGBA8, 필터 없음) ----
    static void WritePng(string path, int w, int h, Func<int, int, (byte, byte, byte, byte)> px)
    {
        var raw = new byte[h * (w * 4 + 1)];
        for (int y = 0; y < h; y++)
        {
            int o = y * (w * 4 + 1); raw[o++] = 0;
            for (int x = 0; x < w; x++) { var (r, g, b, a) = px(x, y); raw[o++] = r; raw[o++] = g; raw[o++] = b; raw[o++] = a; }
        }
        byte[] z;
        using (var ms = new MemoryStream()) { using (var zs = new ZLibStream(ms, CompressionLevel.Optimal)) zs.Write(raw, 0, raw.Length); z = ms.ToArray(); }
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", z); Chunk(f, "IEND", Array.Empty<byte>());
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, Crc(td)); s.Write(crc);
    }

    static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }
}
