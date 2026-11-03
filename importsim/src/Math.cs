using System;

namespace ImportSim
{
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized { get { var l = Length; return l > 1e-20 ? this * (1 / l) : this; } }
        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    public struct Quat
    {
        public double X, Y, Z, W;
        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
    }

    /// <summary>4x4 행렬 (열 벡터 규약: v' = M * v, M[row, col])</summary>
    public sealed class Mat4
    {
        public readonly double[,] M = new double[4, 4];

        public static Mat4 Identity()
        {
            var r = new Mat4();
            for (int i = 0; i < 4; i++) r.M[i, i] = 1;
            return r;
        }

        public static Mat4 Translate(Vec3 t) { var r = Identity(); r.M[0, 3] = t.X; r.M[1, 3] = t.Y; r.M[2, 3] = t.Z; return r; }
        public static Mat4 Scale(Vec3 s) { var r = Identity(); r.M[0, 0] = s.X; r.M[1, 1] = s.Y; r.M[2, 2] = s.Z; return r; }

        static Mat4 RotAxis(int axis, double deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            var r = Identity();
            int i = (axis + 1) % 3, j = (axis + 2) % 3;
            r.M[i, i] = c; r.M[i, j] = -s; r.M[j, i] = s; r.M[j, j] = c;
            return r;
        }

        /// FBX 오일러 회전 (도). order: 0 XYZ, 1 XZY, 2 YZX, 3 YXZ, 4 ZXY, 5 ZYX — 앞 글자 축이 먼저 적용된다.
        /// Unity Transform 값 (localPosition · localRotation · localScale) → 행렬
        public static Mat4 TRS(Vec3 t, Quat q, Vec3 s)
        {
            double x = q.X, y = q.Y, z = q.Z, w = q.W;
            var r = Identity();
            r.M[0, 0] = (1 - 2 * (y * y + z * z)) * s.X; r.M[0, 1] = 2 * (x * y - z * w) * s.Y; r.M[0, 2] = 2 * (x * z + y * w) * s.Z;
            r.M[1, 0] = 2 * (x * y + z * w) * s.X; r.M[1, 1] = (1 - 2 * (x * x + z * z)) * s.Y; r.M[1, 2] = 2 * (y * z - x * w) * s.Z;
            r.M[2, 0] = 2 * (x * z - y * w) * s.X; r.M[2, 1] = 2 * (y * z + x * w) * s.Y; r.M[2, 2] = (1 - 2 * (x * x + y * y)) * s.Z;
            r.M[0, 3] = t.X; r.M[1, 3] = t.Y; r.M[2, 3] = t.Z;
            return r;
        }

        public static Mat4 Euler(Vec3 deg, int order = 0)
        {
            Mat4 x = RotAxis(0, deg.X), y = RotAxis(1, deg.Y), z = RotAxis(2, deg.Z);
            return order switch
            {
                1 => y * z * x,   // XZY
                2 => x * z * y,   // YZX
                3 => z * x * y,   // YXZ
                4 => y * x * z,   // ZXY
                5 => x * y * z,   // ZYX
                _ => z * y * x,   // XYZ
            };
        }

        public static Mat4 operator *(Mat4 a, Mat4 b)
        {
            var r = new Mat4();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a.M[i, k] * b.M[k, j];
                    r.M[i, j] = s;
                }
            return r;
        }

        public Vec3 MulPoint(Vec3 v) => new Vec3(
            M[0, 0] * v.X + M[0, 1] * v.Y + M[0, 2] * v.Z + M[0, 3],
            M[1, 0] * v.X + M[1, 1] * v.Y + M[1, 2] * v.Z + M[1, 3],
            M[2, 0] * v.X + M[2, 1] * v.Y + M[2, 2] * v.Z + M[2, 3]);

        public Vec3 MulDir(Vec3 v) => new Vec3(
            M[0, 0] * v.X + M[0, 1] * v.Y + M[0, 2] * v.Z,
            M[1, 0] * v.X + M[1, 1] * v.Y + M[1, 2] * v.Z,
            M[2, 0] * v.X + M[2, 1] * v.Y + M[2, 2] * v.Z);

        /// FBX 배열(열 우선 16개)에서
        public static Mat4 FromColumnMajor(double[] a, int off = 0)
        {
            var r = new Mat4();
            for (int c = 0; c < 4; c++)
                for (int row = 0; row < 4; row++) r.M[row, c] = a[off + c * 4 + row];
            return r;
        }

        public double Det3 =>
            M[0, 0] * (M[1, 1] * M[2, 2] - M[1, 2] * M[2, 1]) - M[0, 1] * (M[1, 0] * M[2, 2] - M[1, 2] * M[2, 0]) + M[0, 2] * (M[1, 0] * M[2, 1] - M[1, 1] * M[2, 0]);

        /// 일반 4x4 역행렬 (가우스 소거)
        public Mat4 Inverse()
        {
            var a = new double[4, 8];
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++) a[i, j] = M[i, j];
                a[i, 4 + i] = 1;
            }
            for (int c = 0; c < 4; c++)
            {
                int p = c;
                for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[p, c])) p = r;
                for (int j = 0; j < 8; j++) { var t = a[c, j]; a[c, j] = a[p, j]; a[p, j] = t; }
                double d = a[c, c];
                if (Math.Abs(d) < 1e-300) return Identity();
                for (int j = 0; j < 8; j++) a[c, j] /= d;
                for (int r = 0; r < 4; r++)
                    if (r != c)
                    {
                        double f = a[r, c];
                        if (f != 0) for (int j = 0; j < 8; j++) a[r, j] -= f * a[c, j];
                    }
            }
            var inv = new Mat4();
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) inv.M[i, j] = a[i, 4 + j];
            return inv;
        }

        /// TRS 분해 (전단 없음 가정). 음수 행렬식이면 X 스케일을 음수로 둔다.
        public void Decompose(out Vec3 t, out Quat q, out Vec3 s)
        {
            t = new Vec3(M[0, 3], M[1, 3], M[2, 3]);
            var c0 = new Vec3(M[0, 0], M[1, 0], M[2, 0]);
            var c1 = new Vec3(M[0, 1], M[1, 1], M[2, 1]);
            var c2 = new Vec3(M[0, 2], M[1, 2], M[2, 2]);
            s = new Vec3(c0.Length, c1.Length, c2.Length);
            if (Det3 < 0) s.X = -s.X;
            c0 = s.X != 0 ? c0 * (1 / s.X) : new Vec3(1, 0, 0);
            c1 = s.Y != 0 ? c1 * (1 / s.Y) : new Vec3(0, 1, 0);
            c2 = s.Z != 0 ? c2 * (1 / s.Z) : new Vec3(0, 0, 1);
            q = FromRotation(c0, c1, c2);
        }

        static Quat FromRotation(Vec3 c0, Vec3 c1, Vec3 c2)
        {
            double m00 = c0.X, m10 = c0.Y, m20 = c0.Z, m01 = c1.X, m11 = c1.Y, m21 = c1.Z, m02 = c2.X, m12 = c2.Y, m22 = c2.Z;
            double tr = m00 + m11 + m22;
            Quat q;
            if (tr > 0)
            {
                double s = Math.Sqrt(tr + 1) * 2;
                q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
                q = new Quat(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                double s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
                q = new Quat((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                double s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s);
            }
            double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
            return new Quat(q.X / n, q.Y / n, q.Z / n, q.W / n);
        }
    }
}
