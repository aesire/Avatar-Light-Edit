// "의상·헤어·악세서리 추가" 창: 고른 .unitypackage 안의 프리팹을 하나씩 아바타에 넣어 보고(IR은 쓰지 않음)
// 종류(의상·헤어·악세서리)와 붙는 곳, 이 아바타용인지를 보여 준다. 고른 것을 붙일 방법(Spec)으로 돌려준다.
// 어디에 붙일지 알아내지 못한 아이템은 PlacementDialog로 종류와 붙일 뼈를 묻는다.
// 패키지는 데이터로만 읽는다 (안의 스크립트는 실행하지 않는다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImportSim;

namespace VrcProto.AvHost
{
    public sealed class ItemResult
    {
        public List<string> NewSources = new List<string>();
        public OutfitMerge.Spec Spec = new OutfitMerge.Spec();
        public string Name = "";
    }

    public sealed class ItemWindow : Window
    {
        readonly Recipe _recipe;
        readonly string[] _files;
        readonly TextBlock _state = Ui.Text("", 13, FontWeight.Normal, Ui.KText2);
        readonly ProgressBar _progress = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4, Margin = new Thickness(0, 4, 0, 4) };
        readonly ListBox _list = new ListBox { Height = 340, Background = Brushes.Transparent };
        readonly TextBlock _detail = Ui.Wrap("", Ui.KText2, 13);
        readonly Button _add, _place, _cancel;
        readonly List<Row> _rows = new List<Row>();
        readonly CheckBox _showOthers = new CheckBox { IsVisible = false };
        List<string> _bones = new List<string>();
        bool _busy;
        TaskCompletionSource<bool>? _wake;   // 넣어 볼 줄이 없을 때 "다른 아바타용도 보기"를 기다린다

        sealed class Row
        {
            public UnityImport.PrefabInfo Info = null!;
            public bool ForAvatar;             // 경로에 아바타 이름이 들어 있다 (여러 아바타용 패키지에서 이 아바타 것)
            public bool Tried;
            public OutfitMerge.Report? Report;
            public OutfitMerge.Spec? Manual;   // 직접 고른 붙일 곳
            public TextBlock Kind = null!, Desc = null!;
            public Control View = null!;
        }

        public ItemWindow(Recipe recipe, string[] files)
        {
            _recipe = recipe; _files = files;
            Title = "의상·헤어·악세서리 추가";
            Width = 640; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 6 };
            p.Children.Add(Ui.DialogHeader("아바타에 붙일 것 고르기", string.Join(", ", files.Select(Path.GetFileName)) + " 안의 프리팹입니다.",
                "하나씩 아바타에 넣어 보고 의상·헤어·악세서리인지, 어디에 붙는지 알아봐요. Modular Avatar 설정이 있으면 그대로 따라요. 패키지는 읽기만 해요."));
            p.Children.Add(_state);
            p.Children.Add(_progress);
            _list.IsVisible = false;
            _list.SelectionChanged += (_, __) => Refresh();
            _list.DoubleTapped += (_, __) => Add();
            p.Children.Add(_list);
            _showOthers.IsCheckedChanged += (_, __) => { Fill(); _wake?.TrySetResult(true); };
            p.Children.Add(_showOthers);
            p.Children.Add(_detail);

            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            _place = Ui.Button("붙일 곳 직접 고르기…", () => _ = Place(), "종류(의상·헤어·악세서리)와 붙일 뼈를 직접 골라요");
            _place.Margin = new Thickness(0);
            DockPanel.SetDock(_place, Dock.Left);
            row.Children.Add(_place);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            _add = Ui.Button("붙이기", Add, accent: true);
            _cancel = Ui.Button("취소", () => Close(null));
            foreach (var b in new[] { _add, _cancel }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            _add.IsDefault = true; _cancel.IsCancel = true;
            right.Children.Add(_add); right.Children.Add(_cancel);
            row.Children.Add(right);
            p.Children.Add(row);
            Content = p;

            Opened += async (_, __) => await Load();
            Closed += (_, __) => _wake?.TrySetResult(false);
            Refresh();
        }

