// "VRChat에 올리기" 창 (PLAN.md 3.10): 기믹 고르기·라이선스 동의 → 업로드 프로젝트 준비(Upload) → Unity 창을 열어 사용자가 SDK 창에서 로그인·업로드.
// 각 단계 상태를 줄마다 보여 주고, 실패하면 그 단계 로그를 보여 준다. 다시 누르면 받은 패키지·가져온 원본은 재사용한다.
// Unity 프로젝트에서 가져온 아바타 (2026-11-03): 업로드 프로젝트를 만들지 않고 원본 프로젝트에 새 장면을 만들어(내보내기와 같음) 그 프로젝트에서 올린다.
//   업로드 프로젝트로는 원본 Assets 전체(수십 GB)를 복사하게 되고, 그 프로젝트에 설치된 기믹(VRCFury 등)도 빠지기 때문.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class UploadWindow : Window
    {
        readonly EditorContext _ctx;
        readonly Document _doc;
        readonly string _project;
        readonly TextBox _name = new TextBox();
        readonly CheckBox _agree = new CheckBox { Content = "VRChat SDK 라이선스(Materials License Agreement)를 읽고 동의해요" };
        readonly CheckBox _thumb = new CheckBox { Content = "지금 미리보기로 썸네일 찍기 (1200×900)", IsChecked = true };
        readonly CheckBox _thumbPick = new CheckBox { IsChecked = true };
        readonly string? _picked;   // VR 사진 창에서 고른 썸네일 (2026-11-01)
        readonly Dictionary<string, CheckBox> _gimmicks = new Dictionary<string, CheckBox>();
        readonly TextBlock[] _stepText; readonly TextBlock[] _stepIcon;
        readonly TextBox _log = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Height = 150, AcceptsReturn = true };
        readonly Button _go, _close;
        bool _busy;
        readonly string? _orig;   // 원본 Unity 프로젝트 (거기서 올린다). 패키지에서 가져왔으면 null
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly string[] Steps;
        static readonly string[] PackageSteps = { "Unity 2022.3 찾기", "업로드 프로젝트 만들기", "VRChat SDK·기믹 받기 (공식 저장소)", "원본 패키지 가져오기", "변경 적용 (머티리얼·메시·켜고 끄기·아이템)", "Unity 열기 → SDK 창에서 로그인·올리기" };
        static readonly string[] ProjectSteps = { "원본 Unity 프로젝트 확인 (VRChat SDK)", "변경 내용·에디터 스크립트 넣기", "이 프로젝트에 없는 원본 넣기", "Unity에서 새 장면 만들기", "SDK 창에서 로그인·올리기" };

        public UploadWindow(EditorContext ctx, Func<string, bool>? sourceHas, string pickedThumb = "", Action? clearPick = null)
        {
            _ctx = ctx; _doc = ctx.Doc!;
            _orig = Upload.OriginalProject(_doc.Original);
            _project = _orig ?? Upload.ProjectFor(Upload.AvatarKey(_doc));
            Steps = _orig != null ? ProjectSteps : PackageSteps;
            Title = "VRChat에 올리기";
            Width = 620; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 6 };
            p.Children.Add(_orig != null
                ? Ui.DialogHeader("VRChat에 올리기", "원본 Unity 프로젝트에서 올려요.",
                    "이 아바타를 가져온 Unity 프로젝트에 새 장면을 만들어 지금 편집한 내용을 사본 에셋에 적용합니다. 원본 장면·프리팹·머티리얼은 바꾸지 않습니다. "
                    + "그 프로젝트에 설치된 VRChat SDK와 기믹을 그대로 쓰고, Unity가 열리면 VRChat SDK 창에서 직접 로그인하고 업로드합니다. 이 도구는 VRChat 계정 정보를 다루지 않고, VRChat이 만들거나 승인한 도구가 아닙니다.")
                : Ui.DialogHeader("VRChat에 올리기", "업로드용 Unity 프로젝트를 만들어 편집을 적용해요.",
                    "업로드용 Unity 프로젝트를 만들어 VRChat SDK와 선택한 기믹을 각 공식 저장소에서 받아 설치하고, 원본 패키지를 가져와 지금 편집한 내용을 사본 에셋에 적용합니다. "
                    + "그다음 Unity를 열면 VRChat SDK 창에서 직접 로그인하고 업로드합니다. 이 도구는 VRChat 계정 정보를 다루지 않고, VRChat이 만들거나 승인한 도구가 아닙니다."));

            _name.Text = _doc.Original.Name;
            p.Children.Add(Ui.PropRow(Ui.Text("아바타 이름"), _name, null));

            var gh = Ui.SectionTitle("같이 설치할 기믹", "BOOTH 등에서 산 유료 기믹은 자동으로 설치하지 않아요. 아바타·의상과 같이 붙인 패키지는 원본 그대로 가져와요. 아바타·의상의 이용 약관(업로드 허용 범위)은 직접 확인해 주세요.");
            gh.Margin = new Thickness(0, 6, 0, 2);
            gh.IsVisible = _orig == null;
            p.Children.Add(gh);
            var auto = UploadPlan.AutoGimmicks(_doc, sourceHas);
            var gl = new StackPanel { Spacing = 2 };
            foreach (var g in UploadPlan.Gimmicks)
            {
                var cb = new CheckBox { Content = g.Label, IsChecked = auto.Contains(g.Key) };
                var lic = Ui.Button(g.License, () => OpenUrl(g.LicenseUrl), "라이선스 보기");
                lic.Padding = new Thickness(6, 0); lic.MinHeight = 24; lic.Margin = new Thickness(8, 0, 0, 0); lic.FontSize = 12;
                var line = new DockPanel();
                var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (auto.Contains(g.Key)) right.Children.Add(Ui.Text("자동", 12, FontWeight.Normal, Ui.KAccentText));
                right.Children.Add(lic);
                DockPanel.SetDock(right, Dock.Right);
                line.Children.Add(right); line.Children.Add(cb);
                ToolTip.SetTip(line, g.Note + (g.Restricted ? "\n※ 재배포·수정이 금지된 라이선스라 공식 저장소에서 원본 그대로만 받습니다" : ""));
                gl.Children.Add(line);
                _gimmicks[g.Key] = cb;
            }
            p.Children.Add(gl);
            gl.IsVisible = _orig == null;   // 원본 프로젝트에서 올리면 그 프로젝트의 SDK·기믹을 그대로 쓴다
            if (_orig != null)
            {
                var info = new TextBlock { Text = $"원본 프로젝트: {_orig}\nVRChat SDK와 기믹은 이 프로젝트에 설치된 것을 그대로 써요. 새 장면은 Assets/VrcProto/Export 아래에 만들어요.",
                                           TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 2) };
                info.Fg(Ui.KText2);
                p.Children.Add(info);
            }

            // 동의 글은 줄바꿈하고 라이선스 단추는 오른쪽 (좁은 창에서 단추가 밖으로 밀리지 않게)
            var lic2 = new DockPanel();
            _agree.Content = new TextBlock { Text = "VRChat SDK 라이선스(Materials License Agreement)를 읽고 동의해요", TextWrapping = TextWrapping.Wrap };
            var sdkLic = Ui.Button("라이선스 보기", () => OpenUrl(UploadPlan.SdkLicenseUrl), "VRChat SDK 라이선스를 브라우저로 열어요");
            sdkLic.Margin = new Thickness(8, 0, 0, 0); sdkLic.Padding = new Thickness(8, 2); sdkLic.MinHeight = 26; sdkLic.FontSize = 12; sdkLic.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(sdkLic, Dock.Right);
            lic2.Children.Add(sdkLic);
            lic2.Children.Add(_agree);
            lic2.IsVisible = _orig == null;   // SDK를 받지 않으면 묻지 않는다 (그 프로젝트에 넣을 때 동의함)
            p.Children.Add(lic2);
            if (File.Exists(pickedThumb))
            {
                _picked = pickedThumb;
                _thumb.IsEnabled = false;
                _thumbPick.Content = new TextBlock { Text = $"고른 사진을 썸네일로: {Path.GetFileName(pickedThumb)} (1200×900에 맞게 가운데를 잘라요)", TextWrapping = TextWrapping.Wrap };
                _thumbPick.IsCheckedChanged += (_, __) => _thumb.IsEnabled = _thumbPick.IsChecked != true;
                var row = new DockPanel();
                var clear = Ui.Button("고르기 풀기", () => { clearPick?.Invoke(); _thumbPick.IsChecked = false; row.IsVisible = false; }, "이 아바타에 고른 썸네일을 지워요");
                clear.Margin = new Thickness(8, 0, 0, 0); clear.Padding = new Thickness(8, 2); clear.MinHeight = 26; clear.FontSize = 12; clear.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(clear, Dock.Right);
                row.Children.Add(clear); row.Children.Add(_thumbPick);
                p.Children.Add(row);
            }
            else _thumbPick.IsChecked = false;
            p.Children.Add(_thumb);

            var steps = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 2 };
            _stepText = new TextBlock[Steps.Length]; _stepIcon = new TextBlock[Steps.Length];
            for (int i = 0; i < Steps.Length; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                _stepIcon[i] = Ui.Text("○", 13, FontWeight.Normal, Ui.KText3); _stepIcon[i].Width = 16;
                _stepText[i] = Ui.Text(Steps[i], 13, FontWeight.Normal, Ui.KText2);
                row.Children.Add(_stepIcon[i]); row.Children.Add(_stepText[i]);
                steps.Children.Add(row);
            }
            p.Children.Add(steps);
            p.Children.Add(_log);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var folder = Ui.Button("프로젝트 폴더", () => { Directory.CreateDirectory(_project); Process.Start(new ProcessStartInfo("explorer.exe", _project) { UseShellExecute = true }); });
            _go = Ui.Button("준비하고 Unity 열기", () => _ = Run(), accent: true, glyph: Glyph.Upload);
            _close = Ui.Button("닫기", Close);
            foreach (var b in new[] { folder, _go, _close }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            _close.IsCancel = true;
            btns.Children.Add(folder); btns.Children.Add(_go); btns.Children.Add(_close);
            p.Children.Add(btns);
            Content = p;
            Closing += (_, e) => { if (_busy && _orig == null) e.Cancel = true; else _cts.Cancel(); };   // 원본 프로젝트: Unity를 기다리는 중에 닫으면 그만 기다린다
            Log("업로드 프로젝트: " + _project);
            if (pickedThumb.Length > 0 && _picked == null) Log("선택한 썸네일 사진이 없어졌습니다: " + pickedThumb);
        }

        static void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        void Log(string s) => Dispatcher.UIThread.Post(() => { _log.Text += (_log.Text?.Length > 0 ? "\n" : "") + s; _log.CaretIndex = _log.Text?.Length ?? 0; });

        void Step(int i, int state, string? note = null) => Dispatcher.UIThread.Post(() =>
        {
            // state: 0 대기, 1 진행 중, 2 끝, 3 실패
            _stepIcon[i].Text = state switch { 1 => "◐", 2 => "✓", 3 => "✕", _ => "○" };
            _stepIcon[i].Fg(state == 2 ? Ui.KSuccess : state == 3 ? Ui.KCritical : state == 1 ? Ui.KAccentText : Ui.KText3);
            _stepText[i].Text = Steps[i] + (note != null ? " · " + note : "");
            _stepText[i].Fg(state == 3 ? Ui.KCritical : state == 0 ? Ui.KText2 : Ui.KText);
        });

        async Task Run()
        {
            if (_busy) return;
            if (_orig == null && _agree.IsChecked != true) { Log("VRChat SDK 라이선스에 동의해야 SDK를 받을 수 있어요."); return; }
            var exclude = await ExportWindow.BlinkFixGuard(this, _doc, "올리기", Log);
            if (exclude == null) return;
            _busy = true; _go.IsEnabled = _close.IsEnabled = false;
            for (int i = 0; i < Steps.Length; i++) Step(i, 0);
            try
            {
                var name = string.IsNullOrWhiteSpace(_name.Text) ? _doc.Original.Name : _name.Text!.Trim();
                var keys = _gimmicks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                var thumbPath = Path.Combine(_project, "VrcProto", "thumbnail.png");
                Func<Task<string?>>? thumb = _picked != null && _thumbPick.IsChecked == true ? () => UsePicked(_picked, thumbPath)
                    : _thumb.IsChecked == true ? () => CaptureThumbnail(thumbPath) : null;
                if (_orig != null)
                {
                    _close.IsEnabled = true;   // Unity가 열리길 기다리는 동안에도 닫을 수 있게
                    var st = await Upload.Export(_doc, _orig, name, exclude.Value.Blink, Log, (i, s, note) => Step(i, s, note), _cts.Token, exclude.Value.Mouth, upload: true, thumbnail: thumb);
                    int up = Steps.Length - 1;
                    if (st["stage"].AsString == "opened") Step(up, 1, "Unity에 올리기 창이 열렸어요 · SDK 창에서 로그인하세요");
                    else Step(up, 1, "Unity에서 기다리는 중");
                    _busy = false;
                    await Watch(up);
                    return;
                }
                var unity = await Upload.Prepare(_doc, _project, name, keys, thumb, Log, (i, st, note) => Step(i, st, note), false, exclude.Value.Blink, exclude.Value.Mouth);
                int last = Steps.Length - 1;
                Step(last, 1);
                Upload.OpenUnity(unity, _project);
                Log("Unity를 여는 중… 열리면 VRChat SDK 창에서 로그인하고, \"ALEdit 업로드\" 창의 올리기나 SDK 창의 Build & Publish를 눌러 주세요.");
                _busy = false; _close.IsEnabled = true;
                await Watch(last);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log("✕ " + e.Message); }
            finally { _busy = false; _go.IsEnabled = _close.IsEnabled = true; }
        }

        /// Unity 쪽 상태 파일을 보며 열림·올림 결과를 보여 준다 (창을 닫으면 그만 본다)
        async Task Watch(int step)
        {
            string last = "";
            for (int i = 0; i < 60 * 60 && IsVisible; i++)
            {
                await Task.Delay(2000);
                var st = Upload.ReadStatus(_project);
                var key = st["stage"].AsString + st["time"].AsString;
                if (key == last) continue;
                last = key;
                switch (st["stage"].AsString)
                {
                    case "opened": Step(step, 1, "Unity가 열렸어요 · SDK 창에서 로그인하세요"); break;
                    case "uploaded": Step(step, st["ok"].AsBool ? 2 : 3, st["message"].AsString); Log(st["message"].AsString); if (st["ok"].AsBool) return; break;
                }
            }
        }

        /// 고른 사진을 VRChat 썸네일 크기로 맞춰 저장한다. 못 읽으면 건너뛴다.
        async Task<string?> UsePicked(string src, string path)
        {
            try
            {
                await Task.Run(() =>
                {
                    var img = Sticker.DecodePng(File.ReadAllBytes(src)) ?? throw new InvalidDataException("PNG를 읽지 못했어요");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, Sticker.EncodePng(Sticker.Cover(img, 1200, 900)));
                });
                Log("썸네일: " + Path.GetFileName(src) + " → " + path);
                return path;
            }
            catch (Exception e) { Log("선택한 사진을 썸네일로 만들지 못했습니다 (SDK 창에서 선택할 수 있습니다): " + e.Message); return null; }
        }

        /// 렌더러로 썸네일을 찍는다 (VRChat 썸네일 1200×900). 렌더러가 없으면 건너뛴다.
        async Task<string?> CaptureThumbnail(string path)
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { Log("미리보기가 없어 썸네일을 건너뛰어요 (SDK 창에서 고를 수 있어요)."); return null; }
            var tcs = new TaskCompletionSource<bool>();
            void On(JNode m) { if (Msg.Type(m) == Msg.CaptureDone && m["path"].AsString == path) tcs.TrySetResult(m["ok"].AsBool); }
            _ctx.RendererMessage += On;
            try
            {
                _ctx.Render.Send(Msg.Make(Msg.Capture).Set("path", path).Set("width", 1200).Set("height", 900).Set("transparent", false).Set("supersample", 2));
                var done = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                if (done != tcs.Task || !tcs.Task.Result) { Log("썸네일을 찍지 못했어요 (SDK 창에서 고를 수 있어요)."); return null; }
                Log("썸네일: " + path);
                return path;
            }
            finally { _ctx.RendererMessage -= On; }
        }
    }
}
