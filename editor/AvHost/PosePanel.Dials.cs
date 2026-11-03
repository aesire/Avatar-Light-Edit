// 자세 "관절 다이얼" 탭 (2026-10-22, 사용자 요청): 큰 관절(척추·가슴·목·머리·어깨·위팔·아래팔·손목·허벅지·종아리·발목)의
// 머슬 값을 다이얼로 돌린다. 관절마다 작은 판 하나(이름 + 다이얼 2~3개, 다이얼 아래 움직임 이름과 값). 왼쪽·오른쪽은 나란히.
// 끄는 동안 미리보기, 놓으면 실행 취소 한 단계. 두 번 누르면 가운데 값(0)으로. 누르면 오른쪽 세밀 조정 막대에도 그 값이 간다.
// 탭이라 다른 탭처럼 떼어 띄울 수 있다 (떠 있는 창 → 핀으로 다른 페이지에서도).
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    public sealed partial class PosePanel
    {
        sealed class DialCell { public Dial Dial = null!; public TextBlock Label = null!, Value = null!; }
        readonly Dictionary<string, DialCell> _dials = new Dictionary<string, DialCell>();

        static readonly (string Title, string Bone, string[] Moves)[] DialJoints =
        {
            ("척추", "Spine", new[] { "Front-Back", "Left-Right", "Twist Left-Right" }),
            ("가슴", "Chest", new[] { "Front-Back", "Left-Right", "Twist Left-Right" }),
            ("목", "Neck", new[] { "Nod Down-Up", "Tilt Left-Right", "Turn Left-Right" }),
            ("머리", "Head", new[] { "Nod Down-Up", "Tilt Left-Right", "Turn Left-Right" }),
            ("어깨", "Shoulder", new[] { "Down-Up", "Front-Back" }),
            ("위팔", "Arm", new[] { "Down-Up", "Front-Back", "Twist In-Out" }),
            ("아래팔", "Forearm", new[] { "Stretch", "Twist In-Out" }),
            ("손목", "Hand", new[] { "Down-Up", "In-Out" }),
            ("허벅지", "Upper Leg", new[] { "Front-Back", "In-Out", "Twist In-Out" }),
            ("종아리", "Lower Leg", new[] { "Stretch", "Twist In-Out" }),
            ("발목", "Foot", new[] { "Up-Down", "Twist In-Out" }),
        };

        Control BuildDials()
        {
            var p = new StackPanel();
            p.Children.Add(Ui.Wrap("큰 관절을 다이얼로 돌려요. 위·오른쪽으로 끌면 +, 아래·왼쪽이면 − (바깥 테를 잡으면 돌리기 · Shift: 천천히 · 휠: 한 칸 · 두 번 누르면 가운데 0으로). 탭 오른쪽 단추로 떼어 띄워 둘 수 있어요."));
            void Section(string title, IEnumerable<(string Title, string Bone, string[] Moves)> joints, bool sided)
            {
                p.Children.Add(Ui.SectionTitle(title));
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var j in joints)
                {
                    if (!sided) { wrap.Children.Add(JointCard(j.Title, j.Moves.Select(m => j.Bone + " " + m).ToArray())); continue; }
                    foreach (var (side, ko) in new[] { ("Left", "왼"), ("Right", "오른") })
                        wrap.Children.Add(JointCard(ko + " " + j.Title, j.Moves.Select(m => side + " " + j.Bone + " " + m).ToArray()));
                }
                p.Children.Add(wrap);
            }
            Section("몸통 · 머리", DialJoints.Take(4), false);
            Section("팔", DialJoints.Skip(4).Take(4), true);
            Section("다리", DialJoints.Skip(8), true);
            return p;
        }

        Control JointCard(string title, string[] names)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var name in names.Where(Muscles.IsMuscle))
            {
                string full = Muscles.Label(name);
                string move = full.StartsWith(title) ? full.Substring(title.Length).Trim() : full;
                var cell = new DialCell
                {
                    Dial = new Dial { Width = 52, Height = 52, Minimum = -1, Maximum = 1, HorizontalAlignment = HorizontalAlignment.Center },
                    Label = new TextBlock { Text = move, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) },
                    Value = new TextBlock { FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center },
                };
                cell.Label.Fg(Ui.KText2); cell.Value.Fg(Ui.KText3);
                ToolTip.SetTip(cell.Dial, $"{full}\n{name}");
                cell.Dial.DragStarted += () => { var t = MuscleTarget(name); t.Source = cell.Dial; Ui.OfferFine(t); };
                cell.Dial.Changing += v => { Preview(name, v); cell.Value.Text = Ui.F(Math.Round(v, 3)); };
                cell.Dial.Committed += v => Commit(name, v);
                cell.Dial.ResetRequested += () => { if (_rest != null) Commit(name, 0); };   // 두 번 누르면 가운데(0)
                var col = new StackPanel { Width = 64, Children = { cell.Dial, cell.Label, cell.Value } };
                row.Children.Add(col);
                _dials[name] = cell;
            }
            var box = new StackPanel();
            box.Children.Add(Ui.Text(title, 13, FontWeight.SemiBold));
            row.Margin = new Thickness(0, 6, 0, 0);
            box.Children.Add(row);
            var card = Ui.Card(box, new Thickness(10, 8, 10, 8));
            card.Margin = new Thickness(0, 0, 8, 8);
            return card;
        }

        Ui.FineTarget MuscleTarget(string name) => new Ui.FineTarget
        {
            Name = Muscles.Label(name), Min = -1, Max = 1, Step = 0.01, Kind = "muscle", Key = name,
            Get = () => PoseValue.MusclesOf(CurrentPose()).TryGetValue(name, out var x) ? x : 0,
            Preview = v => Preview(name, v), Commit = v => Commit(name, v),
        };

        void RefreshDials()
        {
            if (_dials.Count == 0) return;
            bool can = Doc != null && _rest != null;
            var m = can ? PoseValue.MusclesOf(CurrentPose()) : new Dictionary<string, float>();
            foreach (var (name, c) in _dials)
            {
                double v = m.TryGetValue(name, out var x) ? x : 0;
                double rest = _rest != null && _rest.TryGetValue(name, out var r) ? r : 0;
                c.Dial.IsEnabled = can;
                c.Dial.Value = v;
                c.Value.Text = can ? Ui.F(Math.Round(v, 3)) : "";
                c.Label.Fg(can && Math.Abs(v - rest) > 1e-3 ? Ui.KAccentText : Ui.KText2);
            }
        }
    }
}
