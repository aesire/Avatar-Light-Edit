// 미리보기 PhysBone 느낌 값 (2026-10-24 사용자: "본이 너무 단단해 — 관성을 더, 설정에서 값을 보며 바꾸게").
// 설정 › 미리보기 PhysBone › 느낌의 슬라이더 목록. 값은 avhost.json `physBoneFeel { 열쇠: 값 }`(없으면 기본), 렌더러에는 setPhysBones { feel }로 모두 보낸다.
// 기본값은 렌더러 PhysBoneSim의 필드 기본값과 같게 둔다. 이 에디터 미리보기 값이라 VRChat에는 영향이 없다.
using System;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public static class PhysBoneFeel
    {
        public sealed record Item(string Key, string Label, string Help, double Min, double Max, double Default);

        public static readonly Item[] Items =
        {
            new("damp", "관성 (출렁임)", "Spring(Momentum)에 곱하는 값. 클수록 움직임이 이어져 더 출렁이고, 작을수록 한 번에 멈춰요. 1 = 원본 값 그대로", 0, 1.5, 0.9),
            new("pull", "복원력", "Pull에 곱하는 값. 클수록 쉬는 자세로 단단히 돌아오고, 작을수록 흐느적거려요. 1 = 원본 값 그대로", 0.25, 4, 1.5),
            new("swing", "흔들림 최대 빠르기 (m/s)", "몸에 대해 흔들리는 속도의 상한이에요 (무게감으로 나눠요: 보통 4면 ÷2). 작을수록 크게 흔들려도 천천히 움직여요", 0.2, 5, 1.6),
            new("follow", "작은 움직임 거르기 (초)", "몸이 빨라지거나 멈추는 것을 이 시간으로 고르게 펴서 느껴요. 길수록 고개 끄덕임·잔걸음 같은 작고 빠른 움직임에 덜 흔들려요. 0 = 거르지 않음", 0, 0.5, 0.1),
            new("gravity", "중력 배율", "Gravity에 곱하는 값. 원본 Gravity가 0인 본은 그대로예요", 0, 3, 1.5),
            new("falloff", "Gravity Falloff 배율", "쉬는 자세에서 중력을 빼는 몫(Gravity Falloff)에 곱하는 값. 작을수록 서 있을 때도 중력을 더 받아요", 0, 1, 0.7),
            new("slop", "콜라이더 파고듦 허용 (mm)", "콜라이더에 이만큼까지 들어간 것은 밀어내지 않아요. 클수록 닿은 곳에서 덜 떨리고 조금 뚫려 보여요", 0, 15, 4),
            new("friction", "닿았을 때 멈춤", "콜라이더에 닿은 채 미끄러지는 속도를 줄이는 정도. 1에 가까울수록 닿은 자리에 그대로 있어요", 0, 1, 0.7),
            new("smooth", "잔떨림 거르기 (mm)", "이 정도 크기의 작은 움직임은 화면에서 천천히 따라가요. 클수록 떨림이 안 보이지만 작은 흔들림이 늦게 보여요", 0, 15, 6),
        };

        public static double Get(JNode feel, Item it) => feel.Has(it.Key) ? Math.Clamp(feel[it.Key].AsDouble, it.Min, it.Max) : it.Default;

        /// <summary>렌더러로 보낼 값: 모든 열쇠 (설정에 없으면 기본값)</summary>
        public static JNode Message(JNode feel)
        {
            var o = JNode.Obj();
            foreach (var it in Items) o.Set(it.Key, Get(feel, it));
            return o;
        }
    }
}
