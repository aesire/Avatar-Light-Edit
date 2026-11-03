using System;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    /// <summary>편집 가능한 값 하나의 주소. Changeset의 모든 연산은 "이 주소의 값을 X로" 형태다.</summary>
    public enum AddrKind
    {
        MatFloat, MatColor, MatVector, MatTex, MatTexST, MatKeyword, MatRenderQueue, MatShader,
        BlendShape, NodeActive, RendererEnabled, Environment, Pose,
        MeshEdit, NodeTransform, AvatarTransform, Expression,
        Toggles, ExpressionKeys, FaceMap, TexAsset, ShapeKeys,
    }

    public readonly struct Addr : IEquatable<Addr>
    {
        public readonly AddrKind Kind;
        public readonly string Target; // 머티리얼 GUID / 렌더러 id / 노드 id (환경·자세는 "")
        public readonly string Name;   // 프로퍼티·키워드·블렌드쉐이프 이름 (없으면 "")

        public Addr(AddrKind kind, string target, string name = "") { Kind = kind; Target = target; Name = name ?? ""; }

        public static Addr MatFloat(string mat, string prop) => new Addr(AddrKind.MatFloat, mat, prop);
        public static Addr MatColor(string mat, string prop) => new Addr(AddrKind.MatColor, mat, prop);
        public static Addr MatVector(string mat, string prop) => new Addr(AddrKind.MatVector, mat, prop);
        public static Addr MatTex(string mat, string prop) => new Addr(AddrKind.MatTex, mat, prop);
        public static Addr MatTexST(string mat, string prop) => new Addr(AddrKind.MatTexST, mat, prop);
        public static Addr MatKeyword(string mat, string kw) => new Addr(AddrKind.MatKeyword, mat, kw);
        public static Addr MatRenderQueue(string mat) => new Addr(AddrKind.MatRenderQueue, mat);
        public static Addr MatShader(string mat) => new Addr(AddrKind.MatShader, mat);
        public static Addr BlendShape(string renderer, string name) => new Addr(AddrKind.BlendShape, renderer, name);
        public static Addr NodeActive(int node) => new Addr(AddrKind.NodeActive, node.ToString());
        public static Addr RendererEnabled(string renderer) => new Addr(AddrKind.RendererEnabled, renderer);
        public static readonly Addr Environment = new Addr(AddrKind.Environment, "");
        /// <summary>휴머노이드 자세 (머슬 값). 원본 = Null = 불러온 그대로.</summary>
        public static readonly Addr Pose = new Addr(AddrKind.Pose, "");
        /// <summary>메시 하나의 편집 연산 목록 (PLAN.md 3.9, Protocol.MeshOps). 원본 = 빈 목록.</summary>
        public static Addr MeshEdit(string mesh) => new Addr(AddrKind.MeshEdit, mesh);
        /// <summary>노드 하나의 로컬 위치·회전·크기 [위치3, 회전4(xyzw), 크기3] (IR 부모 기준). 핸들로 옮긴 아이템. 원본 = IR 값.</summary>
        public static Addr NodeTransform(int node) => new Addr(AddrKind.NodeTransform, node.ToString());
        /// <summary>아바타 전체의 자리 [위치3, 회전4]. 미리보기 전용(업로드에는 쓰지 않음). 원본 = 원점에 정면.</summary>
        public static readonly Addr AvatarTransform = new Addr(AddrKind.AvatarTransform, "");
        /// <summary>표정 (Core.FaceExpression). 미리보기 전용. 원본 = Null = 사용자 블렌드쉐이프 값 그대로.</summary>
        public static readonly Addr Expression = new Addr(AddrKind.Expression, "");
        /// <summary>사용자 토글 묶음 [{ name, nodes: [노드 id] }] (Core.ToggleSet). 순서대로 Shift+1~9. 원본 = 빈 목록. 묶음 정의일 뿐 업로드와 무관.</summary>
        public static readonly Addr Toggles = new Addr(AddrKind.Toggles, "");
        /// <summary>표정 단축키 F1~F9 [표정 값(FaceExpression) | null] × 9까지. 원본 = 빈 목록.</summary>
        public static readonly Addr ExpressionKeys = new Addr(AddrKind.ExpressionKeys, "");
        /// <summary>블렌드쉐이프 단축키 정의 (Core.ShapeKeys). 원본 = 빈 목록. 누른 상태는 기록하지 않는다 (ShapeKeyRunner). 미리보기 전용.</summary>
        public static readonly Addr ShapeKeys = new Addr(AddrKind.ShapeKeys, "");
        /// <summary>페이셜 트래킹 수동 연결 (Core.FaceMap) { add: [{ src, r, s, k }], off: [{ src, r, s }] }. 원본 = 빈 객체 = 자동 연결 그대로. 미리보기 전용.</summary>
        public static readonly Addr FaceMap = new Addr(AddrKind.FaceMap, "");
        /// <summary>
        /// IR에 없는 새 텍스처 (색 굽기 결과) { file: 절대 경로, path: 보이는 이름, like: 임포트 설정을 따를 원본 텍스처 키 }. 원본 = Null (없음).
        /// 머티리얼 텍스처 값(MatTex)은 IR 텍스처 GUID 대신 이 키를 가리킬 수 있다 (Document.Texture).
        /// </summary>
        public static Addr TexAsset(string key) => new Addr(AddrKind.TexAsset, key);
        public static readonly float[] Identity = { 0, 0, 0, 0, 0, 0, 1 };

        public bool IsMaterial => Kind <= AddrKind.MatShader;

        public override string ToString() => Kind + "|" + Target + "|" + Name;
        public static Addr Parse(string s)
        {
            var p = s.Split(new[] { '|' }, 3);
            if (p.Length != 3 || !Enum.TryParse<AddrKind>(p[0], out var k)) throw new FormatException("bad address: " + s);
            return new Addr(k, p[1], p[2]);
        }

        public bool Equals(Addr o) => Kind == o.Kind && Target == o.Target && Name == o.Name;
        public override bool Equals(object? obj) => obj is Addr a && Equals(a);
        public override int GetHashCode() => ((int)Kind * 397) ^ (Target?.GetHashCode() ?? 0) * 31 ^ (Name?.GetHashCode() ?? 0);
    }

    public static class Values
    {
        /// <summary>슬라이더 반올림 등으로 생기는 미세 오차는 같은 값으로 본다 (원본과 같으면 덮어쓰기 제거).</summary>
        public static bool Equal(JNode a, JNode b)
        {
            if (a.Kind != b.Kind)
            {
                // bool/숫자 혼용 허용 (토글 프로퍼티는 float 0/1)
                if ((a.Kind == JKind.Bool || a.Kind == JKind.Number) && (b.Kind == JKind.Bool || b.Kind == JKind.Number))
                    return Math.Abs(a.AsDouble - b.AsDouble) < 1e-5;
                return false;
            }
            switch (a.Kind)
            {
                case JKind.Number: return Math.Abs(a.AsDouble - b.AsDouble) <= 1e-5 * Math.Max(1, Math.Abs(a.AsDouble));
                case JKind.Array:
                    if (a.Count != b.Count) return false;
                    for (int i = 0; i < a.Count; i++) if (!Equal(a[i], b[i])) return false;
                    return true;
                default: return a.ToJson() == b.ToJson();
            }
        }
    }
}