        async Task Load()
        {
            SetBusy(true, "패키지 읽는 중…");
            UnityImport imp; SimFile avatar; List<UnityImport.PrefabInfo> prefabs;
            try
            {
                var sources = _recipe.Sources.Concat(_files).ToList();
                (imp, avatar, prefabs, _bones) = await Task.Run(() =>
                {
                    var i = Items.Open(sources);
                    var f = i.ResolveGuid(_recipe.Guid);
                    var set = new HashSet<string>(_files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
                    var list = i.PackagePrefabs().Where(x => set.Contains(x.Source) && !x.IsAvatar && x.Renderers > 0).ToList();
                    return (i, f, list, i.HumanBones(f, _recipe.Root));
                });
            }
            catch (Exception e) { Fail("읽지 못했습니다: " + e.Message); return; }
            if (!IsVisible) return;
            if (prefabs.Count == 0) { Fail("붙일 수 있는 프리팹이 없어요 (메시가 있는 프리팹, 아바타 제외)."); return; }

            // 여러 아바타용 패키지(헤어 등)는 경로에 아바타 이름이 든 것을 앞에 두고 나머지는 숨긴다. MA 프리팹을 먼저
            var key = Norm(Path.GetFileNameWithoutExtension(avatar.Path));
            foreach (var info in prefabs)
            {
                var r = new Row { Info = info, ForAvatar = key.Length >= 3 && Norm(info.Path).Contains(key) };
                r.View = RowView(r);
                _rows.Add(r);
            }
            var ordered = _rows.OrderBy(r => r.ForAvatar ? 0 : 1).ThenBy(r => r.Info.HasMergeArmature ? 0 : 1).ToList();
            _rows.Clear(); _rows.AddRange(ordered);
            int mine = _rows.Count(r => r.ForAvatar), others = _rows.Count - mine;
            if (mine > 0 && others > 0)
            {
                _showOthers.Content = $"다른 아바타용도 보기 ({others}개)";
                _showOthers.IsVisible = true;
                ToolTip.SetTip(_showOthers, $"경로에 \"{Path.GetFileNameWithoutExtension(avatar.Path)}\"가 든 프리팹 {mine}개만 보여 주고 있어요");
            }
            else _showOthers.IsChecked = true;
            Fill();
            _list.IsVisible = true;

            // 보이는 줄을 하나씩 넣어 보기 (색인·해석은 한 스레드에서만)
            while (IsVisible)
            {
                var row = _rows.FirstOrDefault(r => !r.Tried && Shown(r));
                if (row == null)
                {
                    SetBusy(false, $"프리팹 {Visible().Count()}개 · 고르고 \"붙이기\"를 누르세요");
                    if (_rows.All(r => r.Tried)) return;
                    _wake = new TaskCompletionSource<bool>();
                    await _wake.Task;
                    continue;
                }
                row.Tried = true;
                SetBusy(true, $"넣어 보는 중… {Visible().Count(r => r.Tried)}/{Visible().Count()}");
                try { row.Report = await Task.Run(() => imp.TryOutfit(avatar, _recipe.Root, new OutfitMerge.Spec { Prefab = row.Info.Guid })); }
                catch (Exception e) { row.Desc.Text = "넣어 보지 못했습니다: " + e.Message; row.Desc.Fg(Ui.KCritical); continue; }
                UpdateRow(row);
                if (row == Selected) Refresh();
            }
        }

        bool Shown(Row r) => r.ForAvatar || _showOthers.IsChecked == true;
        IEnumerable<Row> Visible() => _rows.Where(Shown);

        /// <summary>목록을 보이는 줄로 다시 채운다 (고른 줄은 그대로)</summary>
        void Fill()
        {
            var sel = Selected;
            _list.Items.Clear();
            foreach (var r in Visible()) _list.Items.Add(r.View);
            var vis = Visible().ToList();
            _list.SelectedIndex = sel != null && vis.Contains(sel) ? vis.IndexOf(sel) : vis.Count > 0 ? 0 : -1;
            _list.ScrollIntoView(_list.SelectedIndex);
        }

        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        static Control RowView(Row r)
        {
            var d = new DockPanel { Margin = new Thickness(4, 6) };
            r.Kind = Ui.Text("…", 11, FontWeight.SemiBold, Ui.KText2);
            var chip = new Border { Child = r.Kind, Padding = new Thickness(6, 1), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 }.Stroke(Ui.KCardStroke);
            r.Kind.HorizontalAlignment = HorizontalAlignment.Center;
            DockPanel.SetDock(chip, Dock.Right);
            d.Children.Add(chip);
            var text = new StackPanel();
            text.Children.Add(Ui.Text(r.Info.Name));
            r.Desc = Ui.Text("확인 중…", 12, FontWeight.Normal, Ui.KText2);
            text.Children.Add(r.Desc);
            d.Children.Add(text);
            ToolTip.SetTip(d, r.Info.Path);
            return d;
        }

        void UpdateRow(Row r)
        {
            var rep = r.Report;
            if (r.Manual != null)
            {
                r.Kind.Text = Items.KindLabel(r.Manual.Kind);
                r.Desc.Text = $"직접 선택됨 · {Items.BoneLabel(r.Manual.Target)}에 장착함" + (r.Manual.KeepWorld ? "" : " (본 위치로)");
                r.Desc.Fg(Ui.KAccentText);
                return;
            }
            if (rep == null) return;
            r.Kind.Text = Items.KindLabel(rep.Kind);
            var fit = Items.FitText(rep.Matched, rep.Bones, rep.MeanOffset, out var bad);
            if (rep.NeedsPlacement)
            {
                r.Desc.Text = "어디에 장착할지 모릅니다 · 장착할 때 선택합니다" + (rep.Reason.Length > 0 ? $" ({rep.Reason})" : "");
                r.Desc.Fg(Ui.KCaution);
            }
            else
            {
                r.Desc.Text = $"{Items.MethodLabel(rep.Method)} · {rep.Reason}" + (fit.Length > 0 ? " · " + fit : "");
                r.Desc.Fg(bad ? Ui.KCaution : Ui.KText2);
            }
        }

        Row? Selected => _list.SelectedItem is Control v ? _rows.FirstOrDefault(r => r.View == v) : null;

        async Task<bool> Place()
        {
            var r = Selected;
            if (r == null || _busy && r.Report == null) return false;
            var spec = await PlacementDialog.Ask(this, r.Info.Name, r.Report, _bones, r.Manual);
            if (spec == null) return false;
            spec.Prefab = r.Info.Guid;
            r.Manual = spec;
            UpdateRow(r);
            Refresh();
            return true;
        }

        async void Add()
        {
            var r = Selected;
            if (r == null || r.Report == null && r.Manual == null) return;
            // 자동으로 붙일 곳을 못 찾았으면 묻는다
            if (r.Manual == null && r.Report!.NeedsPlacement && !await Place()) return;
            Close(new ItemResult
            {
                NewSources = _files.Select(Path.GetFullPath).ToList(),
                Spec = r.Manual ?? new OutfitMerge.Spec { Prefab = r.Info.Guid },
                Name = r.Info.Name,
            });
        }

        void Refresh()
        {
            var r = Selected;
            _add.IsEnabled = r != null && (r.Report != null || r.Manual != null);
            _place.IsEnabled = r != null && r.Report != null;
            if (r?.Report is OutfitMerge.Report rep)
            {
                var lines = new List<string> { $"{r.Info.Name}: {Items.KindLabel(r.Manual?.Kind ?? rep.Kind)}" };
                if (r.Manual == null && !rep.NeedsPlacement)
                    lines.Add($"붙는 곳: {(rep.Target.Length > 0 ? Items.BoneLabel(rep.Target) : "—")} · 본 {rep.Bones}개 중 {rep.Matched}개를 아바타 본에 맞춤 (완전히 합침 {rep.Merged})");
                if (r.Info.HasMergeArmature) lines.Add("Modular Avatar Merge Armature 설정이 있어요 (그대로 따라요).");
                _detail.Text = string.Join("\n", lines);
            }
            else _detail.Text = "";
        }

        void SetBusy(bool busy, string text)
        {
            _busy = busy;
            _state.Text = text;
            _progress.IsVisible = busy;
            Refresh();
        }

        void Fail(string text)
        {
            _busy = false;
            _progress.IsVisible = false;
            _state.Text = text;
            _state.Fg(Ui.KCritical);
            _state.TextWrapping = TextWrapping.Wrap;
            Refresh();
        }
    }

