// 블렌드쉐이프 페이지의 보정 두 가지 (Core.BlinkFixPlan, Protocol/BlinkFix.cs·MouthFix.cs). 메시 편집으로 기록되어 실행 취소·세션·올리기에 들어간다.
//   깜빡임 맞추기 (Blink Fix)  눈 모양을 바꾸는 블렌드쉐이프를 켜면 깜빡임이 덜 감기거나 눈꺼풀이 뚫리는 것을, 깜빡임에서 그 영향을 빼서 고친다
//   입 맞추기 (Mouth Fix)      입 모양을 바꾸는 블렌드쉐이프(mouth_straight 등)를 켜면 입을 벌릴 때 입술이 겹쳐 깨지는 것을, 입 벌리기에서 그 영향을 빼서 고친다
// 섹션 구성은 같다:
//   대상      고칠 블렌드쉐이프 (깜빡임·윙크 / 모음·입 벌리기는 기본으로 체크, 나머지 후보는 직접)
//   편집      지금 값이 0이 아니고 대상이 움직이는 곳을 함께 움직이는 블렌드쉐이프 (자동, 겹침 % 표시)
//   세기      뺄 정도 (100% = 대상 100에서 원래 모양)   ·   범위 제한(전문가) 0 = 쪽 전체, 올리면 대상이 많이 움직이는 곳만
//   되돌리기  보정을 빼서 대상 블렌드쉐이프를 원래대로
//   미리보기  기록하지 않고 고른 대상 하나만 100으로 (나머지 대상은 0) — 여러 개를 한꺼번에 올리면 겹친다
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel
    {
        /// <summary>보정 한 가지의 글·규칙과 섹션 상태. 접힘·미리보기는 다시 그려도(적용 뒤 등) 이어 가도록 정적.</summary>
        sealed class FixKind
        {
            public string Op = "", Title = "", Intro = "", TargetLabel = "", TargetDesc = "", SourceLabel = "", SourceDesc = "", Area = "", NoSources = "";
            public string StrengthDesc = "", FalloffDesc = "", ApplyLabel = "", Applied = "", Reverted = "", RevertTip = "", ViewTip = "", PickTargets = "";
            public Func<Document, IrRenderer, MeshData, List<string>> Targets = (_, __, ___) => new List<string>();
            public Func<string, bool> DefaultTarget = _ => false;
            public string[] PreferPreview = Array.Empty<string>();
            public bool PerEye;   // 눈마다 세기 · 자동 모드 (Blink Fix만)
            public Func<IEnumerable<string>, IEnumerable<(string, float)>, float, float, JNode> Make = (_, __, ___, ____) => JNode.Obj();
            // 섹션 상태
            public bool Open, ViewOn; public string? ViewName;   // Open: 처음엔 접힘 (2026-10-20)
            public IrRenderer? Renderer; public List<string> Previewing = new List<string>(); public Button? Revert;
            public readonly TextBlock State = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        }

        static readonly FixKind Blink = new FixKind
        {
            Op = MeshOps.BlinkFix, Title = "깜빡임 맞추기 (Blink Fix)",
            Intro = "눈 크기·눈매 같은 블렌드쉐이프를 바꾸면 깜빡임이 덜 감기거나 눈꺼풀이 눈을 뚫을 수 있어요. 깜빡임 블렌드쉐이프에서 그 영향을 빼서, 감으면 원래 감은 눈이 되게 해요. 한쪽 눈 윙크는 그쪽 눈만 고쳐요. 원본 메시는 그대로고, 올리기에도 들어가요.",
            TargetLabel = "깜빡임", TargetDesc = "고칠 눈 감기 블렌드쉐이프 (웃는 눈처럼 눈을 감는 표정도 고를 수 있어요)",
            SourceLabel = "눈 편집", SourceDesc = "깜빡임에서 영향을 뺄 블렌드쉐이프 (지금 값 기준)", Area = "눈 둘레",
            NoSources = "눈 둘레를 바꾸는 블렌드쉐이프가 없어요 (값이 0이 아닌 것만 봐요)",
            StrengthDesc = "100 = 깜빡임 100에서 원래 감은 눈 자리. 덜 감기면 올리고, 너무 감기면 내려요",
            FalloffDesc = "0 = 감는 눈 쪽 전체에서 빼요(보통). 올릴수록 깜빡임이 많이 움직이는 눈꺼풀 둘레만 고쳐요",
            ApplyLabel = "깜빡임 맞추기", Applied = "깜빡임 {0}개에서 눈 편집 {1}개의 영향을 뺐어요. \"100으로 보기\"로 확인해요.",
            Reverted = "깜빡임 블렌드쉐이프를 Blink Fix 전으로 되돌렸어요 (실행 취소로 다시 적용).", RevertTip = "Blink Fix로 고친 깜빡임 블렌드쉐이프를 원래대로 되돌려요",
            ViewTip = "고른 깜빡임 하나만 100으로 보여 줘요 (다른 깜빡임은 0). 기록하지 않아요", PickTargets = "고칠 깜빡임 블렌드쉐이프를 골라요.",
            Targets = (_, __, mesh) => BlinkFixPlan.Targets(mesh), DefaultTarget = MeshOps.IsPlainBlink,
            PreferPreview = new[] { "blink", "まばたき", "eye_blink_1", "eyeclosed", "vrc.blink", "eye_blink" },
            Make = (t, s, k, f) => MeshOps.MakeBlinkFix(t, s, k, f), PerEye = true,
        };

        static readonly FixKind Mouth = new FixKind
        {
            Op = MeshOps.MouthFix, Title = "입 맞추기 (Mouth Fix)",
            Intro = "mouth_straight처럼 다문 입 모양을 바꾸는 블렌드쉐이프를 켜 두면, 입을 벌릴 때(모음·あいうえお) 두 모양이 더해져 입술이 겹치거나 깨져 보여요. 입 벌리는 블렌드쉐이프에서 그 영향을 빼서, 벌리면 원래 벌린 입이 되게 해요. 다문 입은 그대로예요. 원본 메시는 그대로고, 올리기에도 들어가요.",
            TargetLabel = "입 벌리기", TargetDesc = "고칠 입 블렌드쉐이프 (모음·입 벌리기는 기본으로 골라요. 지금 값이 0이 아닌 것은 편집 쪽에 나와요)",
            SourceLabel = "입 편집", SourceDesc = "입 벌리기에서 영향을 뺄 블렌드쉐이프 (지금 값 기준)", Area = "입 둘레",
            NoSources = "입 둘레를 바꾸는 블렌드쉐이프가 없어요 (mouth_straight처럼 값이 0이 아닌 것만 봐요)",
            StrengthDesc = "100 = 입 벌리기 100에서 원래 벌린 입. 아직 깨져 보이면 올리고, 입 편집이 너무 빨리 사라지면 내려요",
            FalloffDesc = "0 = 입 벌리기가 움직이는 쪽 전체에서 빼요(보통). 올릴수록 입술처럼 많이 움직이는 곳만 고쳐요",
            ApplyLabel = "입 맞추기", Applied = "입 벌리기 {0}개에서 입 편집 {1}개의 영향을 뺐어요. \"100으로 보기\"로 확인해요.",
            Reverted = "입 블렌드쉐이프를 Mouth Fix 전으로 되돌렸어요 (실행 취소로 다시 적용).", RevertTip = "Mouth Fix로 고친 입 블렌드쉐이프를 원래대로 되돌려요",
            ViewTip = "고른 입 벌리기 하나만 100으로 보여 줘요 (다른 것은 0). 기록하지 않아요", PickTargets = "고칠 입 벌리기 블렌드쉐이프를 골라요.",
            Targets = BlinkFixPlan.MouthTargets, DefaultTarget = MeshOps.IsMouthOpenName,
            PreferPreview = new[] { "vrc.v_aa", "あ", "mouth_a_1", "jawopen", "mouthopen" },
            Make = (t, s, k, f) => MeshOps.MakeMouthFix(t, s, k, f),
        };

        static readonly FixKind[] Fixes = { Blink, Mouth };

        IEnumerable<Control> FixSections()
        {
            foreach (var k in Fixes) if (FixSection(k) is Control c) yield return c;
        }

        void EndFixPreviews() { foreach (var k in Fixes) EndFixPreview(k); }
        void RefreshFixStates() { foreach (var k in Fixes) RefreshFixState(k); }

        Control? FixSection(FixKind k)
        {
            EndFixPreview(k);
            k.Renderer = null;
            if (Doc == null || _ctx.Meshes == null || _renderer == null) return null;
            var r = Doc.Original.FindRenderer(_renderer);
            var mesh = r == null ? null : _ctx.Meshes.Original(r.Mesh);
            if (r == null || mesh == null) return null;
            var targets = k.Targets(Doc, r, mesh);
            var cur = BlinkFixPlan.Current(Doc, r.Mesh, k.Op);
            // 적용한 대상은 지금 값이 바뀌어도 목록에 남긴다
            if (cur != null) foreach (var t in cur["targets"].Items.Select(x => x.AsString)) if (!targets.Contains(t) && mesh.Shapes.Any(s => s.Name == t)) targets.Add(t);
            if (targets.Count == 0) return null;
            k.Renderer = r;
            var curTargets = cur?["targets"].Items.Select(x => x.AsString).ToHashSet();
            var curSources = cur?["sources"].Items.Select(x => x["name"].AsString).ToHashSet();

            var fold = new Ui.FoldSection(k.Title, k.Open, help: k.Intro);
            fold.Toggled += o => k.Open = o;
            var body = fold.Body;

            // 대상
            var tWrap = new WrapPanel();
            var tChecks = new List<(CheckBox Box, string Name)>();
            foreach (var t in targets)
            {
                var cb = new LiteCheck { Content = t, IsChecked = curTargets?.Contains(t) ?? k.DefaultTarget(t), Margin = new Thickness(0, 0, 12, 0) };
                tChecks.Add((cb, t)); tWrap.Children.Add(cb);
            }
            var rows = new StackPanel();
            CheckBox? auto = null;
            if (k.PerEye)
            {
                auto = new CheckBox { Content = "자동", IsChecked = cur?["auto"].AsBool == true };
                rows.Children.Add(Ui.HelpRow("자동 모드", "이름으로 선택합니다: 깜빡임은 Blink·Wink·まばたき, 눈 편집은 이름에 Eye·目·눈이 들어가고 지금 값이 0이 아닌 것. "
                    + "좌우는 이름의 L·R·Left·Right·左·右로 구분합니다. 켜 두면 눈 편집 값을 변경할 때마다 깜빡임을 그 값에 맞춰 다시 수정합니다 (같은 실행 취소 단계).", auto));
            }
            rows.Children.Add(Ui.BlockRow(k.TargetLabel, k.TargetDesc, tWrap));

            // 편집
            var sWrap = new StackPanel();
            var sChecks = new List<(CheckBox Box, BlinkFixPlan.Source Src)>();
            var found = BlinkFixPlan.Sources(Doc, r, mesh, targets);
            if (found.Count == 0) { var none = Ui.Text(k.NoSources, 12, FontWeight.Normal, Ui.KText2); none.TextWrapping = TextWrapping.Wrap; sWrap.Children.Add(none); }   // 영어·일본어는 길어 줄바꿈 (2026-11-02)
            foreach (var s in found)
            {
                var cb = new LiteCheck { Content = $"{s.Name}  ·  {s.Weight:0.#}  ·  {k.Area} {s.Overlap * 100:0}%", IsChecked = curSources?.Contains(s.Name) ?? s.Overlap >= BlinkFixPlan.DefaultOverlap };
                ToolTip.SetTip(cb, $"{k.Area} % = 이 블렌드쉐이프가 움직이는 양 중 고칠 블렌드쉐이프({k.TargetLabel})가 움직이는 곳 안의 몫. 낮으면 얼굴 전체를 바꾸는 것이라 빼지 않는 편이 좋아요");
                sChecks.Add((cb, s)); sWrap.Children.Add(cb);
            }
            rows.Children.Add(Ui.BlockRow(k.SourceLabel, k.SourceDesc, sWrap));

            // 세기 · 범위 제한
            double strength = cur != null ? cur["strength"].AsDouble * 100 : 100, falloff = cur != null && cur.Has("falloff") ? cur["falloff"].AsDouble * 100 : 0;
            double eyeL = strength * (cur != null && cur.Has("left") ? cur["left"].AsDouble : 1), eyeR = strength * (cur != null && cur.Has("right") ? cur["right"].AsDouble : 1);
            Control StrengthRow(string name, string help, double init, Action<double> set)
            {
                var box = Ui.NumberCell(v => set(Math.Clamp(v, 0, 150)), () => { });
                var sl = new Ui.EditSlider(0, 150, v => { set(v); box.Text = Ui.F(v); }, v => { set(v); box.Text = Ui.F(v); });
                sl.SetQuiet(init); box.Text = Ui.F(init);
                return Ui.HelpRow(name, help, Ui.NumberWithSlider(box, sl.Slider), wide: true);
            }
            if (k.PerEye)
            {
                rows.Children.Add(StrengthRow("왼쪽 눈 (%)", k.StrengthDesc + ". 0이면 왼쪽 눈은 수정하지 않습니다 (아바타 기준 왼쪽)", eyeL, v => eyeL = v));
                rows.Children.Add(StrengthRow("오른쪽 눈 (%)", k.StrengthDesc + ". 0이면 오른쪽 눈은 수정하지 않습니다 (아바타 기준 오른쪽)", eyeR, v => eyeR = v));
            }
            else rows.Children.Add(StrengthRow("세기 (%)", k.StrengthDesc, strength, v => strength = v));
            var fBox = Ui.NumberCell(v => falloff = Math.Clamp(v, 0, 100), () => { });
            var fSlider = new Ui.EditSlider(0, 100, v => { falloff = v; fBox.Text = Ui.F(v); }, v => { falloff = v; fBox.Text = Ui.F(v); });
            fSlider.SetQuiet(falloff); fBox.Text = Ui.F(falloff);
            rows.Children.Add(Ui.Expert(Ui.HelpRow("범위 제한 (%)", k.FalloffDesc, Ui.NumberWithSlider(fBox, fSlider.Slider), wide: true)));
            body.Children.Add(Ui.Card(rows));

            // 단추
            var btns = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            int leftX = k.PerEye ? BlinkFixPlan.LeftX(mesh) : -1;
            // 세기: 양쪽이 같으면 strength 하나, 다르면 strength 1 + 눈마다
            JNode Compose(IEnumerable<string> t, IEnumerable<(string, float)> s)
            {
                if (!k.PerEye) return k.Make(t, s, (float)(strength / 100), (float)(falloff / 100));
                if (Math.Abs(eyeL - eyeR) < 0.01) return k.Make(t, s, (float)(eyeL / 100), (float)(falloff / 100));
                return MeshOps.WithEyes(k.Make(t, s, 1f, (float)(falloff / 100)), (float)(eyeL / 100), (float)(eyeR / 100), leftX);
            }
            bool IsAuto() => auto?.IsChecked == true;
            void ShowAuto()
            {
                bool on = IsAuto();
                foreach (var x in tChecks) x.Box.IsEnabled = !on;
                foreach (var x in sChecks) x.Box.IsEnabled = !on;
                if (!on) return;
                var (at, asrc) = BlinkFixPlan.AutoPick(Doc!, r, mesh);
                foreach (var x in tChecks) x.Box.IsChecked = at.Contains(x.Name);
                foreach (var x in sChecks) x.Box.IsChecked = asrc.Any(y => y.Name == x.Src.Name);
            }
            void ApplyAuto(string label)
            {
                var op = BlinkFixPlan.AutoOp(Doc!, r, mesh, Compose(Array.Empty<string>(), Array.Empty<(string, float)>()));
                if (op == null) { _ctx.Status("이름으로 깜빡임 블렌드쉐이프(Blink·Wink·まばたき)를 찾지 못했어요. 자동을 끄고 직접 골라요."); return; }
                BlinkFixPlan.Set(Doc!, r.Mesh, op, label, k.Op);
                _ctx.Status($"자동 깜빡임 맞추기: 깜빡임 {op["targets"].Count}개 · 눈 편집 {op["sources"].Count}개. 눈 편집 값을 바꾸면 따라 고쳐요.");
            }
            if (auto != null)
            {
                auto.IsCheckedChanged += (_, __) =>
                {
                    ShowAuto();
                    if (IsAuto()) { ApplyAuto("깜빡임 자동 맞추기 켜기"); return; }
                    var c = BlinkFixPlan.Current(Doc!, r.Mesh, k.Op);
                    if (c != null && c["auto"].AsBool)
                    {
                        var off = c.Clone(); off.Remove("auto");
                        BlinkFixPlan.Set(Doc!, r.Mesh, off, "깜빡임 자동 맞추기 끄기", k.Op);
                        _ctx.Status("자동을 껐어요. 지금 고친 깜빡임은 그대로예요.");
                    }
                };
                ShowAuto();
            }
            var apply = Ui.Button(cur != null ? "다시 맞추기" : "적용", () =>
            {
                if (IsAuto()) { ApplyAuto(k.ApplyLabel); return; }
                var t = tChecks.Where(x => x.Box.IsChecked == true).Select(x => x.Name).ToList();
                var s = sChecks.Where(x => x.Box.IsChecked == true).Select(x => (x.Src.Name, x.Src.Weight)).ToList();
                if (t.Count == 0) { _ctx.Status(k.PickTargets); return; }
                if (s.Count == 0) { _ctx.Status($"뺄 {k.SourceLabel} 블렌드쉐이프가 없어요."); return; }
                BlinkFixPlan.Set(Doc!, r.Mesh, Compose(t, s), k.ApplyLabel, k.Op);
                _ctx.Status(string.Format(k.Applied, t.Count, s.Count));
            }, $"고른 대로 {k.TargetLabel} 블렌드쉐이프를 고쳐요 (실행 취소 가능)", Glyph.Pin);
            btns.Children.Add(apply);
            k.Revert = Ui.Button("되돌리기", () =>
            {
                if (BlinkFixPlan.Current(Doc!, r.Mesh, k.Op) == null) return;
                BlinkFixPlan.Set(Doc!, r.Mesh, null, k.ApplyLabel + " 되돌리기", k.Op);
                _ctx.Status(k.Reverted);
            }, k.RevertTip, Glyph.Reset);
            k.Revert.IsEnabled = cur != null;
            btns.Children.Add(k.Revert);
            body.Children.Add(btns);

            // 미리보기: 하나만 100
            var pick = Ui.CellCombo(new ComboBox { ItemsSource = targets, SelectedIndex = Math.Max(0, targets.IndexOf(k.ViewName != null && targets.Contains(k.ViewName) ? k.ViewName : DefaultPreview(k, targets))), MinWidth = 160 });
            var view = new CheckBox { Content = "100으로 보기", IsChecked = k.ViewOn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(view, k.ViewTip);
            void ShowPreview()
            {
                EndFixPreview(k);
                k.ViewOn = view.IsChecked == true; k.ViewName = pick.SelectedItem as string;
                if (k.ViewOn && k.ViewName != null) StartFixPreview(k, targets, k.ViewName);
            }
            view.IsCheckedChanged += (_, __) => ShowPreview();
            pick.SelectionChanged += (_, __) => ShowPreview();
            var prow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var plabel = Ui.Text("미리보기", 13); plabel.VerticalAlignment = VerticalAlignment.Center;
            prow.Children.Add(plabel); prow.Children.Add(new Border { Width = 8 });
            prow.Children.Add(pick); prow.Children.Add(view);
            body.Children.Add(prow);
            if (k.ViewOn) ShowPreview();

            k.State.Margin = new Thickness(0, 6, 0, 0); k.State.TextWrapping = TextWrapping.Wrap;
            if (k.State.Parent is Panel old) old.Children.Remove(k.State);
            body.Children.Add(k.State);
            RefreshFixState(k);
            return fold;
        }

        /// <summary>미리보기 기본: 흔한 이름부터, 없으면 기본 대상 중 좌우 표시가 없는 것.</summary>
        static string DefaultPreview(FixKind k, List<string> targets)
        {
            foreach (var p in k.PreferPreview) { var f = targets.FirstOrDefault(t => t.ToLowerInvariant() == p); if (f != null) return f; }
            return targets.FirstOrDefault(t => k.DefaultTarget(t) && !Sided(t)) ?? targets[0];
        }

        static bool Sided(string name)
        {
            var n = name.ToLowerInvariant();
            return n.EndsWith("_l") || n.EndsWith("_r") || n.EndsWith(".l") || n.EndsWith(".r") || n.EndsWith("left") || n.EndsWith("right")
                || n.Contains("右") || n.Contains("左") || n.Contains("ウィンク") || n.Contains("ｳｨﾝｸ") || n.Contains("wink");
        }

        /// <summary>기록하지 않고 고른 대상 하나만 100, 나머지 대상은 0으로 보여 준다.</summary>
        void StartFixPreview(FixKind k, List<string> targets, string one)
        {
            if (Doc == null || k.Renderer == null) return;
            foreach (var t in targets) Doc.Preview(Addr.BlendShape(k.Renderer.Id, t), JNode.From(t == one ? 100 : 0));
            k.Previewing = targets.ToList();
        }

        /// <summary>미리보기로 바꾼 값을 지금 기록된 값으로 되돌린다.</summary>
        void EndFixPreview(FixKind k)
        {
            if (Doc != null && k.Renderer != null)
                foreach (var t in k.Previewing) { var a = Addr.BlendShape(k.Renderer.Id, t); Doc.Preview(a, Doc.Get(a)); }
            k.Previewing = new List<string>();
        }

        void RefreshFixState(FixKind k)
        {
            if (Doc == null || k.Renderer == null) return;
            var cur = BlinkFixPlan.Current(Doc, k.Renderer.Mesh, k.Op);
            if (k.Revert != null) k.Revert.IsEnabled = cur != null;
            if (cur == null) { k.State.Text = $"꺼짐 (원래 {k.TargetLabel})"; k.State.Fg(Ui.KText2); return; }
            bool autoOn = cur["auto"].AsBool;
            bool stale = !autoOn && BlinkFixPlan.Stale(Doc, k.Renderer, cur);
            double st = cur["strength"].AsDouble * 100;
            var power = cur.Has("left") ? $"왼쪽 {st * cur["left"].AsDouble:0}% · 오른쪽 {st * cur["right"].AsDouble:0}%" : $"세기 {st:0}%";
            k.State.Text = $"켜짐{(autoOn ? " (자동)" : "")} · {k.TargetLabel} {cur["targets"].Count}개 · {k.SourceLabel} {cur["sources"].Count}개 · {power}"
                + (autoOn && cur["sources"].Count > 0 ? "\n" + k.SourceLabel + ": " + string.Join(", ", cur["sources"].Items.Select(x => x["name"].AsString)) : "")
                + (stale ? $" — {k.SourceLabel} 값이 바뀌었습니다. \"다시 맞추기\"를 누르세요" : "");
            k.State.Fg(stale ? Ui.KCaution : Ui.KText2);
        }
    }
}
