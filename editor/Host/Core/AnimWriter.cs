// 블렌드쉐이프 표정을 Unity 애니메이션(.anim, 텍스트 YAML)으로 쓴다 (2026-10-19 2차).
//   표정 하나      FaceExpression 값 → 값이 일정한 커브 (키 두 개: 0초·1/60초). VRChat FX 레이어의 표정 애니메이션과 같은 모양
//   트래킹 녹화    시간마다 잰 값 → 키프레임 커브 (거의 직선인 가운데 키는 줄인다)
//   VR 모션 녹화  시간마다 읽은 머슬 값 + RootT·RootQ → 휴머노이드 커브 (path "", classID 95 — Animator). AnimClip.SamplePose로 다시 읽힌다
// 경로는 아바타 루트 기준(루트 이름 없이) "Body" 처럼 — Unity에서 아바타 루트의 Animator에 그대로 쓸 수 있다. AnimClip.Parse로 다시 읽힌다.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.Host.Core
{
    public static class AnimWriter
    {
        public sealed class Track
        {
            public string Path = "", Shape = "";
            /// <summary>커브 속성 (빈 값 = blendShape.Shape) · classID (137 = SkinnedMeshRenderer, 95 = Animator 휴머노이드).</summary>
            public string Attribute = "";
            public int ClassId = 137;
            public string Attr => Attribute.Length > 0 ? Attribute : "blendShape." + Shape;
            public List<(float Time, float Value)> Keys = new List<(float, float)>();
        }

        /// <summary>렌더러 id → 아바타 루트 기준 경로 (FaceExpression.RendererByPath의 거꾸로).</summary>
        public static Dictionary<string, string> PathByRenderer(IrScene s)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in FaceExpression.RendererByPath(s)) d[kv.Value] = kv.Key;
            return d;
        }

        /// <summary>표정 하나 (값이 일정). 경로를 찾지 못한 렌더러의 값은 뺀다.</summary>
        public static List<Track> FromExpression(IrScene s, JNode expression)
        {
            var paths = PathByRenderer(s);
            var list = new List<Track>();
            foreach (var (r, shape, v) in FaceExpression.Shapes(expression))
            {
                if (!paths.TryGetValue(r, out var p)) continue;
                list.Add(new Track { Path = p, Shape = shape, Keys = { (0f, v), (1f / 60, v) } });
            }
            return list;
        }

        /// <summary>
        /// 트래킹 녹화. times[i]초에 잰 값 values[i][k] (k = shapes[k]). 처음부터 끝까지 0인 블렌드쉐이프는 뺀다.
        /// tolerance: 앞뒤 키를 잇는 직선에서 이만큼(0~100 값) 안이면 가운데 키를 버린다.
        /// </summary>
        public static List<Track> FromSamples(IrScene s, IReadOnlyList<(string Renderer, string Shape)> shapes, IReadOnlyList<float> times, IReadOnlyList<float[]> values, float tolerance = 0.5f)
        {
            var paths = PathByRenderer(s);
            var list = new List<Track>();
            for (int k = 0; k < shapes.Count; k++)
            {
                if (!paths.TryGetValue(shapes[k].Renderer, out var p)) continue;
                var keys = new List<(float, float)>();
                for (int i = 0; i < times.Count; i++) keys.Add((times[i], k < values[i].Length ? values[i][k] : 0));
                if (keys.All(x => Math.Abs(x.Item2) < 1e-3f)) continue;
                list.Add(new Track { Path = p, Shape = shapes[k].Shape, Keys = Reduce(keys, tolerance) });
            }
            return list;
        }

        /// <summary>
        /// VR 모션 녹화 (vrMotion): frames[i] = 머슬 값(names 순서) + RootT x·y·z + RootQ x·y·z·w, 1/fps초 간격.
        /// 머슬 이름은 .anim 속성으로 (손가락은 "LeftHand.Index.1 Stretched"). 직선에서 tolerance(머슬)·0.001(RootT·RootQ) 안인 가운데 키는 버린다.
        /// </summary>
        public static List<Track> FromMotion(IReadOnlyList<string> names, IReadOnlyList<float[]> frames, float fps, float tolerance = 0.002f)
        {
            var list = new List<Track>();
            if (frames.Count == 0 || fps <= 0) return list;
            var attrs = names.Select(Muscles.ToAnimAttribute).ToList();
            attrs.AddRange(new[] { "RootT.x", "RootT.y", "RootT.z", "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" });
            for (int k = 0; k < attrs.Count; k++)
            {
                if (attrs[k] is not string attr) continue;
                var keys = new List<(float, float)>();
                for (int i = 0; i < frames.Count; i++) keys.Add((i / fps, k < frames[i].Length ? frames[i][k] : 0));
                list.Add(new Track { Attribute = attr, ClassId = 95, Keys = Reduce(keys, k < names.Count ? tolerance : 0.001f) });
            }
            return list;
        }

        /// <summary>직선으로 이어도 tolerance 안인 가운데 키를 버린다 (처음·끝 키는 남긴다).</summary>
        public static List<(float Time, float Value)> Reduce(List<(float Time, float Value)> keys, float tolerance)
        {
            if (keys.Count <= 2) return keys;
            var keep = new List<(float, float)> { keys[0] };
            int anchor = 0;
            for (int i = 1; i < keys.Count - 1; i++)
            {
                // anchor → i+1 직선이 그 사이의 모든 키를 tolerance 안으로 지나면 i는 버려도 된다
                var a = keys[anchor]; var b = keys[i + 1];
                bool ok = true;
                for (int j = anchor + 1; j <= i && ok; j++)
                {
                    float t = b.Time - a.Time <= 0 ? 0 : (keys[j].Time - a.Time) / (b.Time - a.Time);
                    ok = Math.Abs(a.Value + (b.Value - a.Value) * t - keys[j].Value) <= tolerance;
                }
                if (!ok) { keep.Add(keys[i]); anchor = i; }
            }
            keep.Add(keys[keys.Count - 1]);
            return keep;
        }

        public static void Save(string path, string name, IReadOnlyList<Track> tracks)
        {
            File.WriteAllText(path, ToYaml(name, tracks), new UTF8Encoding(false));
        }

        public static string ToYaml(string name, IReadOnlyList<Track> tracks)
        {
            var ic = CultureInfo.InvariantCulture;
            string F(float v) => v.ToString("R", ic);
            float stop = Math.Max(1f / 60, tracks.Count == 0 ? 0 : tracks.Max(t => t.Keys.Count == 0 ? 0 : t.Keys[t.Keys.Count - 1].Time));
            var curves = new StringBuilder();
            foreach (var t in tracks)
            {
                curves.Append("  - serializedVersion: 2\n    curve:\n      serializedVersion: 2\n      m_Curve:\n");
                for (int i = 0; i < t.Keys.Count; i++)
                {
                    // 기울기: 앞뒤 키를 잇는 선 (끝은 한쪽만) — 선형에 가까운 부드러운 곡선
                    float slope(int a, int b) => t.Keys[b].Time - t.Keys[a].Time <= 0 ? 0 : (t.Keys[b].Value - t.Keys[a].Value) / (t.Keys[b].Time - t.Keys[a].Time);
                    float inS = i > 0 ? slope(i - 1, i) : 0, outS = i < t.Keys.Count - 1 ? slope(i, i + 1) : 0;
                    curves.Append("      - serializedVersion: 3\n")
                          .Append("        time: ").Append(F(t.Keys[i].Time)).Append('\n')
                          .Append("        value: ").Append(F(t.Keys[i].Value)).Append('\n')
                          .Append("        inSlope: ").Append(F(inS)).Append('\n')
                          .Append("        outSlope: ").Append(F(outS)).Append('\n')
                          .Append("        tangentMode: 0\n        weightedMode: 0\n        inWeight: 0.33333334\n        outWeight: 0.33333334\n");
                }
                curves.Append("      m_PreInfinity: 2\n      m_PostInfinity: 2\n      m_RotationOrder: 4\n")
                      .Append("    attribute: ").Append(t.Attr).Append('\n')
                      .Append("    path: ").Append(t.Path).Append('\n')
                      .Append("    classID: ").Append(t.ClassId).Append("\n    script: {fileID: 0}\n");
            }
            var bindings = new StringBuilder();
            // 휴머노이드 커브(95)의 바인딩은 Unity가 머슬 번호로 만든다 — 적지 않고 편집기 커브(m_EditorCurves)에 맡긴다
            foreach (var t in tracks.Where(t => t.ClassId == 137))
                bindings.Append("    - serializedVersion: 2\n      path: ").Append(Crc32(t.Path)).Append("\n      attribute: ").Append(Crc32(t.Attr))
                        .Append("\n      script: {fileID: 0}\n      typeID: 137\n      customType: 20\n      isPPtrCurve: 0\n      isIntCurve: 0\n      isSerializeReferenceCurve: 0\n");
            string curveList = tracks.Count == 0 ? " []\n" : "\n" + curves;
            var sb = new StringBuilder();
            sb.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!74 &7400000\nAnimationClip:\n")
              .Append("  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n")
              .Append("  m_Name: ").Append(name).Append('\n')
              .Append("  serializedVersion: 7\n  m_Legacy: 0\n  m_Compressed: 0\n  m_UseHighQualityCurve: 1\n")
              .Append("  m_RotationCurves: []\n  m_CompressedRotationCurves: []\n  m_EulerCurves: []\n  m_PositionCurves: []\n  m_ScaleCurves: []\n")
              .Append("  m_FloatCurves:").Append(curveList)
              .Append("  m_PPtrCurves: []\n  m_SampleRate: 60\n  m_WrapMode: 0\n")
              .Append("  m_Bounds:\n    m_Center: {x: 0, y: 0, z: 0}\n    m_Extent: {x: 0, y: 0, z: 0}\n")
              .Append("  m_ClipBindingConstant:\n    genericBindings:").Append(bindings.Length == 0 ? " []\n" : "\n" + bindings).Append("    pptrCurveMapping: []\n")
              .Append("  m_AnimationClipSettings:\n    serializedVersion: 2\n    m_AdditiveReferencePoseClip: {fileID: 0}\n    m_AdditiveReferencePoseTime: 0\n")
              .Append("    m_StartTime: 0\n    m_StopTime: ").Append(F(stop)).Append('\n')
              .Append("    m_OrientationOffsetY: 0\n    m_Level: 0\n    m_CycleOffset: 0\n    m_HasAdditiveReferencePose: 0\n    m_LoopTime: 0\n    m_LoopBlend: 0\n")
              .Append("    m_LoopBlendOrientation: 0\n    m_LoopBlendPositionY: 0\n    m_LoopBlendPositionXZ: 0\n    m_KeepOriginalOrientation: 0\n")
              .Append("    m_KeepOriginalPositionY: 1\n    m_KeepOriginalPositionXZ: 0\n    m_HeightFromFeet: 0\n    m_Mirror: 0\n")
              .Append("  m_EditorCurves:").Append(curveList)
              .Append("  m_EulerEditorCurves: []\n  m_HasGenericRootTransform: 0\n  m_HasMotionFloatCurves: 0\n  m_Events: []\n");
            return sb.ToString();
        }

        /// <summary>Unity가 바인딩 경로·속성 이름에 쓰는 CRC32 (IEEE).</summary>
        public static uint Crc32(string s)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in Encoding.UTF8.GetBytes(s))
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }
    }
}
