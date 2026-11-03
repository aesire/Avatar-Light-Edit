// "VR 사진" 떠 있는 창 (PLAN 3.12 "VR 갤러리", 2026-10-27): VR 카메라 폴더의 최근 사진을 작은 그림으로 모아 보고, 누르면 크게.
//   떠 있는 창이라 VR에서도 오버레이로 뜬다 (따로 VR 패널을 만들지 않음). 그림은 줄여서 읽는다 (4K PNG도 가볍게).
using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VrcProto.AvHost
{
    public sealed class VrGalleryPanel : StackPanel
    {
        const int Max = 24, Thumb = 160;
        readonly Func<string> _folder;
        readonly Action<string> _status;
        readonly Image _big = new Image { Stretch = Stretch.Uniform, MaxHeight = 300, IsVisible = false };
        readonly TextBlock _bigName = Ui.Text("", 12, FontWeight.Normal, Ui.KText2);
        readonly WrapPanel _grid = new WrapPanel();
        readonly TextBlock _empty = Ui.Wrap("아직 VR 카메라로 찍은 사진이 없어요.");
        readonly StackPanel _shownBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
        string _shown = "";

        /// <param name="useThumb">크게 본 사진을 올리기 썸네일로 고름 (2026-11-01)</param>
        /// <param name="makeSticker">크게 본 사진으로 스티커 만들기 (2026-11-01)</param>
        public VrGalleryPanel(Func<string> folder, Action openFolder, Action<string> status, Action<string> useThumb, Action<string> makeSticker)
        {
            _folder = folder; _status = status;
            _shownBar.Children.Add(Ui.Button("올리기 썸네일로", () => useThumb(_shown), "지금 아바타를 올릴 때 이 사진을 썸네일로 써요. 1200×900 비율에 맞게 가운데를 잘라요"));
            _shownBar.Children.Add(Ui.Button("스티커로", () => makeSticker(_shown), "투명 배경으로 찍은 사진을 스튜디오 스티커 규격·테두리대로 만들어 같은 폴더에 저장해요"));
            Spacing = 8; Margin = new Thickness(14);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            bar.Children.Add(Ui.Button("새로 고침", Refresh));
            bar.Children.Add(Ui.Button("폴더 열기", openFolder));
            Children.Add(bar);
            Children.Add(_big); Children.Add(_bigName); Children.Add(_shownBar);
            Children.Add(_empty);
            Children.Add(_grid);
        }

        public void Refresh()
        {
            _grid.Children.Clear();
            var dir = _folder();
            var files = Directory.Exists(dir)
                ? new DirectoryInfo(dir).GetFiles("VR_*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(Max).ToList()
                : new System.Collections.Generic.List<FileInfo>();
            _empty.IsVisible = files.Count == 0;
            foreach (var f in files)
            {
                Bitmap? bmp = null;
                try { using var s = File.OpenRead(f.FullName); bmp = Bitmap.DecodeToWidth(s, Thumb); }
                catch { continue; }
                var path = f.FullName;
                var cell = new Border { Child = new Image { Source = bmp, Width = Thumb, Stretch = Stretch.Uniform }, Margin = new Thickness(0, 0, 6, 6), Cursor = new Cursor(StandardCursorType.Hand) };
                cell.PointerPressed += (_, __) => Show(path);
                ToolTip.SetTip(cell, f.Name);
                _grid.Children.Add(cell);
            }
        }

        public void Show(string path)
        {
            try
            {
                using var s = File.OpenRead(path);
                _big.Source = Bitmap.DecodeToWidth(s, 900);
                _big.IsVisible = true;
                _bigName.Text = Path.GetFileName(path);
                _shown = path; _shownBar.IsVisible = true;
            }
            catch (Exception e) { _status("사진을 열지 못했습니다: " + e.Message); }
        }
    }
}
