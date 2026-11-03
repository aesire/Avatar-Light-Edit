// 온라인 업데이트 UI: 사이드바 아래 "업데이트" (Updater.cs). 시작할 때마다 몰래 확인하고, 새 버전이 있을 때만 그 항목을 보인다
// (2026-10-20: 늘 보이던 것을 감춤. 저장소·자동 확인·직접 확인은 UpdateDialog — 설정 창에서 연다).
// 받아서 바꾸는 것은 사용자가 창에서 누를 때만 (저장하지 않은 변경이 있으면 먼저 묻는다).
using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        TextBlock? _updIcon, _updLabel;
        Control? _updItem;
        Updater.Release? _updFound;

        void UpdateSetup()
        {
            var (icon, label, item) = _side.AddBottomAction(Glyph.Refresh, "업데이트", () => UpdateDialog());
            _updIcon = icon; _updLabel = label; _updItem = item;
            item.IsVisible = false;   // 새 버전을 찾았을 때만 (ShowUpdateFound)
            ToolTip.SetTip(item, "새 버전을 받아요 (GitHub 릴리스)");
            bool checkedOnce = false;   // 숨겼다 다시 보일 때도 Opened가 온다
            Opened += async (_, __) =>
            {
                if (checkedOnce) return;
                checkedOnce = true;
                if (!_settings.UpdateAuto || _settings.UpdateRepo.Length == 0 || Updater.CurrentText == null || RunMode.Test) return;
                await Task.Delay(3000);
                try
                {
                    var r = await Updater.Latest(_settings.UpdateRepo);
                    _settings.UpdateLastCheck = DateTime.UtcNow.ToString("o"); _settings.Save();
                    if (Updater.IsNewer(r) && r.Tag != _settings.UpdateSkip) ShowUpdateFound(r);
                }
                catch (Exception e) { AppendLog("warn", "업데이트 확인 실패: " + e.Message); }
            };
        }

        /// <summary>시험용 (테스트 모드만, 2026-11-01): --test-update &lt;API 주소&gt; — 그 주소의 최신 릴리스를 대화상자의 "받아서 다시 시작"과 같은 길로 받아 바꾼다.</summary>
        async Task TestUpdate()
        {
            int i = Array.IndexOf(_args, "--test-update");
            if (!RunMode.Test || i < 0 || i + 1 >= _args.Length) return;
            Updater.ApiBase = _args[i + 1].TrimEnd('/');
            try
            {
                var r = await Updater.Latest("test/test");
                AppendLog("info", $"업데이트 시험: {r.Tag} · 새 버전 {Updater.IsNewer(r)}");
                if (!Updater.IsNewer(r)) return;
                var cmd = await Updater.Prepare(r, null);
                Updater.Apply(cmd);
                _closeConfirmed = true;
                Close();
            }
            catch (Exception e) { AppendLog("error", "업데이트 시험 실패: " + e); Crash.Write("test-update", e); }
        }

        void ShowUpdateFound(Updater.Release r)
        {
            _updFound = r;
            if (_updLabel != null) _updLabel.Text = $"업데이트 {r.Tag}";
            _updIcon?.Fg(Ui.KAccentText);
            if (_updItem != null) _updItem.IsVisible = true;
            SetStatus($"새 버전 {r.Tag}이 나왔어요. 왼쪽 아래 \"업데이트\"에서 받아요.");
        }

        void HideUpdateItem()
        {
            if (_updLabel != null) _updLabel.Text = "업데이트";
            _updIcon?.Fg(Ui.KText2);
            if (_updItem != null) _updItem.IsVisible = false;
        }

        async void UpdateDialog()
        {
            var w = new Window
            {
                Title = "업데이트", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = Ui.Font, FontSize = 14,
            };
            w.Bg(Ui.KCard);
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 8 };
            p.Children.Add(Ui.Text("업데이트", 17, FontWeight.SemiBold));
            p.Children.Add(Ui.Text("현재 버전:" + (Updater.CurrentText ?? "개발 빌드 (version.txt 없음)"), 13, FontWeight.Normal, Ui.KText2));
            var repo = new TextBox { Text = _settings.UpdateRepo, Watermark = "owner/name (GitHub 저장소)" };
            var auto = new CheckBox { Content = "시작할 때 확인", IsChecked = _settings.UpdateAuto };
            p.Children.Add(Ui.PropRow(Ui.Text("저장소"), repo, null));
            p.Children.Add(Ui.PropRow(Ui.Text("자동 확인"), auto, null));
            var state = Ui.Wrap("");
            var notes = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, IsVisible = false, AcceptsReturn = true };
            var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var check = Ui.Button("확인", () => { }, "최신 릴리스를 확인해요", Glyph.Refresh);
            var install = Ui.Button("받아서 다시 시작", () => { }, "새 버전을 받아 바꾸고 프로그램을 다시 열어요", Glyph.Upload);
            var skip = Ui.Button("이 버전 건너뛰기", () => { }, "이 버전은 다시 알리지 않아요");
            var close = Ui.Button("닫기", () => w.Close());
            install.IsVisible = skip.IsVisible = false;
            btns.Children.Add(skip); btns.Children.Add(check); btns.Children.Add(install); btns.Children.Add(close);
            p.Children.Add(state); p.Children.Add(notes); p.Children.Add(bar); p.Children.Add(btns);
            w.Content = p;

            void SaveOpts()
            {
                _settings.UpdateRepo = (repo.Text ?? "").Trim();
                _settings.UpdateAuto = auto.IsChecked == true;
                _settings.Save();
            }
            void Show(Updater.Release r)
            {
                bool newer = Updater.IsNewer(r);
                state.Text = (newer ? $"새 버전 {r.Tag}이 있어요" : $"최신 버전이에요 ({r.Tag})") + (r.ZipUrl.Length == 0 ? " · 받을 zip이 없는 릴리스입니다" : $" · {r.ZipName} ({r.ZipSize / 1048576.0:0.#} MB)");
                notes.Text = r.Notes; notes.IsVisible = r.Notes.Length > 0;
                install.IsVisible = r.ZipUrl.Length > 0 && (newer || Updater.CurrentText == null);
                skip.IsVisible = newer;
                _updFound = newer ? r : null;
                if (newer && r.Tag != _settings.UpdateSkip) ShowUpdateFound(r); else HideUpdateItem();
            }
            async Task Check()
            {
                SaveOpts();
                if (_settings.UpdateRepo.Length == 0) { state.Text = "업데이트를 받을 GitHub 저장소(owner/name)를 적어 주세요."; return; }
                state.Text = "확인하는 중…"; check.IsEnabled = false;
                try
                {
                    var r = await Updater.Latest(_settings.UpdateRepo);
                    _settings.UpdateLastCheck = DateTime.UtcNow.ToString("o"); _settings.Save();
                    Show(r);
                }
                catch (Exception e) { state.Text = "확인하지 못했습니다: " + e.Message; }
                finally { check.IsEnabled = true; }
            }
            check.Click += async (_, __) => await Check();
            skip.Click += (_, __) =>
            {
                if (_updFound == null) return;
                _settings.UpdateSkip = _updFound.Tag; _settings.Save();
                HideUpdateItem();
                state.Text = $"{_updFound.Tag}은 다시 알리지 않아요.";
                skip.IsVisible = install.IsVisible = false;
            };
            install.Click += async (_, __) =>
            {
                var r = _updFound;
                if (r == null) return;
                if (!await ConfirmDiscard()) return;
                install.IsEnabled = check.IsEnabled = false; bar.IsVisible = true;
                state.Text = $"{r.Tag} 받는 중…";
                try
                {
                    var cmd = await Updater.Prepare(r, new Progress<double>(v => bar.Value = v));
                    state.Text = "다시 시작해요…";
                    Updater.Apply(cmd);
                    _closeConfirmed = true;
                    w.Close();
                    Close();
                }
                catch (Exception e) { state.Text = "업데이트하지 못했습니다: " + e.Message; install.IsEnabled = check.IsEnabled = true; bar.IsVisible = false; }
            };
            auto.IsCheckedChanged += (_, __) => SaveOpts();
            repo.LostFocus += (_, __) => SaveOpts();
            if (_updFound != null) Show(_updFound);
            else if (_settings.UpdateRepo.Length == 0) state.Text = "업데이트를 받을 GitHub 저장소(owner/name)를 적고 \"확인\"을 눌러요.";
            else _ = Check();
            await w.ShowDialog(this);
        }
    }
}
