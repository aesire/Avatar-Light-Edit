// Unity 씬·프리팹 가져오기 창: 프로젝트(Assets)를 읽기 전용으로 색인하고 씬 전체를 해석한 뒤,
// 루트 오브젝트 목록에서 하나를 골라 IR로 만든다 (importsim의 UnityImport). 결과는 IR 경로로 돌려준다.
// .unitypackage를 주면 Unity 없이 패키지 안의 프리팹 목록에서 고른다 (아바타 패키지 + 머티리얼·셰이더 패키지를 함께 줄 수 있다).
// 패키지는 데이터로만 읽는다 (안의 스크립트는 실행하지 않는다).
// Models(스튜디오 소품, 2026-10-27): 패키지 목록에 FBX 모델도 넣고, Unity 프로젝트 밖의 .fbx 하나도 받는다 (.meta가 없어 Unity 기본 임포트 설정으로).
// IR은 %LOCALAPPDATA%\ALEdit\imports\<씬 이름>_<guid 앞 8자리>\ 에 쓴다. 원본 프로젝트에는 아무것도 쓰지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImportSim;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class ImportResult
    {
        public string IrPath = "";
        public List<string> Warnings = new List<string>();   // 고른 루트에 대한 경고
        public int SceneWarnings;                             // 씬 전체 해석 경고 수 (다른 루트 것 포함)
        public string Timing = "";                           // 단계별 걸린 시간 (ImportSim.Timing, 로그에 남긴다)
    }

    public sealed class ImportWindow : Window
    {
        readonly string _file;
        readonly string[] _files;
        readonly bool _packages;
        List<string?> _prefabGuids = new List<string?>();   // 패키지 모드: 목록 줄 → 프리팹 guid
        readonly TextBlock _state = Ui.Text("", 13, FontWeight.Normal, Ui.KText2);
        readonly ProgressBar _progress = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4, Margin = new Thickness(0, 4, 0, 4) };
        readonly ListBox _list = new ListBox { Height = 320, Background = Brushes.Transparent };
        readonly CheckBox _includeColorVariants = new CheckBox { Content = "다른 색의 텍스처도 함께 가져오기", IsChecked = false, Margin = new Thickness(0, 6, 0, 0) };
        readonly Button _import, _cancel;
        UnityImport? _imp;
        SimFile? _scene;
        List<UnityImport.RootInfo> _roots = new List<UnityImport.RootInfo>();
        /// <summary>스튜디오 소품: 프리팹뿐 아니라 FBX 모델도 고를 수 있게 (패키지 목록·프로젝트 밖 .fbx).</summary>
        public bool Models { get; init; }
        bool _busy, _writing;   // _writing: IR을 쓰는 중 (이때만 닫기를 막는다. 색인·해석은 읽기만 하므로 취소해도 된다)

        /// <summary>
        /// 지난번 선택 (2026-10-27 동선 줄이기 3번): 같은 파일(들)을 다시 가져오면 그때 고른 오브젝트·"다른 색 텍스처"를 채워 둔다 → Enter 한 번.
        /// 설정의 ImportChoices를 MainWindow가 넣어 주고, 닫힌 뒤 저장한다. 열쇠는 원본 경로(소문자, 정렬)
        /// </summary>
        public static JNode? Choices;
        const int ChoicesMax = 40;
        string ChoiceKey => string.Join("|", _files.Select(f => f.ToLowerInvariant()).OrderBy(f => f, StringComparer.Ordinal));
        JNode Remembered => Choices?[ChoiceKey] ?? JNode.Null;

        public ImportWindow(params string[] files)
        {
            _files = files.Select(Path.GetFullPath).ToArray();
            _file = _files[0];
            _packages = _files.Any(f => f.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase));
            Title = "Unity에서 가져오기";
            Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 6 };
            p.Children.Add(Ui.Text("가져올 오브젝트 고르기", 17, FontWeight.SemiBold));
            p.Children.Add(Ui.Wrap(_packages
                ? string.Join(", ", _files.Select(Path.GetFileName)) + " 안의 프리팹입니다. Unity 없이 패키지에서 바로 가져옵니다. 머티리얼이 따로 든 패키지가 있으면 함께 선택해 주세요. 패키지는 읽기만 합니다."
                : Path.GetFileName(_file) + " 안의 루트 오브젝트입니다. 씬에서 변경한 값(옷 켜고 끄기, 머티리얼 교체 등)까지 그대로 가져옵니다. 원본 프로젝트는 읽기만 합니다.", Ui.KText2, 13));
            p.Children.Add(_state);
            p.Children.Add(_progress);
            _list.IsVisible = false;
            _list.SelectionChanged += (_, __) => Refresh();
            _list.DoubleTapped += (_, __) => Import();
            p.Children.Add(_list);
            if (Remembered["colorVariants"].Kind == JKind.Bool) _includeColorVariants.IsChecked = Remembered["colorVariants"].AsBool;
            ToolTip.SetTip(_includeColorVariants, "현재 머티리얼이 쓰지 않는 같은 계열의 색상 이름 텍스처도 가져와요. 나중에 머티리얼의 텍스처 선택 목록에서 고를 수 있어요.");
            p.Children.Add(_includeColorVariants);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            _import = Ui.Button("가져오기", Import, accent: true);
            _cancel = Ui.Button("취소", () => Close(null));
            foreach (var b in new[] { _import, _cancel }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            _import.IsDefault = true; _cancel.IsCancel = true;
            row.Children.Add(_import); row.Children.Add(_cancel);
            p.Children.Add(row);
            Content = p;

            Closing += (_, e) => { if (_writing) e.Cancel = true; };
            Opened += async (_, __) => await Load();
            Refresh();
        }

        async Task Load()
        {
            if (_packages) { await LoadPackages(); return; }
            var assets = UnityImport.FindAssetsDir(_file);
            bool loose = Models && UnityImport.IsLooseModel(_file);
            if (assets == null && !loose) { Fail("이 파일은 Unity 프로젝트의 Assets 폴더 안에 있지 않아요."); return; }
            SetBusy(true, loose ? "FBX 읽는 중…" : "프로젝트 색인 중… 처음 열 때는 큰 프로젝트에서 수십 초 걸려요.");
            ImportSim.Timing.Start();
            try
            {
                _imp = await Task.Run(() => loose ? UnityImport.Open(new[] { _file }) : UnityImport.Open(assets!));
                if (!IsVisible) return;
                SetBusy(true, $"에셋 {_imp.Db.ByGuid.Count:N0}개 · 씬 해석 중…");
                _scene = await Task.Run(() => _imp.Resolve(_file));
                _roots = _imp.Roots(_scene);
            }
            catch (Exception e) { Fail(e.Message); return; }
            SetBusy(false, $"루트 {_roots.Count}개" + (_scene.Warnings.Count > 0 ? $" · 해석 경고 {_scene.Warnings.Count}건 (가져온 파일의 warnings에 기록됩니다)" : ""));

            foreach (var r in _roots) _list.Items.Add(Row(r));
            _list.IsVisible = true;
            // 기본 선택: 켜진 아바타 → 렌더러가 가장 많은 것
            var pick = _roots.FindIndex(r => r.IsAvatar && r.Active);
            if (pick < 0 && _roots.Count > 0) pick = _roots.IndexOf(_roots.OrderByDescending(r => r.Renderers).First());
            _list.SelectedIndex = RememberedIndex() ?? pick;
            _list.Focus();
            Refresh();
        }

        async Task LoadPackages()
        {
            SetBusy(true, "패키지 읽는 중…");
            ImportSim.Timing.Start();
            List<UnityImport.PrefabInfo> prefabs;
            try
            {
                _imp = await Task.Run(() => UnityImport.Open(_files));
                prefabs = await Task.Run(() => _imp.PackagePrefabs(Models));
            }
            catch (Exception e) { Fail(e.Message); return; }
            if (!IsVisible) return;
            // 아바타 먼저, 그다음 렌더러가 있는 것
            prefabs = prefabs.OrderByDescending(x => x.IsAvatar).ThenByDescending(x => x.Renderers > 0).ToList();
            _roots = prefabs.Select(x => new UnityImport.RootInfo { Name = x.Name, Active = true, IsAvatar = x.IsAvatar, Nodes = x.Nodes, Renderers = x.Renderers }).ToList();
            _prefabGuids = prefabs.Select(x => (string?)x.Guid).ToList();
            SetBusy(false, Models ? $"프리팹·모델 {_roots.Count}개" : $"프리팹 {_roots.Count}개 · 아바타 {prefabs.Count(x => x.IsAvatar)}개");
            foreach (var r in _roots) _list.Items.Add(Row(r));
            _list.IsVisible = true;
            _list.SelectedIndex = RememberedIndex() ?? (_roots.Count > 0 ? 0 : -1);
            _list.Focus();
            Refresh();
        }

        /// <summary>지난번에 고른 오브젝트가 목록에 있으면 그 줄 (이름으로, 상태 줄에 알림)</summary>
        int? RememberedIndex()
        {
            var name = Remembered["root"].AsString;
            if (name.Length == 0) return null;
            int i = _roots.FindIndex(r => r.Name == name);
            if (i < 0) return null;
            _state.Text += " · 지난번 선택을 유지했습니다 (Enter로 가져오기)";
            return i;
        }

        void RememberChoice(string root, bool colorVariants)
        {
            if (Choices == null || Choices.Kind != JKind.Object) return;
            Choices.Remove(ChoiceKey);   // 지우고 다시 넣어 맨 뒤(새것)로
            Choices.Set(ChoiceKey, JNode.Obj().Set("root", root).Set("colorVariants", colorVariants));
            var keys = Choices.Props.Select(kv => kv.Key).ToList();
            for (int i = 0; i < keys.Count - ChoicesMax; i++) Choices.Remove(keys[i]);
        }

        static Control Row(UnityImport.RootInfo r)
        {
            var d = new DockPanel { Margin = new Thickness(4, 6) };
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (r.IsAvatar) tags.Children.Add(Badge("아바타", Ui.KAccentText));
            if (!r.Active) tags.Children.Add(Badge("꺼짐", Ui.KText3));
            tags.Children.Add(Ui.Text($"메시 {r.Renderers} · 오브젝트 {r.Nodes:N0}", 12, FontWeight.Normal, Ui.KText2));
            DockPanel.SetDock(tags, Dock.Right);
            d.Children.Add(tags);
            var icon = Ui.Icon(r.IsAvatar ? Glyph.Avatar : Glyph.Scene, 14, r.Renderers > 0 ? Ui.KText : Ui.KText3);
            icon.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(icon, Dock.Left);
            d.Children.Add(icon);
            d.Children.Add(Ui.Text(r.Name, 14, FontWeight.Normal, r.Renderers > 0 ? Ui.KText : Ui.KText3));
            ToolTip.SetTip(d, r.Renderers == 0 ? "메시가 없어 미리보기에 보이는 것이 없어요" : null);
            return d;
        }

        static Border Badge(string s, string fg) =>
            new Border { Child = Ui.Text(s, 11, FontWeight.SemiBold, fg), Padding = new Thickness(6, 1), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) }.Stroke(Ui.KCardStroke);

        async void Import()
        {
            if (_busy || _imp == null || (_scene == null && !_packages) || _list.SelectedIndex < 0) return;
            var root = _roots[_list.SelectedIndex];
            var imp = _imp; var scene = _scene;
            var prefab = _packages ? _prefabGuids[_list.SelectedIndex] : null;
            var guid = prefab ?? imp.Db.ByGuid.Values.FirstOrDefault(a => a.FilePath != null && string.Equals(a.FilePath, Path.GetFullPath(_file), StringComparison.OrdinalIgnoreCase))?.Guid ?? "";
            var includeColorVariants = _includeColorVariants.IsChecked == true;
            var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "imports",
                Safe(prefab != null ? root.Name : Path.GetFileNameWithoutExtension(_file)) + (guid.Length >= 8 ? "_" + guid.Substring(0, 8) : ""));
            SetBusy(true, $"{root.Name} 가져오는 중… (메시·블렌드쉐이프 계산)");
            _writing = true;
            try
            {
                ImportSim.Timing.Step("(루트 고르기 기다림)");
                var (path, warns, _) = await Task.Run(() =>
                {
                    var f = prefab != null ? imp.ResolveGuid(prefab) : scene!;
                    var rootId = prefab != null ? (f.Root?.Id ?? throw new InvalidOperationException("프리팹 루트가 없어요")) : root.Id;
                    return imp.Export(f, rootId, outDir, null, includeColorVariants);
                });
                _busy = _writing = false;
                RememberChoice(root.Name, includeColorVariants);
                Close(new ImportResult { IrPath = path, Warnings = warns, SceneWarnings = scene?.Warnings.Count ?? 0,
                    Timing = string.Join(" · ", ImportSim.Timing.Steps.Where(x => !x.Name.StartsWith("(")).Select(x => $"{x.Name} {x.Ms / 1000.0:0.0}초")) });
            }
            catch (Exception e) { _writing = false; Fail("가져오지 못했습니다: " + e.Message); }
        }

        static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

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

        void Refresh()
        {
            _import.IsEnabled = !_busy && _list.SelectedIndex >= 0;
            _cancel.IsEnabled = !_writing;
            _list.IsEnabled = !_busy;
        }
    }
}
