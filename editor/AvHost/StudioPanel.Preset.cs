// 스튜디오 세팅 프리셋 (2026-11-01, Core.StudioPreset): 배경·바닥·소품·심도·화질·아바타만·촬영·보정·스티커·녹화·카메라 위치를 파일로 저장하고 불러온다.
//   모든 탭에 걸치는 값이라 탭 위에 둔다. 저장 폴더의 프리셋은 단추로 바로, 다른 곳의 파일은 "파일에서…"나 창에 끌어다 놓기로.
//   표정·트래킹·단축키·오버레이는 넣지 않는다 (아바타나 방송에 묶인 값). 조명은 환경 페이지 값이라 세션에 남는다.
//   스튜디오 값은 실행 취소가 없어서, 불러오면 바로 전 세팅으로 돌아가는 단추를 하나 보인다 (실행 동안만).
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class StudioPanel
    {
        Ui.FoldSection? _presetSection;
        readonly WrapPanel _presetList = new WrapPanel();
        readonly TextBlock _presetState = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        Button _presetUndo = null!;
        JNode? _beforePreset;

        static string PresetFolder => Path.Combine(Settings.Folder, "studio-presets");

        void BuildPresets()
        {
            var save = Ui.Button("저장…", SavePresetDialog, "지금 스튜디오 세팅을 파일로 저장해요", Glyph.Save);
            save.Margin = new Thickness(0);
            _presetSection = new Ui.FoldSection("세팅 프리셋", false, save, "배경·바닥·소품·심도·화질·촬영·보정·스티커·녹화·카메라 위치·조명을 한 파일로 저장해 두었다가 불러와요. 이미지와 소품은 파일 위치만 적어요. 조명은 \"조명도 가져오기\"를 켜 두면 함께 들어가요. 표정·트래킹·단축키·오버레이는 들어가지 않아요.");
            _presetSection.Margin = new Thickness(24, 0, 24, 6);   // 아래 탭과 줄을 맞춘다
            var open = Ui.Button("파일에서…", LoadPresetDialog, "다른 폴더의 프리셋 파일(.studio.json)을 불러와요. 창에 끌어다 놓아도 돼요", Glyph.Open);
            open.Margin = new Thickness(0, 0, 6, 0);
            _presetUndo = Ui.Button("불러오기 전으로", UndoPreset, "프리셋을 불러오기 바로 전 세팅으로 돌아가요", Glyph.Undo);
            _presetUndo.Margin = new Thickness(0);
            _presetUndo.IsVisible = false;
            _presetLight = new CheckBox { Content = "조명도 가져오기", IsChecked = _settings.StudioPresetLighting, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(_presetLight, "프리셋을 불러올 때 저장해 둔 조명(환경 페이지의 조명·추가 조명·라이트 볼륨)도 함께 넣어요. 끄면 지금 조명을 그대로 둬요");
            _presetLight.IsCheckedChanged += (_, __) => { _settings.StudioPresetLighting = _presetLight.IsChecked == true; _settings.Save(); };
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            tools.Children.Add(_presetLight); tools.Children.Add(open); tools.Children.Add(_presetUndo);
            _presetState.TextWrapping = TextWrapping.Wrap;
            _presetState.Margin = new Thickness(0, 6, 0, 0);
            _presetSection.Body.Children.Add(_presetList);
            _presetSection.Body.Children.Add(tools);
            _presetSection.Body.Children.Add(_presetState);
            RefreshPresets();
        }

        void RefreshPresets()
        {
            _presetList.Children.Clear();
            var list = StudioPreset.List(PresetFolder);
            if (_presetSection != null) _presetSection.Hint = list.Count == 0 ? "없음" : $"{list.Count}개";
            if (list.Count == 0) { _presetList.Children.Add(Ui.Wrap("저장한 프리셋이 없어요. 위 \"저장…\"으로 지금 세팅을 저장해요.")); return; }
            foreach (var (name, path) in list)
            {
                var p = path; var n = name;
                var b = Ui.Button(n, () => LoadPreset(p), "누르면 불러와요 · 오른쪽 클릭: 덮어쓰기·지우기");
                var menu = new MenuFlyout();
                var over = new MenuItem { Header = "지금 세팅으로 덮어쓰기" };
                over.Click += (_, __) => SavePreset(p);
                var del = new MenuItem { Header = "지우기" };
                del.Click += (_, __) =>
                {
                    try { File.Delete(p); _presetState.Text = $"프리셋 {n}을(를) 지웠어요."; }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { _presetState.Text = "삭제하지 못했습니다: " + e.Message; }
                    RefreshPresets();
                };
                menu.Items.Add(over); menu.Items.Add(del);
                b.ContextFlyout = menu;
                _presetList.Children.Add(b);
            }
        }

        // ---------------- 값 모으기 ----------------
        /// <summary>지금 스튜디오 세팅 (ApplyStudio가 받는 모양).</summary>
        internal JNode StudioValues()
        {
            float[] Rgb(string hex, float[] fallback) => Hex(hex) ?? fallback;
            var gray = new[] { 0.5f, 0.5f, 0.5f };
            // 단색은 위·아래가 같은 그라데이션으로 적는다 (Shoot·예전 프리셋도 읽게, 2026-11-03)
            bool solid = _settings.StudioBgMode == "solid";
            var bg = JNode.Obj().Set("mode", solid ? "gradient" : _settings.StudioBgMode)
                .Set("top", JNode.FloatArray(Rgb(solid ? _settings.StudioBgColor : _settings.StudioBgTop, gray))).Set("bottom", JNode.FloatArray(Rgb(solid ? _settings.StudioBgColor : _settings.StudioBgBottom, gray)));
            if (_settings.StudioBgPath.Length > 0) bg.Set("path", _settings.StudioBgPath);
            return JNode.Obj()
                .Set("background", bg).Set("exposure", _settings.StudioExposure).Set("rotation", _settings.StudioRotation)
                .Set("floor", JNode.Obj().Set("enabled", _settings.StudioFloor != "none").Set("mode", _settings.StudioFloor).Set("strength", _settings.ShotShadowStrength).Set("color", JNode.FloatArray(Rgb(_settings.StudioFloorColor, gray))))
                .Set("props", _settings.StudioProps.Clone())
                .Set("dof", _settings.StudioDof.Clone())
                .Set("quality", JNode.Obj().Set("studio", _settings.StudioQuality).Set("ao", _settings.StudioAo).Set("aa", _settings.StudioCaptureAa))
                .Set("avatarOnly", JNode.Obj().Set("on", _settings.ViewAvatarOnly).Set("color", JNode.FloatArray(Rgb(_settings.AvatarOnlyColor, gray))))
                .Set("transparent", _settings.ShotTransparent)
                .Set("adjust", JNode.Obj().Set("brightness", _settings.ShotBrightness).Set("contrast", _settings.ShotContrast).Set("saturation", _settings.ShotSaturation))
                .Set("shot", JNode.Obj().Set("width", _settings.ShotWidth).Set("height", _settings.ShotHeight).Set("embed", _settings.ShotEmbedSession))
                .Set("sticker", JNode.Obj().Set("spec", _settings.StickerSpec).Set("color", _settings.StickerOutlineColor).Set("outline", _settings.StickerOutline))
                .Set("record", _settings.StudioRecord.Clone())
                .Set("cameras", _settings.Cameras.Clone())
                .Set("lighting", LightingValues());
        }

        // ---------------- 조명 (2026-11-12) ----------------
        // 사용자: 프리셋을 불러와도 조명이 그대로라 찍던 분위기가 안 돌아왔다. 조명은 환경 페이지 값(세션)이지만 프리셋에도 적어 두고,
        //   불러올 때 "조명도 가져오기"(기본 켜짐)면 환경의 조명 항목만 바꿔 끼운다 (후처리·AudioLink 같은 다른 효과는 그대로). 실행 취소도 된다.
        static readonly string[] LightingProviders = { "UnityLighting", "Lights", "VRCLightVolumes" };
        CheckBox _presetLight = null!;

        JNode CurrentEnvironment() => _ctx.Doc?.Get(Addr.Environment) ?? Document.DefaultEnvironment();

        /// 지금 환경에서 조명 항목만 (없는 항목은 적지 않는다 = 기본값)
        JNode LightingValues()
        {
            var list = JNode.Arr();
            foreach (var e in CurrentEnvironment().Items)
                if (Array.IndexOf(LightingProviders, e["provider"].AsString) >= 0) list.Add(e.Clone());
            return list;
        }

        /// 프리셋의 조명을 환경에 넣는다. 프리셋에 적힌 조명 항목은 바꾸고, 적히지 않은 조명 항목은 기본값으로 (예전 프리셋에는 lighting이 없어 그대로 둔다)
        bool ApplyLighting(JNode j)
        {
            if (j["lighting"].Kind != JKind.Array) return false;
            var list = JNode.Arr();
            foreach (var e in CurrentEnvironment().Items)
                if (Array.IndexOf(LightingProviders, e["provider"].AsString) < 0) list.Add(e.Clone());
            foreach (var e in j["lighting"].Items)
                if (Array.IndexOf(LightingProviders, e["provider"].AsString) >= 0) list.Add(e.Clone());
            if (list.ToJson() == CurrentEnvironment().ToJson()) return true;
            if (_ctx.Doc != null) _ctx.Doc.Set(Addr.Environment, list, "스튜디오 프리셋 조명");
            else _ctx.Render?.Send(Msg.Make(Msg.SetEnvironment).Set("providers", list));
            return true;
        }

        // ---------------- 저장·불러오기 ----------------
        internal void SavePreset(string path)
        {
            path = StudioPreset.WithExtension(path);
            try
            {
                StudioPreset.Save(path, StudioValues());
                _presetState.Text = $"저장했어요 → {path}";
                _ctx.Status($"스튜디오 세팅을 저장했어요 · {StudioPreset.NameOf(path)}");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { _presetState.Text = "저장하지 못했습니다: " + e.Message; _ctx.Status(_presetState.Text); }
            RefreshPresets();
        }

        internal void LoadPreset(string path)
        {
            if (_job != null) { _ctx.Status("녹화가 끝난 뒤 불러와 주세요."); return; }
            JNode j;
            try { j = StudioPreset.Load(path); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { _presetState.Text = e.Message; _ctx.Status(e.Message); return; }
            _beforePreset = StudioValues();
            ApplyStudio(j);
            bool light = _settings.StudioPresetLighting && ApplyLighting(j);
            if (!_settings.StudioPresetLighting) _beforePreset.Remove("lighting");   // 넣지 않은 조명은 되돌리지도 않는다
            _presetUndo.IsVisible = true;
            if (_presetSection != null) _presetSection.IsOpen = true;
            var missing = StudioPreset.MissingFiles(j);
            _presetState.Text = $"불러왔습니다 · {StudioPreset.NameOf(path)}" + (light ? " · 조명 포함" : "") + (missing.Count > 0 ? $"\n찾지 못한 파일 {missing.Count}개: " + string.Join(", ", missing.Take(3).Select(Path.GetFileName)) : "");
            _ctx.Status($"스튜디오 설정을 불러왔습니다 · {StudioPreset.NameOf(path)}" + (missing.Count > 0 ? $" (찾지 못한 파일 {missing.Count}개)" : ""));
        }

        internal void UndoPreset()
        {
            if (_beforePreset == null) return;
            ApplyStudio(_beforePreset);
            ApplyLighting(_beforePreset);
            _beforePreset = null;
            _presetUndo.IsVisible = false;
            _presetState.Text = "불러오기 전 세팅으로 돌아왔어요.";
            _ctx.Status(_presetState.Text);
        }

        async void SavePresetDialog()
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            Directory.CreateDirectory(PresetFolder);
            var file = await w.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Texts.T("스튜디오 세팅 저장"), SuggestedFileName = "스튜디오 설정" + StudioPreset.Extension, DefaultExtension = "studio.json",
                SuggestedStartLocation = await w.StorageProvider.TryGetFolderFromPathAsync(PresetFolder),
                FileTypeChoices = new[] { new FilePickerFileType("스튜디오 프리셋") { Patterns = new[] { "*" + StudioPreset.Extension } } },
            });
            if (file?.TryGetLocalPath() is string path) SavePreset(path);
        }

        async void LoadPresetDialog()
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            Directory.CreateDirectory(PresetFolder);
            var files = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("스튜디오 프리셋 불러오기"),
                SuggestedStartLocation = await w.StorageProvider.TryGetFolderFromPathAsync(PresetFolder),
                FileTypeFilter = new[] { new FilePickerFileType("스튜디오 프리셋") { Patterns = new[] { "*" + StudioPreset.Extension } } },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string path) LoadPreset(path);
        }
    }
}
