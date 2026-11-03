// 자세 페이지 (PLAN 3.5): 내장 프리셋, 대칭(좌우 복사·뒤집기), 손(쥐기·손 모양), 바닥에 맞추기, 뷰포트에서 고른 본 되돌리기·반대쪽 복사, 휴머노이드 .anim 불러오기(시간 고르기), 자세 저장·불러오기, 머슬 슬라이더.
// 자세는 Document의 Addr.Pose 하나로 기록된다 (Undo·세션 포함). 머슬 값이라 다른 아바타에도 그대로 쓸 수 있다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class PosePanel : DockPanel
    {
        readonly EditorContext _ctx;
        // 페이지 안 탭 (2026-10-21): 위에 지금 자세·고른 본(늘 보임), 아래 탭 = 프리셋·대칭 · 손 모양 · 저장한 자세 · 애니메이션 · 머슬(전문가)
        readonly StackPanel _body = new StackPanel(), _tPresets = new StackPanel(), _tHands = new StackPanel();
        readonly SubTabs _tabs;
        readonly TextBlock _current = Ui.Text("", 14, FontWeight.SemiBold);
        readonly Button _resetBtn;
        readonly Border _notice = Ui.Notice("");
        readonly WrapPanel _presets = new WrapPanel(), _saved = new WrapPanel();
        readonly StackPanel _animRow = new StackPanel { IsVisible = false };
        readonly TextBlock _animName = Ui.Text("");
        readonly Ui.EditSlider _animTime;
        readonly TextBox _animBox;
        readonly TextBox _search = Ui.SearchBox("머슬 이름으로 찾기 (예: 팔, 손가락, Arm)");
        readonly StackPanel _muscles = new StackPanel();
        readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        readonly List<(Ui.Fold Fold, List<string> Names)> _groups = new List<(Ui.Fold, List<string>)>();
        readonly List<Control> _needPose = new List<Control>();
        Dictionary<string, float>? _rest;      // 렌더러가 알려 준 쉬는 자세 머슬 값 (null = 자세를 바꿀 수 없음)
        string _problem = "";
        AnimClip? _clip;
        string _bone = "";                     // 뷰포트에서 고른 본 (렌더러 poseSelected, "" = 없음)
        readonly Border _boneCard;
        readonly TextBlock _boneName = Ui.Text("", 14, FontWeight.SemiBold);
        readonly Button _boneReset, _boneCopy;
        string _hand = "Both";                 // 손 모양을 바꿀 쪽: Left · Right · Both
        readonly Ui.EditSlider _grip;
        readonly TextBox _gripBox;
        readonly CheckBox _floorFit = new CheckBox { IsChecked = true };
        readonly Ui.FoldSection _animFold = new Ui.FoldSection("애니메이션에서 가져오기", false), _savedFold = new Ui.FoldSection("저장한 자세", false), _muscleFold = new Ui.FoldSection("머슬 하나씩", false);
        bool _floorQuiet;
        /// <summary>바닥에 맞추기를 켜고 끔 (MainWindow가 설정에 기억하고 setView.floorFit으로 보낸다).</summary>
        public event Action<bool>? FloorFitChanged;
        public bool FloorFit { get => _floorFit.IsChecked == true; set { _floorQuiet = true; _floorFit.IsChecked = value; _floorQuiet = false; } }

        sealed class Row { public Grid Grid = null!; public TextBlock Name = null!; public Ui.EditSlider Slider = null!; public TextBox Box = null!; public Button Reset = null!; }

        public PosePanel(EditorContext ctx)
        {
            _ctx = ctx;

            _resetBtn = Ui.Button("되돌리기", () => Doc?.Reset(Addr.Pose, "자세 되돌리기"), "아바타를 불러온 자세(대개 T 포즈)로 돌려요", Glyph.Reset);
            _resetBtn.Margin = new Thickness(0);
            // 지금 자세 + 바닥에 맞추기를 한 카드로 (바닥은 따로 구역을 두지 않는다)
            ToolTip.SetTip(_floorFit, "자세를 바꾸면 아바타의 가장 낮은 곳(신발 바닥 등)이 불러온 자세와 같은 높이에 오도록 몸을 올리고 내려요. 앉거나 눕는 자세가 공중에 뜨지 않게. 미리보기에만 쓰이고 자세 값은 그대로예요");
            _floorFit.IsCheckedChanged += (_, __) => { if (!_floorQuiet) FloorFitChanged?.Invoke(FloorFit); };
            var nowCard = new StackPanel();
            // 자세 이름은 이름 아래 작은 글씨로 (값 칸에 되돌리기 단추와 같이 두면 "불러온 그대…"로 잘렸다)
            nowCard.Children.Add(Ui.DescRow("지금 자세", _current, _resetBtn));
            _current.FontSize = 13; _current.Fg(Ui.KAccentText);
            nowCard.Children.Add(Ui.PropRow(Ui.Text("바닥에 맞추기"), _floorFit, null));
            _body.Children.Add(Ui.Card(nowCard));
            _boneReset = Ui.Button("이 본 되돌리기", ResetSelectedBone, "고른 본만 불러온 자세로 돌려요 (뷰포트에서 R)", Glyph.Reset);
            _boneCopy = Ui.Button("반대쪽에 복사", CopySelectedBone, "고른 본의 자세를 반대쪽 본에 그대로 옮겨요 (뷰포트에서 C)");
            var boneBtns = new WrapPanel();
            boneBtns.Children.Add(_boneReset); boneBtns.Children.Add(_boneCopy);
            var boneBody = new StackPanel();
            boneBody.Children.Add(Ui.PropRow(Ui.Text("고른 본"), _boneName, null));
            boneBody.Children.Add(boneBtns);
            _boneCard = Ui.Card(boneBody);
            _boneCard.Margin = new Thickness(0, 8, 0, 0);
            _body.Children.Add(_boneCard);
            _notice.Margin = new Thickness(0, 8, 0, 0);
            _body.Children.Add(_notice);

            _tPresets.Children.Add(Ui.SectionTitle("프리셋"));
            _tPresets.Children.Add(_presets);
            foreach (var p in PosePresets.All)
            {
                var b = Ui.Button(p.Name, () => Apply(p.Value, "자세: " + p.Name), "머슬 값 프리셋이라 어느 아바타에나 같은 자세가 돼요");
                _presets.Children.Add(b); _needPose.Add(b);
            }

            _tPresets.Children.Add(Ui.SectionTitle("대칭"));
            var sym = new WrapPanel();
            foreach (var (label, mode, tip) in new[]
            {
                ("왼쪽 → 오른쪽", PoseValue.Symmetry.LeftToRight, "왼팔·왼다리·왼손가락 자세를 오른쪽에 그대로 복사해요 (몸통·머리는 그대로)"),
                ("오른쪽 → 왼쪽", PoseValue.Symmetry.RightToLeft, "오른팔·오른다리·오른손가락 자세를 왼쪽에 그대로 복사해요 (몸통·머리는 그대로)"),
                ("좌우 뒤집기", PoseValue.Symmetry.Flip, "거울에 비춘 자세로 바꿔요 (양쪽을 맞바꾸고 몸통·머리 기울기도 반대로)"),
            })
            {
                var b = Ui.Button(label, () => Mirror(mode, label), tip);
                sym.Children.Add(b); _needPose.Add(b);
            }
            _tPresets.Children.Add(sym);

            _tHands.Children.Add(Ui.SectionTitle("손 모양", "쥐기는 손가락을 함께 굽혀요. 아래 단추는 손 모양 프리셋이에요."));
            var side = Ui.CellCombo(new ComboBox { ItemsSource = new[] { "양손", "왼손", "오른손" }, SelectedIndex = 0 });
            side.SelectionChanged += (_, __) => { _hand = side.SelectedIndex == 1 ? "Left" : side.SelectedIndex == 2 ? "Right" : "Both"; RefreshGrip(); };
            _needPose.Add(side);
            _grip = new Ui.EditSlider(0, 1, g => { if (Doc != null && _rest != null) Doc.Preview(Addr.Pose, HandPose.WithGrip(CurrentPose(), _hand, (float)g, _rest)); }, g => SetGrip((float)g));
            _gripBox = Ui.NumberCell(v => SetGrip((float)v), RefreshGrip);
            var hands = new StackPanel();
            hands.Children.Add(Ui.PropRow(Ui.Text("어느 손"), side, null));
            hands.Children.Add(Ui.PropRow(Ui.Text("쥐기"), Ui.NumberWithSlider(_gripBox, _grip.Slider), null));
            _tHands.Children.Add(Ui.Card(hands));
            var shapes = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (key, name, tip) in HandPose.Shapes)
            {
                var b = Ui.Button(name, () => Apply2(HandPose.WithShape(CurrentPose(), _hand, key, _rest!), $"{HandName} {name}"), tip);
                shapes.Children.Add(b); _needPose.Add(b);
            }
            _tHands.Children.Add(shapes);

            var loadAnim = Ui.Button(".anim 불러오기…", LoadAnimDialog, "휴머노이드 애니메이션(.anim)의 한 순간을 자세로 가져와요. 아바타 패키지의 포즈·AFK 애니메이션 등", Glyph.Open);
            _needPose.Add(loadAnim);
            _animFold.Body.Children.Add(loadAnim);
            _animTime = new Ui.EditSlider(0, 1, t => { if (_clip != null) Doc?.Preview(Addr.Pose, _clip.SamplePose((float)t)); }, t => SetAnimTime((float)t));
            _animBox = Ui.NumberCell(v => SetAnimTime((float)v), RefreshAnim);
            _animRow.Children.Add(_animName);
            _animRow.Children.Add(Ui.Card(Ui.PropRow(Ui.Text("시간 (초)"), Ui.NumberWithSlider(_animBox, _animTime.Slider), null)));
            _animRow.Children.Add(BuildPlay());   // 재생 · 녹화 폴더 (PosePanel.Motion.cs)
            _animRow.Margin = new Thickness(0, 8, 0, 0);
            _animFold.Body.Children.Add(_animRow);

            var tools = new WrapPanel();
            var save = Ui.Button("지금 자세 저장…", SaveDialog, "이 PC에 저장해 두고 다른 아바타에서도 불러올 수 있어요", Glyph.Save);
            _needPose.Add(save);
            tools.Children.Add(save);
            tools.Children.Add(Ui.Button("폴더 열기", OpenFolder, PoseLibrary.Folder, Glyph.Folder));
            _savedFold.Body.Children.Add(tools);
            _saved.Margin = new Thickness(0, 4, 0, 0);
            _savedFold.Body.Children.Add(_saved);

            _muscleFold.Hint = $"{Muscles.Names.Count}개";
            _muscleFold.HelpText = "뷰포트 조작·프리셋으로 모자랄 때 써요. Unity 휴머노이드 머슬 값(-1 ~ 1)이에요. 슬라이더를 끄는 동안 미리보기에 바로 보이고, 놓으면 실행 취소 한 단계로 기록돼요.";
            _search.Margin = new Thickness(0, 8, 0, 8);
            InputBar.SetChoices(_search, "머리", "목", "몸통", "어깨", "팔", "손", "손가락", "엄지", "다리", "발", "눈", "턱");   // 입력 바(길게 누르기): 키보드 없이 찾기
            _muscleFold.Body.Children.Add(_search);
            _muscleFold.Body.Children.Add(_muscles);
            BuildMuscles();

            _body.Margin = new Thickness(0, 0, 0, 6);
            _tabs = new SubTabs("pose", "자세", _body);
            _tabs.Add("presets", "프리셋·대칭", _tPresets);
            _tabs.Add("hands", "손 모양", _tHands);
            _tabs.Add("saved", "저장한 자세", _savedFold.Flatten());
            _tabs.Add("anim", "애니메이션", _animFold.Flatten());
            _tabs.Add("dials", "관절 다이얼", BuildDials());   // 큰 관절 머슬을 다이얼로 (PosePanel.Dials.cs)
            _tabs.Add("muscles", "머슬 하나씩", _muscleFold.Flatten(), expert: true);   // 전문가 모드
            Children.Add(_tabs);

            // 떠 있는 다이얼 프리셋: 머슬 이름 → 값
            Ui.FineResolvers["muscle"] = key => Muscles.IsMuscle(key) ? MuscleTarget(key) : null;
            var searchLater = Ui.Debounce(FilterMuscles);
            _search.TextChanged += (_, __) => searchLater();
            _ctx.DocumentOpened += () =>
            {
                StopPlay();
                _rest = null; _problem = ""; _clip = null;
                if (_ctx.Doc != null) _ctx.Doc.Changed += a => { if (a.Contains(Addr.Pose)) Refresh(); };
                Refresh();
            };
            _ctx.RendererMessage += m =>
            {
                if (Msg.Type(m) == Msg.PoseEdited) { FromViewport(m); return; }
                if (Msg.Type(m) == Msg.PoseSelected) { _bone = m["bone"].AsString; Refresh(); return; }
                if (Msg.Type(m) != Msg.SceneLoaded) return;
                _bone = "";
                var pose = m["pose"];
                _rest = pose["ok"].AsBool ? pose["rest"].Props.ToDictionary(kv => kv.Key, kv => kv.Value.AsFloat) : null;
                _problem = pose["ok"].AsBool ? "" : pose["problem"].AsString;
                Refresh();
            };
            RefreshSaved();
            Refresh();
        }

        Document? Doc => _ctx.Doc;
        Window? Owner => TopLevel.GetTopLevel(this) as Window;

        // ---------------- 컨트롤 보드(OverlayBoard)에서 쓰는 것: 오버레이 중에도 자세 바꾸기 ----------------
        /// <summary>자세가 바뀌었거나, 쓸 수 있게 되었거나, 저장한 자세 목록이 바뀌었을 때.</summary>
        public event Action? QuickChanged;
        /// <summary>자세를 바꿀 수 있는지 (휴머노이드 아바타가 렌더러에 올라감).</summary>
        public bool CanPose => Doc != null && _rest != null;

        /// <summary>보드의 큰 단추: 불러온 그대로 · 프리셋 · 저장한 자세. Active = 지금 그 자세.</summary>
        public List<(string Name, bool Active, Action Apply)> QuickPoses()
        {
            var list = new List<(string, bool, Action)>();
            if (!CanPose) return list;
            var cur = Doc!.Get(Addr.Pose);
            var name = cur.IsNull ? "" : PoseValue.Name(cur);
            list.Add(("불러온 그대로", cur.IsNull, () => { Doc?.Reset(Addr.Pose, "자세 되돌리기"); _ctx.Status("불러온 자세로 돌렸어요."); }));
            foreach (var p in PosePresets.All) { var pp = p; list.Add((p.Name, name == p.Name, () => Apply(pp.Value, "자세: " + pp.Name))); }
            foreach (var (n, path) in PoseLibrary.List()) { var nn = n; var pa = path; list.Add((n, name == n, () => LoadSaved(nn, pa))); }
            return list;
        }

        /// <summary>지금 자세 (기록된 것이 없으면 쉬는 자세의 머슬 값).</summary>
        JNode CurrentPose()
        {
            var p = Doc?.Get(Addr.Pose) ?? JNode.Null;
            return p.IsNull && _rest != null ? PoseValue.Make(_rest) : p;
        }

        void Apply(JNode pose, string label)
        {
            if (Doc == null || _rest == null) return;
            Doc.Set(Addr.Pose, pose, label);
            _ctx.Status(label.Replace("자세: ", "") + " 자세로 변경했습니다.");
        }

        /// <summary>새 자세를 기록한다. 쉬는 자세와 같아지면 "불러온 그대로"로 둔다 (덮어쓰기 없음).</summary>
        void SetOrReset(JNode next, string label)
        {
            if (Doc == null || _rest == null) return;
            if (PoseValue.IsRest(next, _rest)) Doc.Reset(Addr.Pose, label);
            else Doc.Set(Addr.Pose, next, label);
        }

        void Mirror(PoseValue.Symmetry mode, string label)
        {
            if (Doc == null || _rest == null) return;
            SetOrReset(PoseValue.Mirrored(CurrentPose(), mode), "자세: " + label);
            _ctx.Status($"자세를 {label}로 맞췄어요.");
        }

        string HandName => _hand == "Left" ? "왼손" : _hand == "Right" ? "오른손" : "양손";

        void Apply2(JNode next, string label)
        {
            if (Doc == null || _rest == null) return;
            SetOrReset(next, "자세: " + label);
            _ctx.Status(label + "으로 변경했습니다.");
        }

        void SetGrip(float g)
        {
            if (Doc == null || _rest == null) return;
            g = (float)Math.Round(Math.Clamp(g, 0, 1), 3);
            SetOrReset(HandPose.WithGrip(CurrentPose(), _hand, g, _rest), $"자세: {HandName} 쥐기");
            RefreshGrip();
        }

        void RefreshGrip()
        {
            if (_rest == null) return;
            double g = HandPose.Grip(CurrentPose(), _hand, _rest);
            _grip.SetQuiet(g);
            if (!_gripBox.IsFocused) _gripBox.Text = Ui.F(Math.Round(g, 2));
        }

        /// <summary>고른 본만 불러온 자세로 (뷰포트 R).</summary>
        public void ResetSelectedBone()
        {
            if (Doc == null || _rest == null) return;
            if (_bone.Length == 0) { _ctx.Status("먼저 뷰포트에서 본을 더블클릭해 고르세요."); return; }
            var label = HumanBones.Label(_bone);
            if (!PoseValue.BoneModified(CurrentPose(), _bone, _rest)) { _ctx.Status($"{label}은 이미 불러온 자세예요."); return; }
            SetOrReset(PoseValue.ResetBone(CurrentPose(), _bone, _rest), "자세: " + label + " 되돌리기");
            _ctx.Status($"{label}을 불러온 자세로 돌렸어요.");
        }

        /// <summary>고른 본의 자세를 반대쪽에 (뷰포트 C).</summary>
        public void CopySelectedBone()
        {
            if (Doc == null || _rest == null) return;
            if (_bone.Length == 0) { _ctx.Status("먼저 뷰포트에서 본을 더블클릭해 고르세요."); return; }
            var other = Muscles.OppositeBone(_bone);
            if (other == _bone) { _ctx.Status($"{HumanBones.Label(_bone)}은 가운데 본이라 반대쪽이 없어요."); return; }
            var label = $"{HumanBones.Label(_bone)} → {HumanBones.Label(other)}";
            SetOrReset(PoseValue.CopyBoneToOpposite(CurrentPose(), _bone), "자세: " + label);
            _ctx.Status($"{label} 복사했어요.");
        }

        /// <summary>뷰포트에서 본을 돌리고 놓았을 때: 렌더러가 머슬 값으로 바꿔 보낸 자세를 실행 취소 한 단계로 기록한다.</summary>
        void FromViewport(JNode m)
        {
            if (Doc == null || _rest == null) return;
            var label = "자세: " + HumanBones.Label(m["bone"].AsString) + " 돌리기";
            var next = PoseValue.FromViewport(m["pose"], _rest);
            if (next == null) Doc.Reset(Addr.Pose, label);
            else Doc.Set(Addr.Pose, next, label);
        }

        // ---------------- .anim ----------------
        async void LoadAnimDialog()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null || Doc == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("휴머노이드 애니메이션 (.anim)"),
                FileTypeFilter = new[] { new FilePickerFileType("Unity 애니메이션") { Patterns = new[] { "*.anim" } } },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not string path) return;
            LoadAnim(path);
        }

        /// <summary>.anim의 한 순간을 자세로 (고르기 · 창에 끌어다 놓기 2026-10-27).</summary>
        internal void LoadAnim(string path)
        {
            if (Doc == null) return;
            AnimClip clip;
            try { clip = AnimClip.Load(path); }
            catch (Exception e) { _ctx.Status("애니메이션을 읽지 못했습니다: " + e.Message); return; }
            if (!clip.IsHumanoid) { _ctx.Status($"{clip.Name}: 휴머노이드 머슬 커브가 없는 애니메이션이에요 (표정·오브젝트 애니메이션은 자세가 아니에요)."); return; }
            UseClip(clip);
        }

        void UseClip(AnimClip clip)
        {
            StopPlay();
            _clip = clip;
            Apply(clip.SamplePose(0), "자세: " + clip.Name);
            RefreshAnim();
        }

        void SetAnimTime(float t)
        {
            if (_clip == null) return;
            StopPlay();
            t = Math.Clamp(t, 0, _clip.Length);
            Apply(_clip.SamplePose(t), "자세: " + _clip.Name);
            RefreshAnim();
        }

        void RefreshAnim()
        {
            _animRow.IsVisible = _clip != null && _clip.Length > 0;
            _animFold.Hint = _clip == null ? ".anim의 한 순간을 자세로" : _clip.Name;
            if (_clip == null) return;
            _animName.Text = $"{_clip.Name} · {_clip.Length:0.##}초";
            _animTime.Slider.Maximum = Math.Max(0.01, _clip.Length);
            // 지금 자세 이름의 "(n초)"에서 시간을 읽는다 (다른 자세로 바꿨으면 0)
            double t = 0;
            var name = PoseValue.Name(Doc?.Get(Addr.Pose) ?? JNode.Null);
            if (name.StartsWith(_clip.Name + " (") && name.EndsWith("초)")) Ui.TryParse(name.Substring(_clip.Name.Length + 2, name.Length - _clip.Name.Length - 4), out t);
            _animTime.SetQuiet(t);
            if (!_animBox.IsFocused) _animBox.Text = Ui.F(Math.Round(t, 2));
        }

        // ---------------- 저장한 자세 ----------------
        async void SaveDialog()
        {
            var owner = Owner;
            if (owner == null || Doc == null || _rest == null) return;
            var cur = CurrentPose();
            var name = await Dialog.Prompt(owner, "자세 저장", $"이름을 정하세요. {PoseLibrary.Folder}에 저장돼요.", PoseValue.Name(cur).Length > 0 ? PoseValue.Name(cur) : "내 자세");
            if (name == null) return;
            var file = Path.Combine(PoseLibrary.Folder, name + PoseLibrary.Extension);
            if (File.Exists(file) && await Dialog.Ask(owner, "자세 저장", $"'{name}'이 이미 있어요. 덮어쓸까요?", "덮어쓰기", "취소") != 0) return;
            try { PoseLibrary.Save(name, cur); }
            catch (Exception e) { _ctx.Status("저장하지 못했습니다: " + e.Message); return; }
            Doc.Set(Addr.Pose, PoseValue.Named(cur, name), "자세 저장: " + name);
            RefreshSaved();
            _ctx.Status($"'{name}' 자세를 저장했어요.");
        }

        void RefreshSaved()
        {
            _saved.Children.Clear();
            var list = PoseLibrary.List();
            _savedFold.Hint = list.Count == 0 ? "없음" : $"{list.Count}개";
            _tabs?.SetBadge("saved", list.Count == 0 ? "" : $"{list.Count}");
            if (list.Count == 0) { _saved.Children.Add(Ui.Wrap("아직 저장한 자세가 없어요.")); return; }
            foreach (var (name, path) in list)
            {
                var b = Ui.Button(name, () => LoadSaved(name, path), "누르면 이 자세로 · 오른쪽 클릭으로 지우기");
                var del = new MenuItem { Header = "지우기" };
                del.Click += async (_, __) =>
                {
                    var owner = Owner;
                    if (owner == null || await Dialog.Ask(owner, "자세 지우기", $"저장한 자세 '{name}'을 지울까요? 파일이 지워져요.", "지우기", "취소") != 0) return;
                    try { File.Delete(path); } catch (Exception e) { _ctx.Status("삭제하지 못했습니다: " + e.Message); }
                    RefreshSaved();
                };
                b.ContextMenu = new ContextMenu { Items = { del } };
                _saved.Children.Add(b); _needPose.Add(b);
            }
            Refresh();
        }

        void LoadSaved(string name, string path)
        {
            try { Apply(PoseValue.Named(PoseLibrary.Load(path), name), "자세: " + name); }
            catch (Exception e) { _ctx.Status("자세 파일을 읽지 못했습니다: " + e.Message); }
        }

        void OpenFolder()
        {
            Directory.CreateDirectory(PoseLibrary.Folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", PoseLibrary.Folder) { UseShellExecute = true });
        }

        // ---------------- 머슬 ----------------
        void BuildMuscles()
        {
            foreach (var group in Muscles.GroupOrder)
            {
                var body = new StackPanel();
                var names = Muscles.Names.Where(n => Muscles.Group(n) == group).ToList();
                foreach (var name in names) body.Children.Add(MakeRow(name));
                var fold = new Ui.Fold(Ui.Text(group, 15, FontWeight.SemiBold), body, false);
                _groups.Add((fold, names));
                _muscles.Children.Add(fold);
            }
        }

        Control MakeRow(string name)
        {
            var row = new Row();
            string label = Muscles.Label(name);
            row.Name = Ui.Text(label); ToolTip.SetTip(row.Name, name);
            row.Slider = new Ui.EditSlider(-1, 1, v => Preview(name, v), v => Commit(name, v)) { Kind = "muscle", Key = name };
            row.Box = Ui.NumberCell(v => Commit(name, v), () => RefreshRow(name));
            row.Reset = Ui.IconButton(Glyph.Reset, () => { if (_rest != null) Commit(name, _rest.TryGetValue(name, out var r) ? r : 0); }, "불러온 자세의 값으로");
            row.Grid = Ui.PropRow(row.Name, Ui.NumberWithSlider(row.Box, row.Slider.Slider), row.Reset, 0.9);
            _rows[name] = row;
            return row.Grid;
        }

        void Preview(string name, double v) { if (Doc != null && _rest != null) Doc.Preview(Addr.Pose, PoseValue.With(CurrentPose(), name, (float)v)); }

        void Commit(string name, double v)
        {
            if (Doc == null || _rest == null) return;
            var next = PoseValue.With(CurrentPose(), name, (float)Math.Round(v, 3));
            // 전부 쉬는 자세 값이면 "불러온 그대로"로 둔다 (덮어쓰기 없음)
            if (PoseValue.IsRest(next, _rest)) Doc.Reset(Addr.Pose, Muscles.Label(name));
            else Doc.Set(Addr.Pose, next, "자세: " + Muscles.Label(name));
        }

        /// <summary>시험용: 머슬 줄에서 값을 고친 것과 같게 (--test-session-edit).</summary>
        internal void TestCommit(string name, double v) => Commit(name, v);

        void FilterMuscles()
        {
            var q = (_search.Text ?? "").Trim();
            foreach (var kv in _rows)
                kv.Value.Grid.IsVisible = q.Length == 0 || kv.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || Muscles.Label(kv.Key).Contains(q);
            // 찾는 중에는 맞는 줄이 있는 묶음을 펼친다
            if (q.Length > 0) foreach (var (fold, names) in _groups) fold.IsExpanded = names.Any(n => _rows[n].Grid.IsVisible);
        }

        void RefreshRow(string name)
        {
            if (!_rows.TryGetValue(name, out var row)) return;
            var m = PoseValue.MusclesOf(CurrentPose());
            double v = m.TryGetValue(name, out var x) ? x : 0;
            double rest = _rest != null && _rest.TryGetValue(name, out var r) ? r : 0;
            bool mod = _rest != null && Math.Abs(v - rest) > 1e-3;
            row.Slider.SetQuiet(v);
            if (!row.Box.IsFocused) row.Box.Text = Ui.F(Math.Round(v, 3));
            Ui.MarkModified(row.Name, mod);
            row.Reset.IsVisible = mod;
            ToolTip.SetTip(row.Name, mod ? $"{name}\n불러온 자세: {Ui.F(Math.Round(rest, 3))}" : name);
        }

        void Refresh()
        {
            QuickChanged?.Invoke();
            bool can = Doc != null && _rest != null;
            foreach (var c in _needPose) c.IsEnabled = can;
            foreach (var r in _rows.Values) { r.Slider.Slider.IsEnabled = can; r.Box.IsEnabled = can; }
            _notice.IsVisible = !can;
            if (_notice.Child is Panel np && np.Children.OfType<TextBlock>().LastOrDefault() is TextBlock nt)
                nt.Text = Doc == null ? "아바타를 열면 자세를 바꿀 수 있어요."
                    : _problem.Length > 0 ? $"이 아바타는 자세를 바꿀 수 없어요: {_problem}. 휴머노이드(Humanoid)로 설정된 FBX의 아바타만 돼요."
                    : "미리보기가 아바타를 불러오면 자세를 바꿀 수 있어요.";
            var pose = Doc?.Get(Addr.Pose) ?? JNode.Null;
            _current.Text = pose.IsNull ? "불러온 그대로" : PoseValue.Name(pose).Length > 0 ? PoseValue.Name(pose) : "직접 바꾼 자세";
            _resetBtn.IsEnabled = !pose.IsNull;
            _boneCard.IsVisible = can && _bone.Length > 0;
            if (_boneCard.IsVisible)
            {
                var other = Muscles.OppositeBone(_bone);
                _boneName.Text = HumanBones.Label(_bone);
                _boneReset.IsEnabled = PoseValue.BoneModified(CurrentPose(), _bone, _rest!);
                _boneCopy.IsVisible = other != _bone;
                _boneCopy.Content = HumanBones.Label(other) + "에 복사";
            }
            foreach (var name in _rows.Keys) RefreshRow(name);
            _grip.Slider.IsEnabled = can; _gripBox.IsEnabled = can;
            RefreshGrip();
            RefreshAnim();
            RefreshDials();
        }
    }
}
