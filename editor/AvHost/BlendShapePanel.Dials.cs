// 세밀 조정 막대의 쉐이프키 묶음 다이얼에 묶음을 알린다 (FineRail.Group.cs): 마지막으로 바꾼(SetMany)·고른(줄 누르기·고르기)·열어 본(묶음 머리) 묶음.
// 같은 쉐이프키가 즐겨찾기 묶음에도 있으면 원래 묶음을 쓴다. 메시를 바꾸면 같은 이름 묶음이 있으면 그것으로, 없으면 지운다.
using System;
using System.Linq;
using VrcProto.Host.Core;

namespace VrcProto.AvHost
{
    public sealed partial class BlendShapePanel
    {
        public event Action<FineRail.Group?>? GroupFocused;
        string? _focusGroup, _lastGroup;

        /// <summary>즐겨찾기가 없을 때 빈 즐겨찾기 묶음 (막대에 안내 글)</summary>
        void EmptyFavorites()
        {
            _focusGroup = FavKey;
            GroupFocused?.Invoke(new FineRail.Group
            {
                Title = "즐겨찾기", IsFavorites = true, Others = _groups.Where(x => !x.Favorites).Select(x => x.Key).ToList(), Switch = FocusGroup,
                ToFavorites = EmptyFavorites, ToGroups = () => FocusGroup(_lastGroup ?? _groups.FirstOrDefault()?.Key ?? ""),
            });
        }

        /// <summary>떠 있는 다이얼 프리셋: "메시\u001f쉐이프키" → 값 (고르기와 상관없이 그 하나만)</summary>
        void RegisterResolver()
        {
            Ui.FineResolvers["shape"] = key =>
            {
                var parts = key.Split('\u001f');
                if (parts.Length != 2 || Doc == null || Doc.Original.FindRenderer(parts[0]) == null) return null;
                return ShapeTarget(parts[0], parts[1]);
            };
        }

        Ui.FineTarget ShapeTarget(string renderer, string n)
        {
            var a = Addr.BlendShape(renderer, n);
            return new Ui.FineTarget
            {
                Name = Disp(n), Min = 0, Max = 100, Step = 1, Kind = "shape", Key = renderer + "\u001f" + n,
                Get = () => Doc?.Get(a).AsDouble ?? 0,
                Preview = v => Doc?.Preview(a, v),
                Commit = v => Doc?.Set(a, Math.Round(Math.Clamp(v, 0, 100), 2), "BlendShape " + n),
            };
        }

        void FocusGroupOf(string name)
        {
            var g = _groups.FirstOrDefault(x => !x.Favorites && x.Names.Contains(name)) ?? _groups.FirstOrDefault(x => x.Names.Contains(name));
            if (g != null) FocusGroup(g.Key);
        }

        void FocusGroup(string key)
        {
            if (Doc == null || _renderer == null) return;
            var g = _groups.FirstOrDefault(x => x.Key == key);
            if (g == null) return;
            _focusGroup = key;
            if (key != FavKey) _lastGroup = key;
            var renderer = _renderer;
            GroupFocused?.Invoke(new FineRail.Group
            {
                Title = key == FavKey ? "즐겨찾기" : Disp(key),
                Items = g.Names.Select(n =>
                {
                    var a = Addr.BlendShape(renderer, n);
                    return new FineRail.Shape
                    {
                        Fine = () => ShapeTarget(renderer, n),
                        Name = Disp(n), Get = () => Doc?.Get(a).AsDouble ?? 0,
                        Preview = v => PreviewShape(n, v), Commit = v => SetShape(n, Math.Round(v, 2)),
                        Reset = () => ResetShape(n), Modified = () => Doc != null && Doc.IsModified(a),
                    };
                }).ToList(),
                Others = _groups.Where(x => !x.Favorites).Select(x => x.Key).ToList(),
                Switch = FocusGroup, Label = Disp,
                IsFavorites = key == FavKey,
                ToFavorites = () => { if (_groups.Any(x => x.Favorites)) FocusGroup(FavKey); else EmptyFavorites(); },
                ToGroups = () => FocusGroup(_lastGroup != null && _groups.Any(x => x.Key == _lastGroup) ? _lastGroup : _groups.FirstOrDefault(x => !x.Favorites)?.Key ?? ""),
            });
        }

        void RefocusGroup()
        {
            if (_focusGroup != null && _groups.Any(x => x.Key == _focusGroup)) FocusGroup(_focusGroup);
            else { _focusGroup = null; GroupFocused?.Invoke(null); }
        }

        /// <summary>고른 쉐이프키 줄을 세밀 조정 막대로 (고르기 클릭)</summary>
        void OfferRow(string name)
        {
            if (_rows.TryGetValue(name, out var l) && l.Count > 0) Ui.OfferFine(l[0].Slider.Fine());
            FocusGroupOf(name);
        }

        /// <summary>화면 확인 모드: 첫 묶음(즐겨찾기 빼고)을 다이얼로</summary>
        public void FocusFirstGroupForTest()
        {
            var g = _groups.FirstOrDefault(x => !x.Favorites && x.Names.Count > 0);
            if (g != null) FocusGroup(g.Key);
        }
    }
}
