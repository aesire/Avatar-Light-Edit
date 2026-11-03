// 조명 배치판 (2026-10-24 사용자: 조명을 직접 배치하고 조정): 환경 페이지 "조명" 탭 맨 위.
//   아바타를 위에서 본 판 — 가운데가 아바타, 아래쪽이 아바타 정면(카메라에서 보는 그대로 좌우: 아바타 오른손 쪽이 왼쪽).
//   ☀ 주광: 판 위 자리 = 빛이 오는 방향, 가운데에 가까울수록 높이 뜬 해(가장자리 = 지평선). 끌면 환경 조명의 방향·높이(yaw·pitch, 프리셋 기준 더하기)가 바뀐다.
//   ◇ 정면광: 아바타 정면 10° 높이에 고정된 그림자 없는 방향광 — 세기만 (front 배율).
//   1~8 추가 조명: 판 위 자리 = 둘레 각도·거리(가장자리 = 3 m), 높이·세기는 아래 막대. 켜진 것만 판과 단추에 보인다.
//   "+ 포인트 라이트" (2026-10-24): 꺼진 칸 하나를 흰 포인트로 켜 아바타 앞 왼쪽에 둔다. "카메라 자리에 놓기": 지금 미리보기 카메라 자리로 (각도·거리·높이),
//   "카메라 따라가기": 렌더러가 매 프레임 카메라 자리에 둔다(판에는 카메라 자리로 그림).
//   끄는 동안은 미리보기(기록 없음), 놓으면 실행 취소 한 번으로 기록한다. 고른 것은 다시 그려도 유지.
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class LightPad : StackPanel
    {
        // 렌더러 UnityLighting 프리셋의 주광 방향·높이 (Unity/Assets/Renderer/UnityLighting.cs와 같게)
        static (double Yaw, double Pitch)? Base(string preset) => preset switch
        {
            "indoor" => (160, 40), "sunset" => (120, 12), "night" => (210, 55), "dark" => (200, 35),
            "flat" or "none" => null, _ => (150, 50),
        };
        const double MaxDist = 3;   // 판 가장자리 = 3 m
        const int Count = 8;          // 추가 조명 칸 (렌더러 LightSlots.Count)
        static string _sel = "sun";

        readonly Func<JNode> _current, _camera;
        readonly Action<JNode, string, bool> _commit;
        readonly Pad _pad;
        readonly StackPanel _detail = new StackPanel();
        readonly WrapPanel _pick = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        JNode? _live;   // 끄는 동안의 목록 (미리보기는 문서에 남지 않으므로 점은 이것으로 그린다)
        JNode Cur() => _live ?? _current();

        /// <param name="current">지금 환경 목록.</param>
        /// <param name="commit">(목록, 이름, 미리보기만) — 환경 페이지의 Commit.</param>
        /// <param name="camera">미리보기 카메라 상태 (cameraChanged의 state: pivot·yaw·pitch·distance).</param>
        public LightPad(Func<JNode> current, Action<JNode, string, bool> commit, Func<JNode> camera)
        {
            _current = current; _commit = commit; _camera = camera;
            _pad = new Pad(this) { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 6) };
            Children.Add(_pad);
            Children.Add(_pick);
            Children.Add(_detail);
            // 카메라를 따라가는 조명은 카메라가 움직이면 판의 점도 옮긴다 (카메라 상태에는 바뀜 알림이 없어 0.3초마다 다시 그림)
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(0.3) };
            // 페이지는 숨어도 붙어 있다 (MainWindow.ShowPage) — 안 보이면 쉰다
            timer.Tick += (_, __) => { if (!IsEffectivelyVisible) return; for (int i = 1; i <= Count; i++) if (LightOn(i) && Follow(i)) { _pad.InvalidateVisual(); break; } };
            AttachedToVisualTree += (_, __) => timer.Start();
            DetachedFromVisualTree += (_, __) => timer.Stop();
            Refresh();
        }

        // ---- 값 읽기 ----
        JNode? Entry(JNode list, string id) { foreach (var e in list.Items) if (e["provider"].AsString == id) return e; return null; }
        double P(string id, string name, double def)
        {
            var e = Entry(Cur(), id);
            return e != null && e["params"].Has(name) ? e["params"][name].AsDouble : def;
        }
        bool B(string id, string name, bool def)
        {
            var e = Entry(Cur(), id);
            return e != null && e["params"].Has(name) ? e["params"][name].AsBool : def;
        }
        string Preset() { var e = Entry(Cur(), "UnityLighting"); return e != null && e["params"].Has("preset") ? e["params"]["preset"].AsString : "bright"; }
        static double DefAround(int i) => i == 1 ? 45 : i == 2 ? -60 : i == 3 ? 180 : 0;
        bool Follow(int i) => B("Lights", $"l{i}_follow", false);

        /// <summary>미리보기 카메라 월드 자리 (OrbitCamera: pivot − Euler(pitch, yaw) · 앞 × 거리). 모르면 null.</summary>
        (double X, double Y, double Z)? CameraPos()
        {
            var c = _camera();
            if (c.Kind != JKind.Object || !c.Has("pivot")) return null;
            var p = c["pivot"].ToFloatArray();
            if (p.Length < 3) return null;
            double y = c["yaw"].AsDouble * Math.PI / 180, pt = c["pitch"].AsDouble * Math.PI / 180, d = c["distance"].AsDouble;
            return (p[0] - Math.Sin(y) * Math.Cos(pt) * d, p[1] + Math.Sin(pt) * d, p[2] - Math.Cos(y) * Math.Cos(pt) * d);
        }
        bool LightOn(int i) => B("Lights", $"l{i}_on", i == 1) && ProviderOn("Lights");
        bool ProviderOn(string id) { var e = Entry(Cur(), id); return e != null && (!e.Has("enabled") || e["enabled"].AsBool); }

        /// <summary>값 몇 개를 바꾼 목록 (효과가 없으면 만들고, 꺼져 있으면 켠다).</summary>
        JNode With(string id, params (string Name, JNode Value)[] sets)
        {
            var list = Cur().Clone();
            var e = Entry(list, id);
            if (e == null) { e = JNode.Obj().Set("provider", id).Set("version", 1).Set("params", JNode.Obj()); list.Add(e); }
            e.Remove("enabled");
            if (e["params"].Kind != JKind.Object) e.Set("params", JNode.Obj());
            foreach (var (n, v) in sets) e["params"].Set(n, v);
            return list;
        }

        // ---- 판 위 자리 ----
        /// <summary>주광이 오는 방향(가로)·높이(도). 프리셋에 주광이 없으면 null.</summary>
        (double Yaw, double Elev)? Sun()
        {
            if (!(Base(Preset()) is (double by, double bp))) return null;
            return (by + P("UnityLighting", "yaw", 0), Math.Clamp(bp + P("UnityLighting", "pitch", 0), -89, 89));
        }

        Point SunPoint(Point c, double R, double yaw, double elev)
        {
            double r = R * (1 - Math.Clamp(elev, 0, 90) / 90), y = yaw * Math.PI / 180;
            double fx = -Math.Sin(y), fz = -Math.Cos(y);   // 빛이 오는 쪽 (월드 x, z)
            return new Point(c.X - fx * r, c.Y + fz * r);
        }

        Point LightPoint(Point c, double R, int i)
        {
            double k = R / MaxDist;
            if (Follow(i) && CameraPos() is (double cx, double _, double cz))
            {
                double cd = Math.Sqrt(cx * cx + cz * cz), s = cd > MaxDist ? MaxDist / cd : 1;
                return new Point(c.X - cx * s * k, c.Y + cz * s * k);
            }
            double a = P("Lights", $"l{i}_around", DefAround(i)) * Math.PI / 180, d = Math.Min(MaxDist, P("Lights", $"l{i}_distance", 1.2));
            return new Point(c.X - Math.Sin(a) * d * k, c.Y + Math.Cos(a) * d * k);
        }

        /// <summary>판 위 자리로 옮긴 목록 (끌기).</summary>
        JNode? MoveTo(string key, Point p, Point c, double R)
        {
            double dx = p.X - c.X, dy = p.Y - c.Y;
            if (key == "sun")
            {
                if (!(Base(Preset()) is (double by, double bp))) return null;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double elev = 90 * (1 - Math.Min(d, R) / R);
                double fx = d > 1e-6 ? -dx / d : 0, fz = d > 1e-6 ? dy / d : 1;
                double yaw = Math.Atan2(-fx, -fz) * 180 / Math.PI;
                double dyaw = Wrap(yaw - by), dpitch = Math.Clamp(elev - bp, -60, 60);
                return With("UnityLighting", ("yaw", JNode.From(Math.Round(dyaw, 1))), ("pitch", JNode.From(Math.Round(dpitch, 1))));
            }
            if (key.StartsWith("l"))
            {
                int i = key[1] - '0';
                double k = R / MaxDist, x = -dx / k, z = dy / k;
                double around = Math.Atan2(x, z) * 180 / Math.PI, dist = Math.Min(MaxDist, Math.Sqrt(x * x + z * z));
                return With("Lights", ($"l{i}_on", JNode.From(true)), ($"l{i}_follow", JNode.From(false)), ($"l{i}_around", JNode.From(Math.Round(around, 1))), ($"l{i}_distance", JNode.From(Math.Round(dist, 2))));
            }
            return null;
        }

        void AddLight(int i)
        {
            _sel = $"l{i}";
            _commit(With("Lights", ($"l{i}_on", JNode.From(true)), ($"l{i}_type", JNode.From("point")), ($"l{i}_color", JNode.FloatArray(new float[] { 1, 1, 1 })),
                ($"l{i}_intensity", JNode.From(1.0)), ($"l{i}_range", JNode.From(4.0)), ($"l{i}_around", JNode.From(30.0)), ($"l{i}_height", JNode.From(1.3)),
                ($"l{i}_distance", JNode.From(1.2)), ($"l{i}_follow", JNode.From(false))), $"포인트 라이트 {i} 추가", false);
        }

        void PlaceAtCamera(int i)
        {
            if (!(CameraPos() is (double x, double y, double z))) return;
            double around = Math.Atan2(x, z) * 180 / Math.PI, dist = Math.Min(10, Math.Sqrt(x * x + z * z));
            _sel = $"l{i}";
            _commit(With("Lights", ($"l{i}_on", JNode.From(true)), ($"l{i}_follow", JNode.From(false)), ($"l{i}_around", JNode.From(Math.Round(around, 1))),
                ($"l{i}_distance", JNode.From(Math.Round(dist, 2))), ($"l{i}_height", JNode.From(Math.Round(Math.Clamp(y, -1, 4), 2)))), $"조명 {i} 카메라 자리에", false);
        }

        static double Wrap(double a) { a %= 360; if (a > 180) a -= 360; if (a < -180) a += 360; return a; }

        // ---- 고른 것의 막대 ----
        void Refresh()
        {
            // 고르는 단추: 주광 · 정면광 · 켜진 추가 조명(+ 고른 것) · "+ 포인트 라이트"
            _pick.Children.Clear();
            void Pick(string key, string label)
            {
                var b = Ui.Button(label, () => { _sel = key; Refresh(); }, null);
                b.Margin = new Thickness(2); b.Padding = new Thickness(8, 4); b.MinHeight = 0;
                b.Classes.Set("accent", key == _sel);
                _pick.Children.Add(b);
            }
            Pick("sun", "☀ 주광"); Pick("front", "정면광");
            for (int i = 1; i <= Count; i++) if (LightOn(i) || _sel == $"l{i}") Pick($"l{i}", $"조명 {i}");
            int free = 0;
            for (int i = 1; i <= Count && free == 0; i++) if (!LightOn(i)) free = i;
            if (free > 0)
            {
                var add = Ui.Button("+ 포인트 라이트", () => AddLight(free), "꺼진 칸 하나를 흰 포인트 라이트로 켜 아바타 앞에 둬요. 판에서 끌어 옮기거나 \"카메라 자리에 놓기\"를 눌러요");
                add.Margin = new Thickness(2); add.Padding = new Thickness(8, 4); add.MinHeight = 0;
                _pick.Children.Add(add);
            }
            _detail.Children.Clear();
            void Slider(string label, double min, double max, double value, Func<double, JNode> make, string undo, string tip)
            {
                var s = new Ui.EditSlider(min, max, v => _commit(make(v), "", true), v => _commit(make(v), undo, false));
                s.Slider.Value = value;
                var row = Ui.PropRow(Ui.Text(label), s.Slider, null);
                ToolTip.SetTip(row, tip);
                _detail.Children.Add(row);
            }
            switch (_sel)
            {
                case "sun":
                    if (Sun() is (double _, double elev))
                    {
                        var bp = Base(Preset())!.Value.Pitch;
                        Slider("높이 (°)", Math.Max(0, bp - 60), Math.Min(89, bp + 60), elev, v => With("UnityLighting", ("pitch", JNode.From(Math.Round(v - bp, 1)))), "주광 높이", "해가 떠 있는 높이. 판에서 가운데로 끌어도 높아져요");
                        Slider("세기", 0, 3, P("UnityLighting", "intensity", 1), v => With("UnityLighting", ("intensity", JNode.From(Math.Round(v, 2)))), "주광 세기", "주광 세기 배율");
                    }
                    else _detail.Children.Add(Ui.Wrap("이 조명 프리셋에는 주광이 없어요 (그림자 없는 고른 빛 · 라이트 없음)."));
                    break;
                case "front":
                    Slider("세기", 0, 3, P("UnityLighting", "front", 1), v => With("UnityLighting", ("front", JNode.From(Math.Round(v, 2)))), "정면광 세기",
                        "아바타 정면에서 비추는 그림자 없는 빛 — 얼굴 음영을 메워요 (밝은 월드·실내·노을 프리셋에만, 0이면 끔)");
                    break;
                default:
                    {
                        int i = _sel[1] - '0';
                        var on = new Ui.Switch(v => _commit(With("Lights", ($"l{i}_on", JNode.From(v))), v ? $"조명 {i} 켜기" : $"조명 {i} 끄기", false));
                        on.SetQuiet(LightOn(i));
                        _detail.Children.Add(Ui.PropRow(Ui.Text("켜기"), on.Control, null));
                        var place = Ui.Button("카메라 자리에 놓기", () => PlaceAtCamera(i), "지금 미리보기 카메라가 있는 자리로 이 조명을 옮겨요 (카메라 쪽에서 비춰요)");
                        place.Padding = new Thickness(10, 4); place.MinHeight = 0;
                        place.IsEnabled = CameraPos() != null;
                        var follow = new Ui.Switch(v => _commit(With("Lights", ($"l{i}_on", JNode.From(true)), ($"l{i}_follow", JNode.From(v))), v ? $"조명 {i} 카메라 따라가기" : $"조명 {i} 따라가기 끄기", false));
                        follow.SetQuiet(Follow(i));
                        ToolTip.SetTip(follow.Control, "켜면 카메라를 돌려도 조명이 늘 카메라 자리에서 비춰요");
                        _detail.Children.Add(Ui.PropRow(Ui.Text("카메라"), place, null));
                        _detail.Children.Add(Ui.PropRow(Ui.Text("카메라 따라가기"), follow.Control, null));
                        Slider("높이 (m)", -1, 4, P("Lights", $"l{i}_height", 1.5), v => With("Lights", ($"l{i}_height", JNode.From(Math.Round(v, 2)))), $"조명 {i} 높이", "바닥에서 조명까지 높이");
                        Slider("세기", 0, 8, P("Lights", $"l{i}_intensity", 1), v => With("Lights", ($"l{i}_intensity", JNode.From(Math.Round(v, 2)))), $"조명 {i} 세기", "가까울수록 더 밝아요 (닿는 거리는 아래 추가 조명 탭에서)");
                        break;
                    }
            }
            _pad.InvalidateVisual();
        }

        sealed class Pad : Control
        {
            readonly LightPad _o;
            string? _drag;
            public Pad(LightPad o) { _o = o; ClipToBounds = true; Cursor = new Cursor(StandardCursorType.Hand); }

            Point Center => new Point(Bounds.Width / 2, Bounds.Height / 2);
            double R => Math.Min(Bounds.Width, Bounds.Height) / 2 - 14;

            Color C(string key, Color fallback) => this.TryFindResource(key, ActualThemeVariant, out var o) && o is ISolidColorBrush b ? b.Color : fallback;

            /// <summary>그릴 점들: (열쇠, 자리, 색, 글자, 켜짐).</summary>
            (string Key, Point P, Color Col, string Text, bool On)[] Dots()
            {
                var c = Center; double R = this.R;
                var list = new System.Collections.Generic.List<(string, Point, Color, string, bool)>();
                if (_o.Sun() is (double yaw, double elev)) list.Add(("sun", _o.SunPoint(c, R, yaw, elev), Color.FromRgb(0xFF, 0xC9, 0x4D), "☀", true));
                bool frontOn = _o.Sun() != null && _o.Preset() is "bright" or "indoor" or "sunset" && _o.P("UnityLighting", "front", 1) > 0.001;
                list.Add(("front", _o.SunPoint(c, R, 180, 10), Color.FromRgb(0xF2, 0xF2, 0xF2), "◇", frontOn));
                for (int i = 1; i <= Count; i++)
                {
                    if (!_o.LightOn(i) && _sel != $"l{i}") continue;
                    var col = Color.FromRgb(0xFF, 0xD9, 0xB3);
                    var e = _o.Entry(_o.Cur(), "Lights");
                    if (e != null && e["params"][$"l{i}_color"] is JNode cn && cn.Kind == JKind.Array && cn.Count >= 3)
                        col = Ui.ToColor(cn.ToFloatArray());
                    list.Add(($"l{i}", _o.LightPoint(c, R, i), col, _o.Follow(i) ? "◉" : i.ToString(), _o.LightOn(i)));
                }
                return list.ToArray();
            }

            public override void Render(DrawingContext ctx)
            {
                var c = Center; double R = this.R;
                if (R < 20) return;
                var text = C(Ui.KText, Colors.White); var text2 = C(Ui.KText2, Colors.Gray);
                var line = new Pen(new SolidColorBrush(text2, 0.35), 1);
                ctx.DrawEllipse(new SolidColorBrush(C(Ui.KBase, Color.FromRgb(0x20, 0x20, 0x24))), new Pen(new SolidColorBrush(text2, 0.6), 1), c, R, R);
                for (int m = 1; m < MaxDist; m++) ctx.DrawEllipse(null, line, c, R * m / MaxDist, R * m / MaxDist);
                ctx.DrawLine(line, new Point(c.X - R, c.Y), new Point(c.X + R, c.Y));
                ctx.DrawLine(line, new Point(c.X, c.Y - R), new Point(c.X, c.Y + R));
                Label(ctx, "정면", new Point(c.X, c.Y + R + 7), text2, 10);
                Label(ctx, "뒤", new Point(c.X, c.Y - R - 7), text2, 10);
                // 아바타: 원 + 정면(아래) 쪽 삼각형
                var av = new SolidColorBrush(text, 0.8);
                ctx.DrawEllipse(av, null, c, 7, 7);
                var tri = new StreamGeometry();
                using (var g = tri.Open()) { g.BeginFigure(new Point(c.X - 5, c.Y + 6), true); g.LineTo(new Point(c.X + 5, c.Y + 6)); g.LineTo(new Point(c.X, c.Y + 14)); g.EndFigure(true); }
                ctx.DrawGeometry(av, null, tri);
                var accent = Dial.Highlight(this);
                foreach (var (key, p, col, t, on) in Dots())
                {
                    double a = on ? 1 : 0.35;
                    ctx.DrawLine(new Pen(new SolidColorBrush(col, 0.35 * a), 1, new DashStyle(new double[] { 2, 3 }, 0)), p, c);
                    if (key == _sel) ctx.DrawEllipse(null, new Pen(new SolidColorBrush(accent), 2), p, 13, 13);
                    ctx.DrawEllipse(new SolidColorBrush(col, a), new Pen(new SolidColorBrush(Colors.Black, 0.5 * a), 1), p, 9, 9);
                    Label(ctx, t, p, Color.FromRgb(0x20, 0x20, 0x20), 10);
                }
            }

            // 직접 그리는 글이라 TextBlock처럼 저절로 바뀌지 않는다 → 글 표로 바꿔 그린다 (2026-11-03, "정면"·"뒤"가 영어 화면에서도 한국어였다)
            static void Label(DrawingContext ctx, string s, Point at, Color col, double size)
            {
                var ft = new FormattedText(Texts.T(s),CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, new SolidColorBrush(col));
                ctx.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                var p = e.GetPosition(this);
                string? hit = null; double best = 16 * 16;
                foreach (var (key, dp, _, _, _) in Dots())
                {
                    double d = (dp.X - p.X) * (dp.X - p.X) + (dp.Y - p.Y) * (dp.Y - p.Y);
                    if (d < best) { best = d; hit = key; }
                }
                if (hit == null) return;
                _sel = hit;
                _o.Refresh();
                if (hit == "front") return;   // 정면광은 자리 고정 (카메라를 따라가는 조명은 끌면 따라가기가 풀린다)
                _drag = hit;
                e.Pointer.Capture(this);
                e.Handled = true;
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                if (_drag == null) return;
                var list = _o.MoveTo(_drag, e.GetPosition(this), Center, R);
                if (list != null) { _o._live = list; _o._commit(list, "", true); _last = list; InvalidateVisual(); }
            }
            JNode? _last;

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                if (_drag == null) return;
                var key = _drag; _drag = null;
                e.Pointer.Capture(null);
                _o._live = null;
                if (_last != null) { var l = _last; _last = null; _o._commit(l, key == "sun" ? "주광 옮기기" : $"조명 {key[1]} 옮기기", false); }
            }
        }
    }
}
