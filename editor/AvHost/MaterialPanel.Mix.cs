// 머티리얼 페이지: 텍스처 합치기 창 열기 (2026-11-02). 창은 TextureMixWindow.cs, 계산은 Host/Core/TextureMix.cs.
using Avalonia.Controls;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    public sealed partial class MaterialPanel
    {
        TextureMixWindow? _mix;

        /// <summary>lilToon Main 섹션 끝에 붙이는 "텍스처 합치기" 줄 (색 굽기 옆). 다른 텍스처 칸은 줄 끝의 작은 단추로 연다.</summary>
        Control MixRow(string mat)
        {
            var btn = Ui.Button("합치기…", () => OpenMix(mat, "_MainTex", "메인 텍스처"), "메인 텍스처 위에 다른 이미지를 올려요", Glyph.Add);
            btn.Margin = new Avalonia.Thickness(0);
            return Ui.HelpRow("텍스처 합치기", "얼굴 위에 눈을, 몸 위에 이펙트나 문양을 올려 새 텍스처 하나로 만들어요. "
                + "자리·크기·섞는 방법을 고르고 아바타에서 바로 확인해요. 원본 파일은 그대로 남고 실행 취소할 수 있어요.", btn);
        }

        void OpenMix(string mat, string prop, string label)
        {
            if (Doc == null) return;
            var doc = Doc;
            var baseKey = doc.Get(Addr.MatTex(mat, prop)).AsStringOrNull;
            if (baseKey == null || doc.Texture(baseKey) == null) { _ctx.Status("먼저 이 칸에 바탕이 될 텍스처를 골라요."); return; }
            if (doc.Texture(baseKey)!.Importer.NormalMap) { _ctx.Status("노멀 맵에는 이미지를 합칠 수 없어요."); return; }
            _mix?.Close();
            _mix = new TextureMixWindow(_ctx, doc, mat, prop, label, CompatibleTextures(mat));
            _mix.Closed += (_, __) => _mix = null;
            if (TopLevel.GetTopLevel(this) is Window w) _mix.Show(w); else _mix.Show();
        }
    }
}
