// "셰이더 팩 추가" 창: 셰이더가 든 .unitypackage 나 폴더를 고르면 안의 셰이더를 보여 주고, Unity로 팩을 만든다 (ShaderPacks).
// 끝나면 true 를 돌려주고, 메인 창이 미리보기를 다시 시작해 팩을 불러온다.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    public sealed class ShaderPackWindow : Window
    {
        readonly IrScene? _scene;
        readonly string _unityVersion;
        readonly TextBlock _state = Ui.Wrap("", Ui.KText2, 13);
        readonly TextBlock _found = Ui.Wrap("", Ui.KText, 13);
        readonly TextBox _name = new TextBox { Watermark = "팩 이름" };
        readonly CheckBox _useAvatar = new CheckBox { Content = "열린 아바타의 머티리얼로 필요한 기능(키워드)까지 넣기", IsChecked = true };
        readonly ProgressBar _progress = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4, IsVisible = false };
        readonly Button _build, _close;
        ShaderPacks.Source? _src;
        bool _busy;

        public ShaderPackWindow(IrScene? scene, string unityVersion)
        {
            _scene = scene; _unityVersion = unityVersion;
            Title = "셰이더 팩 추가";
            Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            FontFamily = Ui.Font; FontSize = 14;
            this.Bg(Ui.KCard);

            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 6 };
            p.Children.Add(Ui.DialogHeader("셰이더 팩 추가", "미리보기에 없는 셰이더(MToon, Poiyomi 등)를 더해요.",
                "셰이더가 든 .unitypackage 나 폴더를 선택하면 이 PC의 Unity " + unityVersion + "로 컴파일해 팩으로 만듭니다. 셰이더 파일만 꺼내 사용하고 스크립트는 실행하지 않습니다."));
            var pick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
            pick.Children.Add(Ui.Button("패키지 고르기", PickPackage, glyph: Glyph.Open));
            pick.Children.Add(Ui.Button("폴더 고르기", PickFolder, glyph: Glyph.Folder));
            p.Children.Add(pick);
            p.Children.Add(_found);
            p.Children.Add(_name);
            p.Children.Add(_useAvatar);
            p.Children.Add(_progress);
            p.Children.Add(_state);
            var installed = ShaderPacks.Installed();
            var inst = Ui.Wrap(installed.Count == 0 ? "설치된 팩 없음" : "설치된 팩: " + string.Join(", ", installed), Ui.KText3, 12);
            p.Children.Add(inst);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Spacing = 8 };
            var folder = Ui.Button("팩 폴더 열기", () =>
            {
                Directory.CreateDirectory(ShaderPacks.PackDir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", ShaderPacks.PackDir) { UseShellExecute = true });
            });
            _build = Ui.Button("팩 만들기", Build, accent: true);
            _close = Ui.Button("닫기", () => Close(false));
            foreach (var b in new[] { folder, _build, _close }) { b.Margin = new Thickness(0); b.MinWidth = 88; b.HorizontalContentAlignment = HorizontalAlignment.Center; }
            _close.IsCancel = true;
            row.Children.Add(folder); row.Children.Add(_build); row.Children.Add(_close);
            p.Children.Add(row);
            Content = p;
            _name.IsVisible = _useAvatar.IsVisible = false;
            Closing += (_, e) => { if (_busy) e.Cancel = true; };   // Unity가 팩을 쓰는 중
            Refresh();
        }

        async void PickPackage()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("셰이더가 든 패키지"),
                FileTypeFilter = new[] { new FilePickerFileType("Unity 패키지") { Patterns = new[] { "*.unitypackage" } }, new FilePickerFileType("셰이더") { Patterns = new[] { "*.shader" } } },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path) await Scan(path);
        }

        async void PickFolder()
        {
            var dirs = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Texts.T("셰이더가 든 폴더") });
            if (dirs.Count > 0 && dirs[0].TryGetLocalPath() is string path) await Scan(path);
        }

        public async Task Scan(string path)
        {
            SetBusy(true, "셰이더를 찾는 중…");
            try { _src = await Task.Run(() => ShaderPacks.Scan(path)); }
            catch (Exception e) { _src = null; SetBusy(false, e.Message, error: true); return; }
            if (_src.Shaders.Count == 0) { _src = null; SetBusy(false, "셰이더(.shader)를 찾지 못했어요.", error: true); return; }
            var used = _scene == null ? 0 : _scene.Materials.Count(m => _src.Shaders.Any(s => s.Guid == m.ShaderGuid));
            _found.Text = $"셰이더 {_src.Shaders.Count}개 (파일 {_src.Files.Count}개, 원본 머티리얼 {_src.Materials.Count}개)"
                + (used > 0 ? $" · 열린 아바타에서 {used}개 머티리얼이 이 셰이더를 사용합니다" : "") + "\n"
                + string.Join("\n", _src.Shaders.Take(8).Select(s => "  · " + s.Name)) + (_src.Shaders.Count > 8 ? $"\n  … 외 {_src.Shaders.Count - 8}개" : "");
            _name.Text = Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
            _name.IsVisible = true;
            _useAvatar.IsVisible = _scene != null;
            SetBusy(false, "팩 이름을 확인하고 \"팩 만들기\"를 누르세요. 같은 이름의 팩은 새로 만든 것으로 바뀌어요.");
        }

        async void Build()
        {
            if (_src == null || _busy) return;
            var unity = ShaderPacks.FindUnity(_unityVersion);
            if (unity == null) { SetBusy(false, $"Unity {_unityVersion}를 찾지 못했어요. Unity Hub로 설치하거나 환경 변수 UNITY_EXE에 Unity.exe 경로를 지정하세요.", error: true); return; }
            var name = string.IsNullOrWhiteSpace(_name.Text) ? "pack" : _name.Text!.Trim();
            var extra = _useAvatar.IsChecked == true ? ShaderPacks.AvatarMaterials(_scene, _src) : new System.Collections.Generic.List<(string, string)>();
            SetBusy(true, "준비 중…");
            ShaderPacks.Result r;
            try { r = await ShaderPacks.Build(_src, name, extra, unity, s => Avalonia.Threading.Dispatcher.UIThread.Post(() => _state.Text = s)); }
            catch (Exception e) { SetBusy(false, "팩을 만들지 못했습니다: " + e.Message, error: true); return; }
            if (!r.Ok) { SetBusy(false, "팩을 만들지 못했습니다.\n" + string.Join("\n", r.Errors.Take(5)), error: true); return; }
            _busy = false;
            await Dialog.Ask(this, "셰이더 팩을 만들었어요", $"셰이더 {r.Compiled.Count}개" + (r.Errors.Count > 0 ? $", 컴파일 실패 {r.Errors.Count}개:\n" + string.Join("\n", r.Errors.Take(5)) : "")
                + "\n미리보기를 다시 시작해 팩을 불러옵니다.", "확인");
            Close(true);
        }

        void SetBusy(bool busy, string text, bool error = false)
        {
            _busy = busy;
            _progress.IsVisible = busy;
            _state.Text = text;
            _state.Fg(error ? Ui.KCritical : Ui.KText2);
            Refresh();
        }

        void Refresh()
        {
            _build.IsEnabled = !_busy && _src != null;
            _close.IsEnabled = !_busy;
        }
    }
}
