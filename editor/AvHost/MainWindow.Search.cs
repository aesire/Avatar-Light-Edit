// Ctrl+K 찾기 (UI 개선 3번, 2026-10-21): 페이지·페이지 안 탭·설정 줄 이름·명령을 한 칸에서 찾아 바로 간다.
// 설정 줄은 그때 만들어져 있는 탭 내용에서 이름 글자(줄 이름·구역 제목)를 모은다 → 머티리얼은 지금 고른 머티리얼의 섹션만.
// 고르면 그 페이지·탭으로 가서 줄을 화면에 보이게 하고 잠깐 강조한다 (떠 있는 탭이면 그 창을 앞으로).
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        sealed record SearchEntry(string Title, string Where, Action Go);

        static readonly (string Key, string Title)[] PageTitles =
        {
            ("avatar", "아바타"), ("material", "머티리얼"), ("shapes", "블렌드쉐이프"), ("pose", "자세"), ("environment", "환경"),
            ("studio", "스튜디오"), ("optimize", "최적화"), ("upload", "올리기"), ("log", "로그"),
        };

        Flyout? _searchFly;

        void OpenSearch()
        {
            if (_searchFly?.IsOpen == true) { _searchFly.Hide(); return; }
            var entries = SearchIndex();
            var box = Ui.SearchBox("페이지·탭·설정 이름으로 찾기 (예: 그림자, 쥐기, 오버레이)");
            var list = new ListBox { MaxHeight = 420, Background = Brushes.Transparent };
            var foot = Ui.Text("↑↓ 고르기 · Enter 가기 · Esc 닫기", 11, FontWeight.Normal, Ui.KText3);
            var panel = new StackPanel { Width = 540, Spacing = 8 };
            panel.Children.Add(box); panel.Children.Add(list); panel.Children.Add(foot);
            var fly = _searchFly = new Flyout { Content = panel, Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Standard };

            void Fill()
            {
                var q = (box.Text ?? "").Trim();
                IEnumerable<SearchEntry> hits = entries;
                if (q.Length > 0)
                    hits = entries.Select(e => (e, s: Score(e, q))).Where(x => x.s > 0).OrderByDescending(x => x.s).Select(x => x.e);
                else hits = RecentEntries(entries);   // 빈 칸: 최근 고른 것 → 최근 파일
                var items = new List<ListBoxItem>();
                foreach (var e in hits.Take(40))
                {
                    var c = new StackPanel { Spacing = 1 };
                    c.Children.Add(Ui.Text(e.Title, 14));
                    c.Children.Add(Ui.Text(e.Where, 11, FontWeight.Normal, Ui.KText3));
                    var it = new ListBoxItem { Content = c, Tag = e, Padding = new Thickness(10, 6) };
                    it.DoubleTapped += (_, __) => Go(e);
                    it.PointerReleased += (_, ev) => { if (ev.InitialPressMouseButton == MouseButton.Left) Go(e); };
                    items.Add(it);
                }
                list.ItemsSource = items;
                list.SelectedIndex = items.Count > 0 ? 0 : -1;
                foot.Text = q.Length == 0 ? $"최근 고른 것 · 찾을 수 있는 곳 {entries.Count}개 · ↑↓ 고르기 · Enter 가기 · Esc 닫기" : items.Count == 0 ? "찾은 것이 없어요" : "↑↓ 고르기 · Enter 가기 · Esc 닫기";
            }

            void Go(SearchEntry e) { fly.Hide(); RememberSearch(e); Dispatcher.UIThread.Post(e.Go, DispatcherPriority.Background); }

            box.TextChanged += (_, __) => Fill();
            box.KeyDown += (_, e) =>
            {
                int n = list.ItemCount;
                if (e.Key == Key.Down && n > 0) { list.SelectedIndex = Math.Min(n - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedIndex); e.Handled = true; }
                else if (e.Key == Key.Up && n > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedIndex); e.Handled = true; }
                else if (e.Key == Key.Enter && list.SelectedItem is ListBoxItem { Tag: SearchEntry se }) { Go(se); e.Handled = true; }
                else if (e.Key == Key.Escape) { fly.Hide(); e.Handled = true; }
            };
            fly.Opened += (_, __) => Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Input);
            Fill();
            fly.ShowAt(_paneBorder);
        }

        /// <summary>글자가 모두 들어 있으면 점수: 제목 시작 > 제목 안 > 위치 안. 띄어쓰기로 나눈 낱말은 모두 맞아야.</summary>
        static int Score(SearchEntry e, string q)
        {
            int total = 0;
            foreach (var w in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                // 화면에 보이는 글(글 표)과 코드의 원문 둘 다로 찾는다 (2026-11-02): "BlendShape"도 "블렌드쉐이프"도 찾히게
                int s = Math.Max(TitleScore(e.Title, w), TitleScore(Texts.T(e.Title), w));
                if (s == 0 && (e.Where.Contains(w, StringComparison.OrdinalIgnoreCase) || Texts.T(e.Where).Contains(w, StringComparison.OrdinalIgnoreCase))) s = 8;
                if (s == 0) return 0;
                total += s;
            }
            return total - Math.Min(10, e.Title.Length / 6);   // 짧은 이름이 조금 앞으로
        }

        static int TitleScore(string title, string w) => title.StartsWith(w, StringComparison.OrdinalIgnoreCase) ? 30 : title.Contains(w, StringComparison.OrdinalIgnoreCase) ? 20 : 0;

        List<SearchEntry> SearchIndex()
        {
            var list = new List<SearchEntry>();
            bool easy = Classes.Contains("easy");
            // 명령
            void Cmd(string t, string where, Action a) => list.Add(new SearchEntry(t, "명령 · " + where, a));
            Cmd("Unity 씬·프리팹·패키지 가져오기", "Ctrl+I", ImportUnityDialog);
            Cmd("의상·헤어·악세서리 붙이기", "Ctrl+Shift+A", AddItemDialog);
            Cmd("가져온 파일 관리", "아바타", OpenFilesWindow);
            Cmd("머티리얼·에셋 관리", "머티리얼", OpenAssetsWindow);
            Cmd("세션 저장", "Ctrl+S", () => _ = SaveSession(false));
            Cmd("파일까지 담아 저장 (.alexp)", "저장", () => _ = SaveSession(true, pack: true));
            Cmd("VRChat에 올리기", "Ctrl+U", UploadDialog);
            Cmd("Unity 프로젝트로 내보내기", "Ctrl+Shift+U", ExportDialog);
            Cmd("설정", "Ctrl+,", SettingsDialog);
            Cmd("단축키 목록", "Ctrl+/", ShowShortcuts);
            Cmd(_settings.ExpertMode ? "쉬운 모드로 바꾸기" : "전문가 모드로 바꾸기", "보이는 설정 수", () => SetExpert(!_settings.ExpertMode));
            Cmd("셰이더 팩 추가", "Ctrl+Shift+P", ShaderPackDialog);
            // 최근 파일 (이름으로도 찾는다)
            var recent = RecentFiles();
            for (int i = 0; i < recent.Count; i++)
            {
                var path = recent[i];
                var (name, folder) = RecentLabel(path);
                list.Add(new SearchEntry(name, "최근 파일 · " + (i < 9 ? $"Ctrl+Shift+{i + 1} · " : "") + folder, () => OpenPath(path)));
            }

            foreach (var (key, title) in PageTitles)
            {
                if (!_pages.TryGetValue(key, out var page)) continue;
                var pageKey = key;
                list.Add(new SearchEntry(title, "페이지", () => _side.Select(pageKey)));
                var tabs = page.GetLogicalDescendants().OfType<SubTabs>().FirstOrDefault();
                if (tabs == null) continue;
                foreach (var (tabKey, tabTitle, content) in tabs.Entries(easy))
                {
                    var tk = tabKey;
                    list.Add(new SearchEntry(tabTitle, $"{title} › 탭", () => { _side.Select(pageKey); tabs.Select(tk); }));
                    if (content == null) continue;
                    var seen = new HashSet<string>();
                    foreach (var tb in content.GetLogicalDescendants().OfType<TextBlock>())
                    {
                        var text = (tb.Text ?? "").Trim();
                        if (text.Length < 2 || text.Length > 40 || tb.FontSize < 13.5 || tb.FontSize > 15.5 || text == tabTitle || !seen.Add(text)) continue;
                        if (text.All(c => char.IsDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c))) continue;
                        if (Hidden(tb, easy)) continue;
                        var target = tb;
                        list.Add(new SearchEntry(text, $"{title} › {tabTitle}", () => { _side.Select(pageKey); tabs.Select(tk); Reveal(target); }));
                    }
                }
            }
            return list;
        }

        // 최근 고른 것 (2026-10-27): 제목 + 위치로 기억한다. 최근 파일은 위치가 "최근 파일 · …"라 따로 기억하지 않는다 (목록이 이미 새것 먼저)
        static string SearchKey(SearchEntry e) => e.Title + "\u0001" + e.Where;

        void RememberSearch(SearchEntry e)
        {
            if (e.Where.StartsWith("최근 파일", StringComparison.Ordinal)) return;
            var list = _settings.RecentCommands;
            var k = SearchKey(e);
            list.Remove(k);
            list.Insert(0, k);
            if (list.Count > 5) list.RemoveRange(5, list.Count - 5);
            _settings.Save();
        }

        IEnumerable<SearchEntry> RecentEntries(List<SearchEntry> entries)
        {
            var byKey = new Dictionary<string, SearchEntry>();
            foreach (var e in entries) byKey.TryAdd(SearchKey(e), e);
            foreach (var k in _settings.RecentCommands)
                if (byKey.TryGetValue(k, out var e)) yield return e;
            foreach (var e in entries)
                if (e.Where.StartsWith("최근 파일", StringComparison.Ordinal)) yield return e;
        }

        /// <summary>단추·목록 안 글자거나 쉬운 모드에서 숨긴 것이면 찾기에서 뺀다</summary>
        static bool Hidden(ILogical c, bool easy)
        {
            for (var p = c.LogicalParent; p != null; p = p.LogicalParent)
            {
                if (p is Button or ComboBox or ComboBoxItem or MenuItem or ListBoxItem or CheckBox) return true;
                if (easy && p is Control pc && pc.Classes.Contains("expert")) return true;
                if (p is SubTabs) break;
            }
            return false;
        }

        /// <summary>찾은 줄을 화면에 보이게 하고 잠깐 강조한다</summary>
        void Reveal(TextBlock tb)
        {
            Dispatcher.UIThread.Post(() =>
            {
                tb.BringIntoView();
                var old = tb.Background;
                var bind = tb.Bind(TextBlock.BackgroundProperty, tb.GetResourceObservable(Ui.KSelect));
                DispatcherTimer.RunOnce(() => { bind.Dispose(); tb.Background = old ?? Brushes.Transparent; }, TimeSpan.FromMilliseconds(1400));
            }, DispatcherPriority.Background);
        }
    }
}
