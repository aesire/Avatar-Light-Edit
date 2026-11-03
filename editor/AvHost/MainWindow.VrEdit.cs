// 메인 창: VR 남은 단계 (2026-10-27 사용자 "VR모드 남은 작업 해치우자")
//   6단계 아이템 잡아 배치 — 켜고 끄기만 여기서 (잡기·놓기는 렌더러 VrItemGrab, 다른 뼈에 놓으면 OnVrItemPlaced가 다시 만든다).
//   8단계 카메라 남은 것 — 심도·투명 배경·스튜디오 보정·자리 저장 3칸·VR 사진 모아 보기(VrGalleryPanel).
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        VrGalleryPanel? _vrGallery;

        // ---- 아이템 잡기 ----
        void SetVrItemGrab(bool on)
        {
            _settings.VrItemGrab = on; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("itemGrab", on));
            SetStatus(on ? "아바타 밖에서 붙인 악세서리를 그립으로 잡아 옮겨요. 잡은 손 스틱 좌우 = 붙일 뼈 바꾸기, 다른 손 그립 = 크기." : "악세서리 잡기를 껐어요.");
        }

        // ---- 카메라 남은 것 ----
        void SetVrCameraDof(double v) { _settings.VrCameraDof = v; _settings.Save(); SendVrCamera(); SetStatus(v > 0 ? $"VR 카메라 심도 {v * 100:0}% (초점은 아바타 머리)." : "VR 카메라 심도를 껐어요."); }
        void SetVrCameraTransparent(bool on) { _settings.VrCameraTransparent = on; _settings.Save(); SendVrCamera(); SetStatus(on ? "VR 카메라: 투명 배경으로 찍어요 (아바타만, 블룸은 빠져요)." : "VR 카메라: 배경까지 찍어요."); }
        void SetVrCameraAdjust(bool on) { _settings.VrCameraAdjust = on; _settings.Save(); SetStatus(on ? "VR 사진에 스튜디오 보정(밝기·대비·채도)을 넣어요." : "VR 사진 보정을 껐어요."); }

        void VrCameraMark(int slot, bool save)
        {
            if (save) { _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraOpts", JNode.Obj().Set("bookmark", slot))); return; }
            var marks = _settings.VrCameraMarks;
            if (slot >= marks.Count || marks[slot].Kind != JKind.Object) { SetStatus($"카메라 자리 {slot + 1}에 저장한 것이 없어요."); return; }
            if (!_settings.VrCamera) SetVrCamera(true);
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("cameraGoto", marks[slot].Clone()));
            SetStatus($"VR 카메라를 자리 {slot + 1}로 옮겼어요.");
        }

        void OnVrCameraBookmark(JNode m)
        {
            int slot = Math.Clamp(m["slot"].AsInt, 0, 2);
            var marks = _settings.VrCameraMarks;
            while (marks.Count <= slot) marks.Add(JNode.Null);
            var arr = JNode.Arr();
            for (int i = 0; i < marks.Count; i++) arr.Add(i == slot ? JNode.Obj().Set("pos", m["pos"].Clone()).Set("rot", m["rot"].Clone()) : marks[i].Clone());
            _settings.VrCameraMarks = arr; _settings.Save();
            SetStatus($"VR 카메라 자리 {slot + 1}에 저장했어요.");
        }

        /// <summary>찍은 VR 사진에 스튜디오 보정을 넣는다 (켰고 값이 그대로가 아닐 때). 끝나면 모아 보기를 새로.</summary>
        void AdjustVrShot(string path)
        {
            double b = _settings.ShotBrightness, c = _settings.ShotContrast, s = _settings.ShotSaturation;
            if (!_settings.VrCameraAdjust || Sticker.IsNeutral(b, c, s)) { _vrGallery?.Refresh(); return; }
            _ = Task.Run(() =>
            {
                try
                {
                    var img = Sticker.DecodePng(File.ReadAllBytes(path));
                    if (img != null) { Sticker.Adjust(img, b, c, s); File.WriteAllBytes(path, Sticker.EncodePng(img)); }
                }
                catch (Exception e) { Dispatcher.UIThread.Post(() => AppendLog("warn", "VR 사진 보정 실패: " + e.Message)); }
                Dispatcher.UIThread.Post(() => _vrGallery?.Refresh());
            });
        }

        void OpenVrGallery()
        {
            _vrGallery ??= new VrGalleryPanel(() => VrCameraFolder, OpenVrCameraFolder, SetStatus, SetUploadThumb, MakeVrSticker);
            _vrGallery.Refresh();
            var f = _floats.Show("vrgallery", null, "VR 사진", _vrGallery, canDock: false, new Avalonia.Size(560, 640));
            f.DockRequested -= CloseVrGallery; f.DockRequested += CloseVrGallery;
        }

        // ---- VR 사진 → 스티커 (2026-11-01): 규격·테두리·보정은 스튜디오 "스티커 만들기" 설정, 파일은 사진 폴더에 (이름이 VR_로 시작하지 않아 모아 보기에 섞이지 않음) ----
        void MakeVrSticker(string path)
        {
            var s = _settings;
            var opt = new Sticker.Options
            {
                Spec = s.StickerSpec, OutlineWidth = s.StickerOutlineColor == "none" ? 0 : s.StickerOutline,
                OutlineColor = s.StickerOutlineColor == "black" ? new byte[] { 0, 0, 0 } : new byte[] { 255, 255, 255 },
                Brightness = s.ShotBrightness, Contrast = s.ShotContrast, Saturation = s.ShotSaturation,
            };
            SetStatus("스티커를 만드는 중…");
            _ = Task.Run(() =>
            {
                try
                {
                    var img = Sticker.DecodePng(File.ReadAllBytes(path));
                    if (img == null) return ("사진을 읽지 못했어요.", (string?)null);
                    bool clear = false;
                    for (int i = 3; i < img.Px.Length && !clear; i += 4) clear = img.Px[i] < 8;
                    if (!clear) return ("배경이 있는 사진이에요. VR 메뉴 \"VR 카메라 더 보기\"에서 투명 배경 사진을 켜고 찍어 주세요.", null);
                    var list = Sticker.Make(img, opt);
                    if (list.Count == 0) return ("사진에 아바타가 보이지 않아요.", null);
                    string? first = null;
                    var stem = Path.Combine(Path.GetDirectoryName(path) ?? "", "Sticker_" + Path.GetFileNameWithoutExtension(path) + "_" + opt.Spec);
                    foreach (var (size, one) in list)
                    {
                        var p = list.Count == 1 ? stem + ".png" : stem + "_" + size + ".png";
                        File.WriteAllBytes(p, Sticker.EncodePng(one));
                        first ??= p;
                    }
                    return ($"스티커를 만들었습니다: {Path.GetFileName(first)}" + (list.Count > 1 ? $" 외 {list.Count - 1}장" : ""), first);
                }
                catch (Exception e) { return ("스티커를 만들지 못했습니다: " + e.Message, null); }
            }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                var (text, first) = t.Result;
                SetStatus(text);
                if (first != null) ShowSaved(first);
            }));
        }

        // ---- 올리기 썸네일로 고른 사진 (아바타마다, 2026-11-01) ----
        void SetUploadThumb(string path)
        {
            if (_ctx.Doc == null) { SetStatus("아바타를 연 뒤에 골라 주세요."); return; }
            _settings.UploadThumbs.Set(Upload.AvatarKey(_ctx.Doc), path); _settings.Save();
            SetStatus(path.Length > 0 ? $"올리기 썸네일로 골랐어요: {Path.GetFileName(path)}" : "고른 썸네일을 풀었어요.");
        }

        void CloseVrGallery()
        {
            var f = _floats.Find("vrgallery");
            if (f != null) { f.Release(); _floats.Close(f); }
        }

        /// <summary>VR 메뉴의 배치 항목과 카메라 추가 항목.</summary>
        void AddVrEditMenu()
        {
            _vrMenu.Items.Add(new Separator());
            var items = new MenuItem { Header = "악세서리 잡아 옮기기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrItemGrab };
            ToolTip.SetTip(items, "아바타 밖에서 붙인 악세서리 범위에 손을 넣고 그립 — 놓으면 가까운 뼈에 붙어요 (잡은 손 스틱 좌우 = 다른 뼈, 다른 손 그립 = 크기)");
            items.Click += (_, __) => SetVrItemGrab(!_settings.VrItemGrab);
            _vrMenu.Items.Add(items);
            void ItemOpt(string label, string tip, string key, Func<bool> get, Action<bool> set)
            {
                var it = new MenuItem { Header = label, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
                ToolTip.SetTip(it, tip);
                it.Click += (_, __) => { set(!get()); _settings.Save(); _ctx.Render?.Send(Msg.Make(Msg.Vr).Set(key, get())); };
                _vrMenu.Items.Add(it);
            }
            ItemOpt("악세서리 스냅 (1 cm·15°)", "잡아 옮길 때 붙은 뼈 기준으로 1 cm, 15° 단위로 맞춰요. 스튜디오 소품도 같아요", "itemSnap", () => _settings.VrItemSnap, v => _settings.VrItemSnap = v);
            ItemOpt("놓을 때 표면에 붙이기", "놓는 자리 12 cm 안에 몸·옷 표면이 있으면 그 면에 닿게 맞춰요", "itemSurface", () => _settings.VrItemSurface, v => _settings.VrItemSurface = v);
            ItemOpt("짝 아이템 좌우 대칭", "이름이 짝인 아이템(_L·_R, Left·Right, 左·右, 왼쪽·오른쪽)이 있으면 반대쪽 거울 자리로 같이 옮겨요. 아이템을 복제하지는 않아요", "itemMirror", () => _settings.VrItemMirror, v => _settings.VrItemMirror = v);

            var more = new MenuItem { Header = "VR 카메라 더 보기" };
            var dof = new MenuItem { Header = "피사계 심도" };
            foreach (var v in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                var val = v;
                var it = new MenuItem { Header = v == 0 ? "끔" : $"{v * 100:0}%", ToggleType = MenuItemToggleType.Radio, IsChecked = Math.Abs(_settings.VrCameraDof - v) < 0.01 };
                it.Click += (_, __) => SetVrCameraDof(val);
                dof.Items.Add(it);
            }
            more.Items.Add(dof);
            var clear = new MenuItem { Header = "투명 배경 사진", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCameraTransparent };
            clear.Click += (_, __) => SetVrCameraTransparent(!_settings.VrCameraTransparent);
            more.Items.Add(clear);
            var adj = new MenuItem { Header = "스튜디오 보정 넣기 (밝기·대비·채도)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCameraAdjust };
            ToolTip.SetTip(adj, "스튜디오 \"촬영\"의 보정 값을 VR 사진에도 써요");
            adj.Click += (_, __) => SetVrCameraAdjust(!_settings.VrCameraAdjust);
            more.Items.Add(adj);
            var mic = new MenuItem { Header = "영상에 마이크 소리 넣기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrCameraMic };
            ToolTip.SetTip(mic, "말하기에서 고른 마이크로 녹음해 MP4에 넣어요. 녹화하는 동안에만 듣고, 다른 곳에 저장하거나 보내지 않아요");
            mic.Click += (_, __) => VrCameraAct("mic");
            more.Items.Add(mic);
            for (int i = 0; i < 3; i++)
            {
                int slot = i;
                var save = new MenuItem { Header = $"카메라 자리 {i + 1} 저장" };
                save.Click += (_, __) => VrCameraMark(slot, true);
                var go = new MenuItem { Header = $"카메라 자리 {i + 1}로", IsEnabled = slot < _settings.VrCameraMarks.Count && _settings.VrCameraMarks[slot].Kind == JKind.Object };
                go.Click += (_, __) => VrCameraMark(slot, false);
                more.Items.Add(save); more.Items.Add(go);
            }
            var gal = new MenuItem { Header = "VR 사진 모아 보기" };
            gal.Click += (_, __) => OpenVrGallery();
            more.Items.Add(gal);
            _vrMenu.Items.Add(more);
        }
    }
}
