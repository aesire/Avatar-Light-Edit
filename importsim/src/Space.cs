using System;

namespace ImportSim
{
    /// <summary>
    /// FBX 공간 -> Unity 공간.
    /// - 단위: 위치·정점에 s = (useFileScale ? UnitScaleFactor/100 : 1) * globalScale 을 곱한다 (localScale 은 그대로)
    /// - 축: 파일 축 체계를 Y-up 으로 돌리는 회전 Rax 를 최상위 노드에만 건다 (bakeAxisConversion: 0)
    /// - 손잡이: X 를 뒤집는다 (오른손 -> 왼손). 로컬 행렬은 D·L·D⁻¹, D = diag(-s, s, s)
    /// </summary>
    public sealed class UnitySpace
    {
        public readonly double Scale;
        public readonly Mat4 AxisRotation;
        readonly Mat4 d, dInv;

        public UnitySpace(FbxScene scene, bool useFileScale, double globalScale)
        {
            var gs = scene.GlobalSettings;
            double unit = gs?.P70Num("UnitScaleFactor", 1) ?? 1;
            Scale = (useFileScale ? unit / 100.0 : 1.0) * globalScale;
            d = Mat4.Scale(new Vec3(-Scale, Scale, Scale));
            dInv = Mat4.Scale(new Vec3(-1 / Scale, 1 / Scale, 1 / Scale));
            AxisRotation = AxisToYUp(gs);
        }

        /// 파일 축(Coord, Up, Front)을 (X, Y, Z)로 보내는 회전 = [C U F]^T
        static Mat4 AxisToYUp(FbxNode? gs)
        {
            if (gs == null) return Mat4.Identity();
            Vec3 Axis(string a, string sign, int defAxis)
            {
                int ax = (int)gs.P70Num(a, defAxis);
                double sg = gs.P70Num(sign, 1) < 0 ? -1 : 1;
                return new Vec3(ax == 0 ? sg : 0, ax == 1 ? sg : 0, ax == 2 ? sg : 0);
            }
            Vec3 up = Axis("UpAxis", "UpAxisSign", 1), front = Axis("FrontAxis", "FrontAxisSign", 2), coord = Axis("CoordAxis", "CoordAxisSign", 0);
            var r = Mat4.Identity();
            // 행 = 파일에서의 C, U, F
            r.M[0, 0] = coord.X; r.M[0, 1] = coord.Y; r.M[0, 2] = coord.Z;
            r.M[1, 0] = up.X; r.M[1, 1] = up.Y; r.M[1, 2] = up.Z;
            r.M[2, 0] = front.X; r.M[2, 1] = front.Y; r.M[2, 2] = front.Z;
            return r;
        }

        /// FBX 로컬 행렬 -> Unity 로컬 행렬
        public Mat4 Local(Mat4 fbxLocal, bool topLevel) => d * (topLevel ? AxisRotation * fbxLocal : fbxLocal) * dInv;

        /// 노드 로컬 공간의 점/방향 (정점, 노멀, 블렌드쉐이프 델타)
        public Vec3 Point(Vec3 v) => new Vec3(-v.X * Scale, v.Y * Scale, v.Z * Scale);
        public static Vec3 Dir(Vec3 v) => new Vec3(-v.X, v.Y, v.Z);

        /// FBX 씬 월드 행렬 -> Unity 월드 (모델 루트 기준)
        public Mat4 World(Mat4 fbxWorld) => d * AxisRotation * fbxWorld * dInv;

        /// FBX Model 의 로컬 행렬:
        /// T · Roff · Rp · Rpre · R · Rpost⁻¹ · Rp⁻¹ · Soff · Sp · S · Sp⁻¹
        public static Mat4 FbxLocal(FbxNode model)
        {
            var zero = new Vec3(0, 0, 0);
            var t = model.P70Vec("Lcl Translation", zero);
            var r = model.P70Vec("Lcl Rotation", zero);
            var s = model.P70Vec("Lcl Scaling", new Vec3(1, 1, 1));
            bool rotActive = model.P70Num("RotationActive", 0) != 0;
            int order = rotActive ? (int)model.P70Num("RotationOrder", 0) : 0;
            var pre = rotActive ? model.P70Vec("PreRotation", zero) : zero;
            var post = rotActive ? model.P70Vec("PostRotation", zero) : zero;
            var roff = model.P70Vec("RotationOffset", zero);
            var rp = model.P70Vec("RotationPivot", zero);
            var soff = model.P70Vec("ScalingOffset", zero);
            var sp = model.P70Vec("ScalingPivot", zero);
            return Mat4.Translate(t) * Mat4.Translate(roff) * Mat4.Translate(rp) * Mat4.Euler(pre) * Mat4.Euler(r, order)
                 * Mat4.Euler(post).Inverse() * Mat4.Translate(-rp) * Mat4.Translate(soff) * Mat4.Translate(sp)
                 * Mat4.Scale(s) * Mat4.Translate(-sp);
        }

        /// 지오메트릭 변환 (메시에만 적용, 자식에는 상속되지 않음)
        public static Mat4 Geometric(FbxNode model)
        {
            var zero = new Vec3(0, 0, 0);
            return Mat4.Translate(model.P70Vec("GeometricTranslation", zero)) * Mat4.Euler(model.P70Vec("GeometricRotation", zero))
                 * Mat4.Scale(model.P70Vec("GeometricScaling", new Vec3(1, 1, 1)));
        }
    }
}
