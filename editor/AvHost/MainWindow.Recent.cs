// 최근 파일 (2026-10-27 동선 줄이기 1번): 연·저장한 IR·세션을 새것부터 기억해 시작 화면·열기 메뉴·Ctrl+K에서 한 번에 다시 연다.
// 예전 설정(LastFile 하나)만 있으면 그것으로 목록을 시작한다. 없어진 파일은 보여 주지 않는다 (목록에서 지우지는 않음 — 외장 디스크일 수 있다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using VrcProto.Host.Core;
using VrcProto.Host.Shared;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        readonly StackPanel _recentBox = new StackPanel { Spacing = 2 };

        void RememberRecent(string full)
        {
            var list = _settings.RecentFiles;
            if (list.Count == 0 && _settings.LastFile.Length > 0 && !string.Equals(_settings.LastFile, full, StringComparison.OrdinalIgnoreCase)) list.Add(_settings.LastFile);
            if (list.Count > 0 && string.Equals(list[0], full, StringComparison.OrdinalIgnoreCase)) return;
            list.RemoveAll(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, full);
            if (list.Count > Settings.RecentMax) list.RemoveRange(Settings.RecentMax, list.Count - Settings.RecentMax);
            RefreshRecent();
        }

        /// <summary>지금 있는 최근 파일 (새것 먼저)</summary>
        List<string> RecentFiles()
        {
            IEnumerable<string> src = _settings.RecentFiles.Count > 0 ? _settings.RecentFiles : new[] { _settings.LastFile };
            return src.Where(f => f.Length > 0 && File.Exists(f)).ToList();
        }

        static bool IsSessionFile(string f) => Session.IsSessionFile(f);

        /// <summary>목록에 보일 이름: 파일 이름 + 흐린 폴더</summary>
        static (string Name, string Folder) RecentLabel(string f)
        {
            var name = Path.GetFileName(f);
            if (name.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ".ir.json".Length);
            return (name, Path.GetDirectoryName(f) ?? "");
        }

        /// <summary>시작 화면의 "최근 파일" 칸을 다시 채운다</summary>
        void RefreshRecent()
        {
            _recentBox.Children.Clear();
            var files = RecentFiles();
            _recentBox.IsVisible = files.Count > 0;
            if (files.Count == 0) return;
            var head = Ui.Text("최근 파일", 12, Avalonia.Media.FontWeight.SemiBold, Ui.KText2);
            head.Margin = new Thickness(2, 18, 0, 4);
            _recentBox.Children.Add(head);
            foreach (var f in files)
            {
                var (name, folder) = RecentLabel(f);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(Ui.Icon(IsSessionFile(f) ? Glyph.Folder : Glyph.Open, 13, Ui.KText2));
                var text = new StackPanel { Spacing = 0 };
                text.Children.Add(Ui.Text(name, 13));
                var sub = Ui.Text(folder, 11, Avalonia.Media.FontWeight.Normal, Ui.KText3);
                sub.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis; sub.MaxWidth = 300;
                text.Children.Add(sub);
                row.Children.Add(text);
                var path = f;
                var b = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 5), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
                b.Click += (_, __) => OpenPath(path);
                ToolTip.SetTip(b, path);
                _recentBox.Children.Add(b);
            }
        }

        /// <summary>열기 메뉴의 "최근 파일" 하위 메뉴: 열 때마다 다시 채운다</summary>
        void FillRecentMenu(MenuItem sub)
        {
            sub.Items.Clear();
            var files = RecentFiles();
            sub.IsEnabled = files.Count > 0;
            for (int i = 0; i < files.Count; i++)
            {
                var path = files[i];
                var (name, folder) = RecentLabel(path);
                var mi = new MenuItem { Header = $"{name}   —   {folder}" };
                if (i < 9) mi.InputGesture = Avalonia.Input.KeyGesture.Parse($"Ctrl+Shift+D{i + 1}");
                mi.Click += (_, __) => OpenPath(path);
                sub.Items.Add(mi);
            }
        }

        /// <summary>Ctrl+Shift+숫자: 최근 파일 n번째 열기</summary>
        bool OpenRecentAt(int index)
        {
            var files = RecentFiles();
            if (index < 0 || index >= files.Count) { SetStatus($"최근 파일 {index + 1}번이 없어요."); return true; }
            OpenPath(files[index]);
            return true;
        }
    }
}
