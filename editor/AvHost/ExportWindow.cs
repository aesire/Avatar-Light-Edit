// "Unity 프로젝트로 내보내기" 창: 지금 편집한 내용을 사용자의 기존 Unity 프로젝트에 새 장면으로 (Upload.Export).
// 원본 장면·프리팹·머티리얼·임포트 설정·기믹(MA·VRCFury 등) 설정은 그대로 두고, Assets/VrcProto/Export/<이름>_<시각>/에 새 장면과 바뀐 사본만 만든다.
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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed class ExportWindow : Window
    {
        readonly Document _doc;
        readonly TextBox _project = new TextBox(), _name = new TextBox();
        readonly TextBlock[] _stepText, _stepIcon;
        readonly TextBox _log = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Height = 150, AcceptsReturn = true };
        readonly Button _go, _close;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        bool _busy;

        public ExportWindow(EditorContext ctx)
        {
            _doc = ctx.Doc!;
            Title = "Unity 프로젝트로 내보내기";
            Width = 620; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 6 };
            p.Children.Add(Ui.DialogHeader("Unity 프로젝트로 내보내기", "내 프로젝트에 새 장면으로 만들어요. 원본은 그대로예요.",
                "지금 편집한 내용을 내 Unity 프로젝트에 새 씬으로 만듭니다. 원본 씬·프리팹·머티리얼·임포트 설정은 수정하지 않고, "
                + "Assets/VrcProto/Export/<이름>_<시각>/ 폴더에 새 씬과 변경된 머티리얼·메시 사본만 만듭니다. MA·VRCFury 같은 기믹 설정은 원본 그대로입니다.\n\n"
                + "이 프로젝트에 없는 원본(장착한 의상 패키지 등)은 없는 파일만 넣고, 에디터 스크립트 두 개(Assets/VrcProto/Editor)를 넣습니다. "
                + "Unity가 이 프로젝트를 열고 있으면 Unity 창으로 가면 만들어지고, 닫혀 있으면 창 없이 만듭니다."));

            _project.Text = Upload.OriginalProject(_doc.Original) ?? "";
            _project.Watermark = "Assets와 ProjectSettings가 있는 폴더";
            var pick = Ui.Button("찾아보기…", () => _ = Pick());
            pick.Margin = new Thickness(6, 0, 0, 0);
            var projRow = new DockPanel();
            DockPanel.SetDock(pick, Dock.Right);
            projRow.Children.Add(pick); projRow.Children.Add(_project);
            p.Children.Add(Ui.PropRow(Ui.Text("Unity 프로젝트"), projRow, null));
            _name.Text = _doc.Original.Name;
            p.Children.Add(Ui.PropRow(Ui.Text("장면 이름"), _name, null));

            var existing = UploadPlan.ExistingBlinkFix(_doc.Original);
            if (existing.Count > 0)
            {
                var drop = Dropped(_doc);
                p.Children.Add(Ui.Notice("원본에 이미 눈 깜빡임 보정이 있습니다: " + string.Join(", ", existing)
                    + (drop.Length > 0 ? $". 이 에디터의 {drop}는 내보내기에서 빠져요." : ". 이 에디터의 보정은 쓰지 않아 그대로예요.")));
            }

            var steps = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 2 };
            _stepText = new TextBlock[Upload.ExportSteps.Length]; _stepIcon = new TextBlock[Upload.ExportSteps.Length];
            for (int i = 0; i < _stepText.Length; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                _stepIcon[i] = Ui.Text("○", 13, FontWeight.Normal, Ui.KText3); _stepIcon[i].Width = 16;
                _stepText[i] = Ui.Text(Upload.ExportSteps[i], 13, FontWeight.Normal, Ui.KText2);
                row.Children.Add(_stepIcon[i]); row.Children.Add(_stepText[i]);
                steps.Children.Add(row);
            }
            p.Children.Add(steps);
            p.Children.Add(_log);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var folder = Ui.Button("프로젝트 폴더", () => { var d = _project.Text?.Trim() ?? ""; if (Directory.Exists(d)) Process.Start(new ProcessStartInfo("explorer.exe", d) { UseShellExecute = true }); });
            _go = Ui.Button("내보내기", () => _ = Run(), accent: true, glyph: Glyph.Save);
            _close = Ui.Button("닫기", Close);
            foreach (var b in new[] { folder, _go, _close }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            _close.IsCancel = true;
            btns.Children.Add(folder); btns.Children.Add(_go); btns.Children.Add(_close);
            p.Children.Add(btns);
            Content = p;
            // 일괄 처리(batchmode) 중에는 닫지 않는다. Unity 창을 기다리는 중이면 닫을 때 그만 기다린다
            Closing += (_, e) => { if (_busy) e.Cancel = true; };
            Closed += (_, __) => _cts.Cancel();
        }

        async Task Pick()
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Texts.T("Unity 프로젝트 폴더") });
            if (dirs.Count > 0 && dirs[0].TryGetLocalPath() is string path) _project.Text = path;
        }

        void Log(string s) => Dispatcher.UIThread.Post(() => { _log.Text += (_log.Text?.Length > 0 ? "\n" : "") + s; _log.CaretIndex = _log.Text?.Length ?? 0; });

        void Step(int i, int state, string? note = null) => Dispatcher.UIThread.Post(() =>
        {
            _stepIcon[i].Text = state switch { 1 => "◐", 2 => "✓", 3 => "✕", _ => "○" };
            _stepIcon[i].Fg(state == 2 ? Ui.KSuccess : state == 3 ? Ui.KCritical : state == 1 ? Ui.KAccentText : Ui.KText3);
            _stepText[i].Text = Upload.ExportSteps[i] + (note != null ? " · " + note : "");
            _stepText[i].Fg(state == 3 ? Ui.KCritical : state == 0 ? Ui.KText2 : Ui.KText);
        });

        /// <summary>원본의 보정과 겹쳐 뺄 이 에디터의 보정 이름 ("Blink Fix", "Blink Fix·Mouth Fix", 없으면 "").</summary>
        static string Dropped(Document doc)
        {
            var s = new List<string>();
            if (UploadPlan.ExistingBlinkFix(doc.Original).Count == 0) return "";
            if (UploadPlan.UsesBlinkFix(doc)) s.Add("Blink Fix");
            if (UploadPlan.ExistingMouthFix(doc.Original) && UploadPlan.UsesBlinkFix(doc, MeshOps.MouthFix)) s.Add("Mouth Fix");
            return string.Join("·", s);
        }

        /// <summary>
        /// 원본에 이미 눈 깜빡임 보정이 있고 이 에디터의 Blink Fix(그 보정이 입도 고치면 Mouth Fix도)를 쓰면 경고하고 뺀다 (같은 보정이 두 번 들어가지 않게).
        /// 반환: null = 취소, 아니면 (깜빡임을 뺌, 입을 뺌).
        /// </summary>
        public static async Task<(bool Blink, bool Mouth)?> BlinkFixGuard(Window owner, Document doc, string what, Action<string> log)
        {
            var existing = UploadPlan.ExistingBlinkFix(doc.Original);
            if (existing.Count == 0) return (false, false);
            bool blink = UploadPlan.UsesBlinkFix(doc), mouth = UploadPlan.ExistingMouthFix(doc.Original) && UploadPlan.UsesBlinkFix(doc, MeshOps.MouthFix);
            if (!blink && !mouth) { log($"원본에 눈 깜빡임 보정이 있어요 ({string.Join(", ", existing)}). 이 에디터의 같은 보정은 쓰지 않아 그대로 진행해요."); return (false, false); }
            var names = Dropped(doc);
            var r = await Dialog.Ask(owner, "기존 Blink Fix가 있어요",
                $"원본 아바타에 이미 눈 깜빡임 보정이 들어 있습니다: {string.Join(", ", existing)}" + (mouth ? " (입 보정 포함)" : "") + "\n\n"
                + $"둘 다 적용하면 같은 보정이 두 번 들어가 눈이 덜 감기거나 찌그러질 수 있어서, 이 에디터의 {names}는 {what}에서 제거합니다. "
                + $"기존 보정 컴포넌트는 그대로 두고, 에디터 안의 {names} 설정도 삭제하지 않습니다.",
                $"빼고 {what}", "취소");
            if (r != 0) return null;
            log($"이 에디터의 {names}를 빼고 {what}해요 (원본의 보정을 써요)");
            return (blink, mouth);
        }

        async Task Run()
        {
            if (_busy) return;
            var project = _project.Text?.Trim() ?? "";
            if (!Upload.IsUnityProject(project)) { Log("✕ Unity 프로젝트 폴더를 골라 주세요 (Assets와 ProjectSettings가 있는 폴더)."); return; }
            var exclude = await BlinkFixGuard(this, _doc, "내보내기", Log);
            if (exclude == null) return;
            _busy = true; _go.IsEnabled = _close.IsEnabled = false;
            for (int i = 0; i < _stepText.Length; i++) Step(i, 0);
            try
            {
                var name = string.IsNullOrWhiteSpace(_name.Text) ? _doc.Original.Name : _name.Text!.Trim();
                // Unity 창을 기다리는 동안에는 닫을 수 있게 한다 (batchmode는 Export 안에서 끝까지 기다림)
                var task = Upload.Export(_doc, project, name, exclude.Value.Blink, Log, (i, st, note) =>
                {
                    Step(i, st, note);
                    if (i == Upload.ExportSteps.Length - 1 && note == "Unity에서 기다리는 중") Dispatcher.UIThread.Post(() => { _busy = false; _close.IsEnabled = true; });
                }, _cts.Token, exclude.Value.Mouth);
                var st = await task;
                Log("✓ 새 씬: " + st["scene"].AsString);
                // 예전 IR이라 미리 몰랐는데 Unity 쪽이 원본의 보정을 찾아 뺀 경우
                if (!exclude.Value.Blink && st["blinkFixSkipped"].AsInt > 0 || !exclude.Value.Mouth && st["mouthFixSkipped"].AsInt > 0)
                    await Dialog.Ask(this, "기존 Blink Fix가 있어요",
                        "원본 아바타에 이미 눈 깜빡임 보정이 들어 있어서 ("
                        + string.Join(", ", st["existingBlinkFix"].Items.Select(x => x.AsString))
                        + "), 이 에디터의 " + (st["mouthFixSkipped"].AsInt > 0 ? (st["blinkFixSkipped"].AsInt > 0 ? "Blink Fix·Mouth Fix" : "Mouth Fix") : "Blink Fix")
                        + "는 내보내기에서 제거했습니다. 기존 보정은 그대로 남아 있습니다.", "확인");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log("✕ " + e.Message); }
            finally { _busy = false; _go.IsEnabled = _close.IsEnabled = true; }
        }
    }
}
