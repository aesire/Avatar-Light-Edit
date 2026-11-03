// 세션의 스튜디오 값 넣기 (2026-11-01): 세션에 studio가 있으면(Tools/Shoot가 만든 세션) 배경·바닥·소품·심도·화질·보정까지 같은 상태로 연 뒤 여기서 이어 고친다.
// 스튜디오 세팅 프리셋(StudioPanel.Preset.cs)도 같은 길로 넣는다.
// 스튜디오 값은 원래 작업 환경(avhost.json)이라 세션에 저장하지 않는다 — 넣은 값도 설정으로 남고, 에디터에서 세션을 다시 저장하면 studio는 적지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class StudioPanel
    {
        /// 칸들을 설정 값에 다시 맞추는 일 (Build…가 자기 지역 칸을 등록한다)
        readonly List<Action> _syncUi = new List<Action>();

        /// <summary>시험용: 그라데이션 칸을 고친 것과 같게 (--test-session-edit).</summary>
        internal void TestGradient(string top, string bottom)
        {
            _settings.StudioBgTop = top; _settings.StudioBgBottom = bottom; _settings.StudioBgMode = "gradient";
            _quiet = true; foreach (var sync in _syncUi) sync(); _bgMode.SelectedIndex = 4; _quiet = false;
            ApplyBackground();
        }

        static string HexOf(JNode c, string fallback)
        {
            if (c.Kind != JKind.Array || c.Count < 3) return fallback;
            int B(int i) => (int)Math.Round(Math.Clamp(c[i].AsDouble, 0, 1) * 255);
            return $"#{B(0):X2}{B(1):X2}{B(2):X2}";
        }

        /// <summary>세션을 열 때 (MainWindow.ApplySessionStudio).</summary>
        internal void ApplySessionStudio(JNode j) => ApplyStudio(j);

        /// <summary>
        /// 스튜디오 값 넣기 (세션의 studio · 프리셋, StudioValues와 같은 모양). 적지 않은 항목은 그대로 둔다:
        /// { background, exposure, rotation, floor, props, dof, quality: { studio, ao, aa }, avatarOnly, transparent, adjust, shot: { width, height, embed }, sticker: { spec, color, outline }, record, cameras }
        /// </summary>
        internal void ApplyStudio(JNode j)
        {
            if (j.Has("background"))
            {
                var bg = j["background"];
                _settings.StudioBgMode = bg["mode"].AsString is "panorama" or "image" or "gradient" or "solid" ? bg["mode"].AsString : "none";
                if (bg.Has("path")) _settings.StudioBgPath = bg["path"].AsString;
                string top = HexOf(bg["top"], _settings.StudioBgTop), bottom = HexOf(bg["bottom"], _settings.StudioBgBottom);
                // 위·아래가 같은 그라데이션은 단색 칸으로 (2026-11-03 — Shoot 장면의 "#RRGGBB" 배경·단색 프리셋)
                if (_settings.StudioBgMode == "solid" || _settings.StudioBgMode == "gradient" && top == bottom) { _settings.StudioBgMode = "solid"; _settings.StudioBgColor = top; }
                else { _settings.StudioBgTop = top; _settings.StudioBgBottom = bottom; }
            }
            if (j.Has("exposure")) _settings.StudioExposure = j["exposure"].AsDouble;
            if (j.Has("rotation")) _settings.StudioRotation = j["rotation"].AsDouble;

            if (j.Has("floor"))
            {
                var fl = j["floor"];
                _settings.StudioFloor = fl["enabled"].AsBool && fl["mode"].AsString is "shadow" or "solid" ? fl["mode"].AsString : "none";
                if (fl.Has("strength")) _settings.ShotShadowStrength = fl["strength"].AsDouble;
                _settings.StudioFloorColor = HexOf(fl["color"], _settings.StudioFloorColor);
            }

            bool hadProps = _propsLoaded;
            if (j.Has("props"))
            {
                var props = JNode.Arr();
                foreach (var p in j["props"].Items)
                {
                    var c = p.Clone();
                    if (!c.Has("name")) c.Set("name", Path.GetFileName(p["path"].AsString).Replace(".ir.json", ""));
                    props.Add(c);
                }
                _settings.StudioProps = props;
            }

            if (j.Has("dof"))
            {
                // 칸들이 이 객체를 붙잡고 있어 바꿔 끼우지 않고 값만 바꾼다
                var d = _settings.StudioDof;
                d.Set("enabled", j["dof"]["enabled"].AsBool);
                foreach (var k in new[] { "strength", "auto", "distance" }) if (j["dof"].Has(k)) d.Set(k, j["dof"][k]);
            }

            var q = j["quality"];
            if (q.Has("studio")) _settings.StudioQuality = q["studio"].AsBool;
            if (q.Has("ao")) _settings.StudioAo = q["ao"].AsDouble;
            if (q.Has("aa")) _settings.StudioCaptureAa = q["aa"].AsInt;
            if (j.Has("avatarOnly"))
            {
                _settings.ViewAvatarOnly = j["avatarOnly"]["on"].AsBool;
                _settings.AvatarOnlyColor = HexOf(j["avatarOnly"]["color"], _settings.AvatarOnlyColor);
            }
            if (j.Has("transparent")) _settings.ShotTransparent = j["transparent"].AsBool;
            var a = j["adjust"];
            if (a.Has("brightness")) _settings.ShotBrightness = a["brightness"].AsDouble;
            if (a.Has("contrast")) _settings.ShotContrast = a["contrast"].AsDouble;
            if (a.Has("saturation")) _settings.ShotSaturation = a["saturation"].AsDouble;
            var shot = j["shot"];
            if (shot.Has("width") && shot.Has("height")) { _settings.ShotWidth = shot["width"].AsInt; _settings.ShotHeight = shot["height"].AsInt; }
            if (shot.Has("embed")) _settings.ShotEmbedSession = shot["embed"].AsBool;
            var st = j["sticker"];
            if (st.Has("spec")) _settings.StickerSpec = st["spec"].AsString;
            if (st.Has("color")) _settings.StickerOutlineColor = st["color"].AsString;
            if (st.Has("outline")) _settings.StickerOutline = Math.Clamp(st["outline"].AsInt, 0, 30);
            if (j["record"].Kind == JKind.Object) _settings.StudioRecord = j["record"].Clone();
            if (j["cameras"].Kind == JKind.Array) _settings.Cameras = j["cameras"].Clone();
            _settings.Save();

            // 칸을 맞춘 뒤 렌더러에 올린다 (아직 연결 전이면 Restore가 같은 값을 올린다)
            bool hadBg = _bgLoaded;
            _bgLoaded = false;
            _quiet = true;
            foreach (var sync in _syncUi) sync();
            _quiet = false;
            ShowBgState();
            ShowAvatarOnly();
            // 이미지가 없으면 앞 배경을 남기지 않는다 (값은 두고 "찾지 못했어요"로 알린다)
            if (_settings.StudioBgMode is "panorama" or "image" && !File.Exists(_settings.StudioBgPath)) { if (hadBg) Send(JNode.Obj().Set("background", JNode.Obj().Set("mode", "none"))); }
            else if (hadBg || _settings.StudioBgMode != "none") ApplyBackground();
            _propsLoaded = _settings.StudioProps.Count > 0;
            RefreshProps();
            if (hadProps || _propsLoaded) Send(JNode.Obj().Set("props", PropsMessage()));   // 없어진 소품도 내려야 한다
            SendDof();
            SendFloor(force: true);
            RefreshCameras();
            QualityChanged?.Invoke();
            ViewChanged?.Invoke();
        }
    }
}
