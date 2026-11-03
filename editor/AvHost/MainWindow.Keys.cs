// 메인 창: 단축키 목록 (2026-10-27, 사용자 "소소한 불편 고치기") — Ctrl+/ · Ctrl+K "단축키 목록" · 찾기 단추 옆.
//   미리보기의 한 글자 키(G·M·H·P·E…)가 많은데 모아 볼 곳이 없었다. HandleShortcut·MeshEditKey와 같이 고친다.
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        static readonly (string Group, (string Keys, string What)[] Rows)[] KeyList =
        {
            ("파일", new[]
            {
                ("Ctrl+I", "Unity 씬·프리팹·패키지 가져오기"), ("Ctrl+Shift+A", "의상·헤어·악세서리 붙이기"),
                ("Ctrl+O · Ctrl+Shift+O", "가져온 파일 · 세션 열기"), ("Ctrl+S · Ctrl+Shift+S", "저장 · 다른 이름으로 저장"),
                ("Ctrl+Shift+1~9", "최근 파일 열기"), ("Ctrl+U · Ctrl+Shift+U", "VRChat에 올리기 · Unity 프로젝트로 내보내기"),
                ("Ctrl+Shift+P", "셰이더 팩 추가"),
            }),
            ("편집·이동", new[]
            {
                ("Ctrl+Z", "실행 취소"), ("Ctrl+Y · Ctrl+Shift+Z", "다시 실행"),
                ("Ctrl+1~8", "페이지 (아바타 · 머티리얼 · 블렌드쉐이프 · 자세 · 환경 · 스튜디오 · 최적화 · 올리기)"),
                ("Ctrl+K", "페이지·설정·명령 찾기"), ("Ctrl+,", "설정"), ("Ctrl+/", "이 단축키 목록"),
            }),
            ("미리보기 (글 입력 칸 밖에서)", new[]
            {
                ("1 · 2 · 3", "전신 · 상반신 · 얼굴"), ("F · B", "앞 · 뒤에서 보기"), ("5", "원근 ↔ 직교"),
                ("G", "바닥 격자"), ("M", "거울"), ("H", "아바타만 보기 (배경·소품 빼기)"),
                ("P", "조작 핸들 (자세 본)"), ("E", "메시 편집"), ("Alt+1~9", "저장한 카메라 위치"),
            }),
            ("자세 (조작 핸들이 켜져 있을 때)", new[]
            {
                ("R", "고른 본 되돌리기"), ("C", "반대쪽에 복사"), ("L", "핸들 방향: 월드 ↔ 로컬"),
            }),
            ("메시 편집 (E로 켰을 때)", new[]
            {
                ("클릭 · Shift+클릭 · 끌기", "정점 고르기 · 더하기 · 상자로 고르기"),
                ("A · L", "모두 고르기 · 이어진 정점 고르기"),
                ("X · Delete", "지우기"), ("M", "합치기"), ("F", "면 만들기"), ("N", "노멀 다시 계산"), ("B", "누르기 브러시 (휠로 반지름)"),
            }),
            ("표정·토글", new[]
            {
                ("F1~F9", "표정 단축키 (표정 단추를 오른쪽 클릭해 넣기)"), ("Shift+1~9", "내 토글 (아바타 페이지)"),
            }),
        };

        Window? _keysWindow;

        void ShowShortcuts()
        {
            if (_keysWindow != null) { _keysWindow.Activate(); return; }
            var p = new StackPanel { Margin = new Thickness(24, 20, 24, 20), Spacing = 4 };
            p.Children.Add(Ui.Text("단축키", 17, FontWeight.SemiBold));
            var note = Ui.Wrap("블렌드쉐이프 단축키는 블렌드쉐이프 페이지에서 직접 정해요.", Ui.KText2, 12);
            note.Margin = new Thickness(0, 0, 0, 6);
            p.Children.Add(note);
            foreach (var (group, rows) in KeyList)
            {
                var head = Ui.Text(group, 13, FontWeight.SemiBold, Ui.KAccentText);
                head.Margin = new Thickness(0, 10, 0, 2);
                p.Children.Add(head);
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
                int r = 0;
                foreach (var (keys, what) in rows)
                {
                    g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var k = Ui.Text(keys, 12, FontWeight.SemiBold); k.Margin = new Thickness(0, 3, 12, 3); k.TextWrapping = TextWrapping.Wrap;
                    var w = Ui.Wrap(what, Ui.KText2, 12); w.Margin = new Thickness(0, 3);
                    Grid.SetRow(k, r); Grid.SetRow(w, r); Grid.SetColumn(w, 1);
                    g.Children.Add(k); g.Children.Add(w);
                    r++;
                }
                p.Children.Add(g);
            }
            var win = _keysWindow = new Window
            {
                Title = "단축키", Width = 520, Height = 640, MinWidth = 380, MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                FontFamily = FontFamily, FontSize = 14,
                Content = new ScrollViewer { Content = p },
            };
            win.Bg(Ui.KCard);
            win.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) win.Close(); };
            win.Closed += (_, __) => _keysWindow = null;
            win.Show(this);
        }
    }
}
