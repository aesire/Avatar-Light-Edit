// 머티리얼 페이지: 색 굽기 (PLAN 3.3). lilToon 메인 색·HSVG(색상·채도·명도·감마)·그라데이션을 메인 텍스처에 구워 넣고 그 값들을 기본으로 되돌린다.
// 계산은 렌더러가 lilToon의 굽기 셰이더로 한다 (ColorBaker) → 구운 PNG는 %LOCALAPPDATA%\ALEdit\baked\에 두고 문서에 새 텍스처(Addr.TexAsset)로 더한다.
// 텍스처 교체 + 값 되돌리기는 실행 취소 한 단계. 올리기·내보내기에서는 구운 PNG가 프로젝트로 복사되고 원본 텍스처의 임포트 설정을 따른다.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MaterialPanel
    {
        static readonly float[] DefaultHsvg = { 0, 1, 1, 1 };
        string? _bakeOut, _bakeMat;

        /// <summary>lilToon Main 섹션 끝에 붙이는 "색 굽기" 줄.</summary>
        Control BakeRow(string mat)
        {
            var btn = Ui.Button("굽기", () => Bake(mat), "지금 색 조정을 메인 텍스처에 구워 넣어요 (실행 취소 가능)", Glyph.Pin);
            btn.Margin = new Avalonia.Thickness(0);
            return Ui.HelpRow("색 굽기", "메인 색·색상/채도/명도/감마·그라데이션을 메인 텍스처에 베이크해 새 PNG로 변경하고, 그 값들은 기본으로 되돌립니다. "
                + "화면은 그대로이고 원본 텍스처 파일은 그대로 유지됩니다. lilToon이 아닌 셰이더로 변경하거나 다른 곳에서 텍스처를 그대로 사용하고 싶을 때 사용합니다.", btn);
        }

        Addr ColorAddr(string mat, string name) => Doc!.Schema(mat)?.Find(name)?.Type == "Vector" ? Addr.MatVector(mat, name) : Addr.MatColor(mat, name);

        static bool Near(float[] a, float[] b) => a.Length >= 4 && Enumerable.Range(0, 4).All(i => Math.Abs(a[i] - b[i]) < 1e-4f);

        void Bake(string mat)
        {
            var doc = Doc;
            if (doc == null) return;
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { _ctx.Status("미리보기가 켜져야 구울 수 있어요."); return; }
            if (_bakeOut != null) { _ctx.Status("굽는 중이에요."); return; }
            var mainKey = doc.Get(Addr.MatTex(mat, "_MainTex")).AsStringOrNull;
            var main = doc.Texture(mainKey);
            if (main == null || !File.Exists(main.File)) { _ctx.Status("메인 텍스처 파일이 없어 구울 수 없어요."); return; }
            var ext = Path.GetExtension(main.File).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") { _ctx.Status($"{ext} 텍스처는 아직 구울 수 없어요 (PNG·JPG만)."); return; }
            var color = doc.Get(ColorAddr(mat, "_Color")).ToFloatArray();
            var hsvg = doc.Get(ColorAddr(mat, "_MainTexHSVG")).ToFloatArray();
            float grad = doc.Get(Addr.MatFloat(mat, "_MainGradationStrength")).AsFloat;
            if (color.Length < 4) color = new float[] { 1, 1, 1, 1 };
            if (hsvg.Length < 4) hsvg = (float[])DefaultHsvg.Clone();
            if (Near(color, new float[] { 1, 1, 1, 1 }) && Near(hsvg, DefaultHsvg) && grad == 0) { _ctx.Status("구울 색 조정이 없어요 (메인 색 흰색 · 색상/채도/명도/감마 기본 · 그라데이션 0)."); return; }
            string? TexFile(string prop) => doc.Texture(doc.Get(Addr.MatTex(mat, prop)).AsStringOrNull)?.File;
            var gradFile = grad > 0 ? TexFile("_MainGradationTex") : null;
            var maskFile = TexFile("_MainColorAdjustMask");
            // 같은 원본·같은 값이면 같은 파일 (다시 구워도 쌓이지 않게)
            var sig = string.Join("|", mainKey, string.Join(",", color), string.Join(",", hsvg), grad, gradFile, maskFile);
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sig))).Substring(0, 16).ToLowerInvariant();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "baked");
            _bakeOut = Path.Combine(dir, Path.GetFileNameWithoutExtension(main.File) + "_baked_" + hash.Substring(0, 8) + ".png");
            _bakeMat = mat;
            var m = Msg.Make(Msg.BakeColor).Set("out", _bakeOut).Set("main", main.File).Set("color", JNode.FloatArray(color)).Set("hsvg", JNode.FloatArray(hsvg)).Set("gradationStrength", grad);
            if (gradFile != null) m.Set("gradation", gradFile);
            if (maskFile != null) m.Set("mask", maskFile);
            _bakeKey = "baked:" + hash; _bakeLike = mainKey!; _bakeName = Path.GetFileNameWithoutExtension(main.Path) + "_baked.png";
            _ctx.Status("색을 굽는 중…");
            _ctx.Render.Send(m);
        }

        string _bakeKey = "", _bakeLike = "", _bakeName = "";

        /// <summary>bakeDone: 새 텍스처를 더하고 메인 텍스처를 바꾸고 색 값들을 기본으로 (한 단계).</summary>
        void OnBakeDone(JNode m)
        {
            if (_bakeOut == null || m["out"].AsString != _bakeOut || Doc == null || _bakeMat == null) return;
            var mat = _bakeMat; var file = _bakeOut;
            _bakeOut = null; _bakeMat = null;
            if (!m["ok"].AsBool) { _ctx.Status("베이크하지 못했습니다: " + m["error"].AsString); return; }
            var doc = Doc;
            var name = doc.Original.FindMaterial(mat)?.Name ?? "머티리얼";
            using (doc.Group("색상 베이크: " + name))
            {
                doc.Set(Addr.TexAsset(_bakeKey), JNode.Obj().Set("file", file).Set("path", _bakeName).Set("like", _bakeLike));
                doc.Set(Addr.MatTex(mat, "_MainTex"), JNode.From(_bakeKey));
                doc.Set(ColorAddr(mat, "_Color"), JNode.FloatArray(new float[] { 1, 1, 1, 1 }));
                doc.Set(ColorAddr(mat, "_MainTexHSVG"), JNode.FloatArray(DefaultHsvg));
                doc.Set(Addr.MatFloat(mat, "_MainGradationStrength"), JNode.From(0f));
                doc.Set(Addr.MatTex(mat, "_MainGradationTex"), JNode.Null);
            }
            _ctx.Status($"색을 구웠어요 ({m["width"].AsInt}×{m["height"].AsInt}) → {Path.GetFileName(file)}. 실행 취소로 되돌려요.");
        }
    }
}
