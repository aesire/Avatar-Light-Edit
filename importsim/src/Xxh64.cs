using System.Text;

namespace ImportSim
{
    /// <summary>XXH64 (seed 0)와 Unity ModelImporter의 fileID 계산.</summary>
    public static class Xxh64
    {
        const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
                    P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

        static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));
        static ulong Round(ulong acc, ulong lane) => Rotl(acc + lane * P2, 31) * P1;
        static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;
        static ulong U64(byte[] b, int i) => System.BitConverter.ToUInt64(b, i);
        static uint U32(byte[] b, int i) => System.BitConverter.ToUInt32(b, i);

        public static ulong Hash(byte[] data, ulong seed = 0)
        {
            int n = data.Length, i = 0;
            ulong h;
            if (n >= 32)
            {
                ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
                for (; i + 32 <= n; i += 32)
                {
                    v1 = Round(v1, U64(data, i)); v2 = Round(v2, U64(data, i + 8));
                    v3 = Round(v3, U64(data, i + 16)); v4 = Round(v4, U64(data, i + 24));
                }
                h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
                h = Merge(h, v1); h = Merge(h, v2); h = Merge(h, v3); h = Merge(h, v4);
            }
            else h = seed + P5;
            h += (ulong)n;
            for (; i + 8 <= n; i += 8) h = Rotl(h ^ Round(0, U64(data, i)), 27) * P1 + P4;
            if (i + 4 <= n) { h = Rotl(h ^ (U32(data, i) * P1), 23) * P2 + P3; i += 4; }
            for (; i < n; i++) h = Rotl(h ^ (data[i] * P5), 11) * P1;
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return h;
        }

        /// <summary>fileID = int64(XXH64("Type:{type}->{identifier}0"))</summary>
        public static long FileId(string type, string identifier) =>
            unchecked((long)Hash(Encoding.UTF8.GetBytes("Type:" + type + "->" + identifier + "0")));
    }
}