    /// <summary>종류(의상·헤어·악세서리)와 붙일 뼈를 묻는 창. 자동으로 알아내지 못했거나 사용자가 바꾸고 싶을 때.</summary>
    public static class PlacementDialog
    {
        public static Task<OutfitMerge.Spec?> Ask(Window owner, string itemName, OutfitMerge.Report? analysis, List<string> humanBones, OutfitMerge.Spec? current)
        {
            var w = new Window
            {
                Title = Texts.T("어디에 붙일까요?"), Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 14,
            };
            w.Bg(Ui.KCard);
            OutfitMerge.Spec? result = null;
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 8 };
            p.Children.Add(Ui.Text("어디에 붙일까요?", 17, FontWeight.SemiBold));
            var why = analysis == null ? "" : analysis.NeedsPlacement
                ? $"{itemName}: 자동으로 장착 위치를 알아내지 못했습니다" + (analysis.Reason.Length > 0 ? $" ({analysis.Reason})" : "") + ". 종류와 장착할 본을 선택해 주세요."
                : $"{itemName}: 자동으로는 {Items.KindLabel(analysis.Kind)}로 보고 {Items.BoneLabel(analysis.Target)}에 붙였어요. 바꾸려면 골라 주세요.";
            p.Children.Add(Ui.Wrap(why, Ui.KText2, 13));

