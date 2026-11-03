// 작업 창 위쪽의 메시(렌더러) 선택 — 머티리얼·블렌드쉐이프 페이지가 같이 쓴다.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;

namespace VrcProto.AvHost
{
    /// <summary>작업 창 위쪽의 렌더러 선택.</summary>
    public sealed class RendererPicker : ComboBox
    {
        protected override Type StyleKeyOverride => typeof(ComboBox); // 서브클래스도 Fluent ComboBox 스타일을 받게
        readonly EditorContext _ctx;
        readonly bool _onlyWithShapes;
        bool _suppress;

        public RendererPicker(EditorContext ctx, bool onlyWithShapes)
        {
            _ctx = ctx; _onlyWithShapes = onlyWithShapes;
            HorizontalAlignment = HorizontalAlignment.Stretch; MinHeight = 32;
            SelectionChanged += (_, __) =>
            {
                if (_suppress || SelectedItem is not ComboBoxItem it) return;
                var r = _ctx.Doc!.Original.FindRenderer((string)it.Tag!)!;
                _ctx.Select(r.Id, _ctx.SelectedRenderer == r.Id ? _ctx.SelectedMaterial : r.Materials.FirstOrDefault());
            };
            _ctx.DocumentOpened += Fill;
            // 블렌드쉐이프 창: 셰이프가 없는 렌더러가 선택돼 있으면 창이 보일 때 첫 렌더러로 옮긴다
            // 작업 창을 바꾸면 ContentControl이 패널을 붙였다 뗐다 하므로 붙을 때 확인하면 된다
            // 페이지는 늘 붙어 있고 보이기만 바뀐다 (MainWindow.ShowPage) — 보일 때 확인
            AttachedToVisualTree += (_, __) => Shown();
            _ctx.PageShown += _ => { if (IsEffectivelyVisible) Shown(); };
            _ctx.SelectionChanged += Sync;
        }

        void Shown() { if (_bodyFirst) SelectBody(); EnsureValidSelection(); }

        // 블렌드쉐이프 창: 문서를 연 뒤 처음 보일 때는 몸(Body) 메시부터 보여 준다 (열 때 이미 골라 두었으면 하지 않음 — MainWindow.OpenIr)
        bool _bodyFirst;

        /// <summary>몸(얼굴) 메시: 이름 body · 입 모양(vrc.v_) 블렌드쉐이프 · 블렌드쉐이프 수 순. 블렌드쉐이프가 있는 렌더러 중에서. 없으면 null.</summary>
        public static string? BestBody(IrScene scene)
        {
            string? best = null; int bestScore = -1;
            foreach (var r in scene.Renderers)
            {
                var m = scene.FindMesh(r.Mesh);
                if (m == null || m.BlendShapes.Count == 0 || r.Node < 0 || r.Node >= scene.Nodes.Count) continue;
                var name = scene.Nodes[r.Node].Name.ToLowerInvariant();
                int score = (name == "body" ? 4000000 : name.Contains("body") ? 2000000 : 0)
                          + (m.BlendShapes.Any(b => b.Name.StartsWith("vrc.v_", StringComparison.OrdinalIgnoreCase)) ? 1000000 : 0) + m.BlendShapes.Count;
                if (score > bestScore) { bestScore = score; best = r.Id; }
            }
            return best;
        }

        void SelectBody()
        {
            if (!IsEffectivelyVisible || _ctx.Doc is not { } doc) return;
            _bodyFirst = false;
            var id = BestBody(doc.Original);
            var it = Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == id);
            if (it != null) SelectedItem = it;
        }

        void Fill()
        {
            _bodyFirst = _onlyWithShapes && _ctx.SelectedRenderer == null;
            _suppress = true;
            Items.Clear();
            var doc = _ctx.Doc;
            if (doc != null)
                foreach (var r in doc.Original.Renderers)
                {
                    var shapes = doc.Original.FindMesh(r.Mesh)?.BlendShapes.Count ?? 0;
                    if (_onlyWithShapes && shapes == 0) continue;
                    var name = doc.Original.Nodes[r.Node].Name;
                    var it = new ComboBoxItem { Content = _onlyWithShapes ? $"{name}  ·  {shapes}" : $"{name}  ·  머티리얼 {r.Materials.Count}", Tag = r.Id };
                    ToolTip.SetTip(it, doc.Original.NodePath(r.Node));
                    Items.Add(it);
                }
            _suppress = false;
            Sync();
            if (_bodyFirst) SelectBody();
            if (_ctx.SelectedRenderer == null && Items.Count > 0 && !_onlyWithShapes) SelectedIndex = 0;   // 블렌드쉐이프 창은 보일 때 (Shown)
            EnsureValidSelection();
        }

        void EnsureValidSelection()
        {
            if (_ctx.Doc == null || Items.Count == 0 || !IsEffectivelyVisible) return;
            if (Items.Cast<ComboBoxItem>().Any(i => (string)i.Tag! == _ctx.SelectedRenderer)) return;
            SelectedIndex = 0;
        }

        void Sync()
        {
            _suppress = true;
            SelectedItem = Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == _ctx.SelectedRenderer);
            _suppress = false;
        }
    }
}
