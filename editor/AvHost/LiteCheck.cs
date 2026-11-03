// 가벼운 체크 상자 (2026-10-27, 불러오기 속도): 깜빡임·입 맞추기 탭에 체크 상자가 200개 넘게 있어(큰 시험 장면 — 입 블렌드쉐이프 146개)
//   Fluent CheckBox 템플릿(요소 9개)과 상태 스타일 수십 개를 입히고 배치하느라 그 탭 예열만 0.4~0.7초였다.
//   CheckBox를 그대로 물려받아 쓰는 쪽은 같고(IsChecked·IsCheckedChanged·Content), 템플릿은 상자·체크·글 셋, 색은 Fluent와 같은 자원 이름을 상태마다 직접 칠한다.
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace VrcProto.AvHost
{
    public sealed class LiteCheck : CheckBox
    {
        // 겉모양 테마 없이 템플릿만 (Fluent의 CheckBox 테마·스타일은 형식이 같을 때만 붙는다)
        protected override Type StyleKeyOverride => typeof(LiteCheck);

        static readonly FuncControlTemplate<LiteCheck> Look = new FuncControlTemplate<LiteCheck>((c, scope) =>
        {
            // Fluent와 같은 자리: 높이 32 띠의 가운데에 20×20 상자, 글은 그 오른쪽 8px
            var box = new Border { Name = "Box", Width = 20, Height = 20, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), UseLayoutRounding = false };
            var glyph = new Avalonia.Controls.Shapes.Path { Name = "Glyph", Stretch = Stretch.Uniform, Width = 11.25, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            box.Child = glyph;
            var text = new ContentPresenter { Name = "PART_ContentPresenter", VerticalAlignment = VerticalAlignment.Center };
            text.Bind(ContentPresenter.ContentProperty, c.GetObservable(ContentProperty));
            text.Bind(ContentPresenter.PaddingProperty, c.GetObservable(PaddingProperty));
            text.Bind(ContentPresenter.ForegroundProperty, c.GetObservable(ForegroundProperty));
            Grid.SetColumn(text, 1);
            var root = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), Background = Brushes.Transparent, Children = { box, text } };
            box.RegisterInNameScope(scope); glyph.RegisterInNameScope(scope); text.RegisterInNameScope(scope);
            c._box = box; c._glyph = glyph;
            c.Paint();
            return root;
        });

        Border? _box;
        Avalonia.Controls.Shapes.Path? _glyph;

        public LiteCheck()
        {
            Template = Look;
            Padding = new Thickness(8, 0, 0, 0);
            MinHeight = 32;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Center;
            FontSize = 14;
            ActualThemeVariantChanged += (_, __) => Paint();
            ResourcesChanged += (_, __) => Paint();   // 강조색을 바꾸면
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsCheckedProperty || change.Property == IsPointerOverProperty || change.Property == IsPressedProperty || change.Property == IsEffectivelyEnabledProperty) Paint();
        }

        IBrush? R(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

        void Paint()
        {
            if (_box == null || _glyph == null) return;
            bool on = IsChecked == true;
            var st = (on ? "Checked" : "Unchecked") + (!IsEffectivelyEnabled ? "Disabled" : IsPressed ? "Pressed" : IsPointerOver ? "PointerOver" : "");
            var fill = R("CheckBoxCheckBackgroundFill" + st);
            _box.Background = fill;
            _box.BorderBrush = R("CheckBoxCheckBackgroundStroke" + st) ?? fill;
            _box.CornerRadius = this.TryFindResource("ControlCornerRadius", ActualThemeVariant, out var cr) && cr is CornerRadius r ? r : new CornerRadius(4);
            if (on && _glyph.Data == null && this.TryFindResource("CheckMarkPathData", ActualThemeVariant, out var g) && g is Geometry geo) _glyph.Data = geo;
            _glyph.Fill = R("CheckBoxCheckGlyphForeground" + st);
            _glyph.Opacity = on ? 1 : 0;
            Foreground = R("CheckBoxForeground" + st);
        }
    }
}