            // 종류
            string guess = current?.Kind ?? (analysis != null && analysis.Kind != "unknown" ? analysis.Kind : "accessory");
            var kinds = new[] { ("outfit", "의상", "고른 뼈부터 아래로 이름이 같은 본(또는 휴머노이드 뼈)을 찾아 합쳐요. 맞는 본이 없으면 통째로 붙여요."),
                                ("hair", "헤어", "머리에 붙여요. 헤어 안에 Head 같은 본이 있으면 그 본부터 합쳐요."),
                                ("accessory", "악세서리", "고른 뼈에 통째로 붙여요 (Bone Proxy처럼). 뼈를 따라 움직여요.") };
            var kindHint = Ui.Wrap("", Ui.KText2, 12);
            var bones = Items.BoneChoices(humanBones);
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 360 };
            foreach (var (_, label) in bones) combo.Items.Add(label);
            bool boneTouched = current != null;
            void PickBone(string id) { var i = bones.FindIndex(b => b.Id == id); if (i >= 0) combo.SelectedIndex = i; }
            string kind = guess;
            var radios = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            foreach (var (id, label, hint) in kinds)
            {
                var rb = new RadioButton { Content = label, GroupName = "kind", IsChecked = id == guess };
                rb.IsCheckedChanged += (_, __) =>
                {
                    if (rb.IsChecked != true) return;
                    kind = id; kindHint.Text = hint;
                    if (!boneTouched) PickBone(id == "outfit" ? "Hips" : "Head");
                };
                radios.Children.Add(rb);
            }
            kindHint.Text = kinds.First(k => k.Item1 == guess).Item3;
            p.Children.Add(Ui.Text("종류", 13, FontWeight.SemiBold));
            p.Children.Add(radios);
            p.Children.Add(kindHint);

            p.Children.Add(Ui.Text("붙일 곳", 13, FontWeight.SemiBold));
            PickBone(current?.Target is string t && t.Length > 0 ? t : guess == "outfit" ? "Hips" : "Head");
            if (combo.SelectedIndex < 0) combo.SelectedIndex = bones.Count - 1;
            combo.SelectionChanged += (_, __) => boneTouched = true;
            p.Children.Add(combo);
            bool atOrigin = analysis?.NearOrigin == true;
            var keep = new CheckBox { Content = "지금 위치 그대로 붙이기", IsChecked = current?.KeepWorld ?? !atOrigin };
            p.Children.Add(keep);
            if (atOrigin && current == null) p.Children.Add(Ui.Wrap("이 아이템은 발밑(원점)에 만들어져 있어서, 고른 뼈 위치로 옮기도록 해 뒀어요.", Ui.KAccentText, 12));
            p.Children.Add(Ui.Wrap("끄면 고른 뼈 위치로 옮겨요 (원점에 만들어 둔 소품을 손·머리에 바로 붙일 때). 아바타에 맞춰 놓인 아이템은 켜 두세요.", Ui.KText2, 12));

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var ok = Ui.Button("붙이기", () =>
            {
                var b = combo.SelectedIndex >= 0 ? bones[combo.SelectedIndex].Id : "@root";
                result = new OutfitMerge.Spec { Prefab = current?.Prefab ?? analysis?.AddedFrom ?? "", Kind = kind, Target = b, KeepWorld = keep.IsChecked == true };
                w.Close();
            }, accent: true);
            var cancel = Ui.Button("취소", () => w.Close());
            foreach (var b in new[] { ok, cancel }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            ok.IsDefault = true; cancel.IsCancel = true;
            row.Children.Add(ok); row.Children.Add(cancel);
            p.Children.Add(row);
            w.Content = p;
            var tcs = new TaskCompletionSource<OutfitMerge.Spec?>();
            w.Closed += (_, __) => tcs.TrySetResult(result);
            w.ShowDialog(owner);
            return tcs.Task;
        }
    }
}
