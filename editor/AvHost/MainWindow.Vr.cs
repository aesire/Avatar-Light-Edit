// 메인 창: 미리보기 도구 모음의 "VR" 메뉴 — SteamVR 연결 · VR로 보기 (PLAN 3.12).
// 연결과 그리기는 렌더러가 한다(VrLink·VrView). 여기서는 켜고 끄기를 보내고 vrStatus를 메뉴·상태 표시줄에 보인다.
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        // 켜 둔 상태 (렌더러를 다시 띄우면 다시 보낸다) — 연결·보기는 설정에 남기지 않는다. PC 화면은 남긴다
        bool _vrWanted, _vrView;
        JNode _vrStatus = JNode.Obj();
        MenuFlyout _vrMenu = null!;
        TextBlock _vrLabel = null!;
        readonly StatusItem _vrItem = new StatusItem("VR") { IsVisible = false };

        Button VrMenu()
        {
            _vrMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            _vrMenu.Opening += (_, __) => RefreshVrMenu();
            var b = _vrButton = Ui.DropButton(Glyph.Headset, "VR", _vrMenu, out _vrLabel, "SteamVR로 아바타를 헤드셋에서 봐요. \"SteamVR 연결\"만 켜면 장치만 확인하고, \"VR로 보기\"를 켜면 헤드셋에 화면이 나와요");
            RefreshVrMenu();
            return b;
        }

        bool VrRendererReady()
        {
            if (_ctx.Render != null && _ctx.Render.IsConnected) return true;
            SetStatus("미리보기가 켜지면 SteamVR에 연결해요.");
            RefreshVr();
            return false;
        }

        void SetVr(bool on)
        {
            _vrWanted = on;
            if (!on) _vrView = false;
            if (!VrRendererReady()) return;
            _ctx.Render!.Send(Msg.Make(Msg.Vr).Set("enabled", on));
            if (!on) SetStatus("SteamVR 연결을 껐어요.");
            RefreshVr();
        }

        void SetVrView(bool on)
        {
            _vrView = on;
            if (on) _vrWanted = true;
            if (!VrRendererReady()) return;
            _ctx.Render!.Send(VrViewMessage(on));
            SetStatus(on ? "VR로 보기를 켰어요. 헤드셋을 쓰면 아바타 앞에 서 있어요 (왼쪽 스틱 이동 · 오른쪽 스틱 회전 · A 점프, 자리 맞추기로 다시 잡아요)." : "VR로 보기를 껐어요. 헤드셋에는 SteamVR 홈이 나와요.");
            RefreshVr();
        }

        void SetVrDesktop(string mode)
        {
            _settings.VrDesktop = mode; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("desktop", mode));
            SetStatus(mode == "eye" ? "PC 화면: 헤드셋에 보이는 내 눈 화면이에요." : "PC 화면: 미리보기 카메라예요 (마우스로 돌려 볼 수 있어요).");
        }

        JNode VrViewMessage(bool on) => WithVrCamera(Msg.Make(Msg.Vr).Set("view", on).Set("desktop", _settings.VrDesktop)
            .Set("resolution", (float)_settings.VrResolution).Set("smoothTurn", _settings.VrSmoothTurn).Set("boneGrab", _settings.VrBoneGrab).Set("itemGrab", _settings.VrItemGrab).Set("itemSnap", _settings.VrItemSnap).Set("itemSurface", _settings.VrItemSurface).Set("itemMirror", _settings.VrItemMirror).Set("controllers", _settings.VrControllers)
            .Set("bonePinOthers", _settings.VrBonePinOthers).Set("boneLock", _settings.VrBoneLock).Set("boneHandle", (float)_settings.VrBoneHandle)
            .Set("mirror", _settings.VrMirror).Set("personalMirror", _settings.VrPersonalMirror).Set("mirrorQuality", (float)_settings.VrMirrorQuality));

        /// <summary>VR 거울 (VRChat처럼): 월드 거울 = 아바타를 불러온 자리 정면 · 개인 거울 = 내 앞, 그립으로 잡아 옮기고 두 손으로 크기.</summary>
        void SetVrMirror(bool? world, bool? personal)
        {
            if (world is bool w) _settings.VrMirror = w;
            if (personal is bool p) _settings.VrPersonalMirror = p;
            _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("mirror", _settings.VrMirror).Set("personalMirror", _settings.VrPersonalMirror));
            SetStatus(personal == true ? "개인 거울을 내 앞에 놓았어요. 그립으로 잡아 옮기고, 두 손으로 잡고 벌리면 커져요."
                : world == true ? "월드 거울을 아바타 정면 2 m에 세웠어요." : "거울을 껐어요.");
        }

        void VrMirrorSummon()
        {
            if (!_settings.VrPersonalMirror) { SetVrMirror(null, true); return; }
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("mirrorSummon", true));
            SetStatus("개인 거울을 내 앞으로 불렀어요.");
        }

        void SetVrMirrorQuality(double q)
        {
            _settings.VrMirrorQuality = q; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("mirrorQuality", (float)q));
            SetStatus($"거울 화질 {q * 100:0}% (눈 화면 기준). 거울을 볼 때 끊기면 낮춰 보세요.");
        }

        void SetVrSmoothTurn(bool on)
        {
            _settings.VrSmoothTurn = on; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("smoothTurn", on));
            SetStatus(on ? "부드러운 회전: 오른쪽 스틱을 기울인 만큼 계속 돌아요." : "스냅 회전: 오른쪽 스틱을 젖힐 때마다 30°씩 돌아요.");
        }

        void SetVrResolution(double res)
        {
            _settings.VrResolution = res; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("resolution", (float)res));
            SetStatus($"VR 해상도 {res * 100:0}% (SteamVR 권장 크기 기준). 끊기면 낮춰 보세요.");
        }

        void VrRecenter()
        {
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("recenter", true));
            SetStatus("자리를 다시 맞췄어요: 지금 보는 쪽이 아바타 정면이 돼요.");
        }

        /// <summary>렌더러가 (다시) 준비되면 켜 둔 연결·보기를 다시 보낸다.</summary>
        void SendVrOnReady()
        {
            _vrStatus = JNode.Obj();
            if (_vrView) _ctx.Render?.Send(VrViewMessage(true));
            else if (_vrWanted) _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("enabled", true).Set("desktop", _settings.VrDesktop));
            RefreshVr();
        }

        void OnVrStatus(JNode m)
        {
            var before = _vrStatus["state"].AsString + _vrStatus["text"].AsString + _vrStatus["error"].AsString;
            _vrStatus = m;
            var state = m["state"].AsString;
            if (state + m["text"].AsString + m["error"].AsString != before && m["text"].AsString.Length > 0)
            {
                var err = m["error"].AsString;
                if (state == "ok" || state == "error" || err.Length > 0) SetStatus(err.Length > 0 ? err : m["text"].AsString);
                AppendLog(state == "error" || err.Length > 0 ? "warn" : "info", "VR: " + m["text"].AsString + (state == "ok" ? " · " + VrDevicesText() : "") + (err.Length > 0 ? " · " + err : ""));
            }
            RefreshVr();
        }

        /// <summary>" · GPU 9.8 ms · 재투영 12%" — 재투영이 많으면 렌더러가 헤드셋 주기를 못 맞추는 것 (해상도를 낮추면 나아진다).</summary>
        string VrTimingText()
        {
            float gpu = _vrStatus["gpuMs"].AsFloat; int rep = _vrStatus["reprojected"].AsInt, drop = _vrStatus["dropped"].AsInt;
            return (gpu > 0 ? $" · GPU {gpu:0.0} ms" : "") + (rep > 0 ? $" · 재투영 {rep}%" : "") + (drop > 0 ? $" · 놓친 프레임 {drop}" : "");
        }

        string VrDevicesText()
        {
            var devs = _vrStatus["devices"].Items.ToList();
            int Count(string kind) => devs.Count(d => d["kind"].AsString == kind);
            var parts = new System.Collections.Generic.List<string>();
            if (Count("hmd") > 0) parts.Add("헤드셋");
            if (Count("controller") > 0) parts.Add($"컨트롤러 {Count("controller")}");
            if (Count("tracker") > 0) parts.Add($"트래커 {Count("tracker")}");
            return parts.Count == 0 ? "장치 없음" : string.Join(" · ", parts);
        }

        void RefreshVr()
        {
            UpdateVrPanelMode();   // VR로 보기가 켜지고 꺼질 때 에디터 창도 (MainWindow.VrPanel.cs)
            _vrHand?.Refresh();
            var state = _vrWanted ? _vrStatus["state"].AsString : "off";
            bool viewing = state == "ok" && _vrStatus["view"].AsBool;
            int fps = _vrStatus["fps"].AsInt;
            _vrLabel.Text = state switch
            {
                "ok" => viewing ? (fps > 0 ? $"VR {fps} fps" : "VR로 보는 중") : "VR 연결됨",
                "waiting" => "VR 기다리는 중", "error" => "VR 오류", _ => "VR",
            };
            _vrItem.IsVisible = _vrWanted;
            var text = _vrStatus["text"].AsString;
            bool warn = _vrStatus["error"].AsString.Length > 0 || _vrStatus["inputProblem"].AsString.Length > 0 || _vrStatus["reprojected"].AsInt >= 20;
            _vrItem.Set(state switch { "ok" => warn ? StatusItem.State.Warn : StatusItem.State.On, "error" => StatusItem.State.Error, _ => StatusItem.State.Busy },
                state == "ok" ? (viewing ? $"VR로 보는 중 · {fps} fps" + VrTimingText() + " · " : "VR · ") + VrDevicesText() : text.Length > 0 ? "VR · " + text : "VR · 연결하는 중");
            FitToolsLater();
        }

        void RefreshVrMenu()
        {
            _vrMenu.Items.Clear();
            var view = new MenuItem { Header = "VR로 보기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _vrView };
            ToolTip.SetTip(view, "헤드셋에 아바타를 보여 줘요. SteamVR이 꺼져 있으면 켜요. 아바타 앞 1.2 m에 서서 실제 크기로 보고, 관절·소품을 손으로 잡아 고쳐요");
            view.Click += (_, __) => SetVrView(!_vrView);
            _vrMenu.Items.Add(view);
            var connect = new MenuItem { Header = "SteamVR 연결만 (장치 확인)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _vrWanted && !_vrView };
            ToolTip.SetTip(connect, "헤드셋에 화면을 보내지 않고 장치만 확인해요. SteamVR이 켜져 있으면 바로, 꺼져 있으면 켜질 때 자동으로 연결돼요 (SteamVR을 대신 켜지는 않아요)");
            connect.Click += (_, __) => { if (_vrView) { _vrView = false; SetVr(true); _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("view", false)); } else SetVr(!_vrWanted); };
            _vrMenu.Items.Add(connect);
            if (_vrView)
            {
                var recenter = new MenuItem { Header = "자리 다시 맞추기" };
                ToolTip.SetTip(recenter, "지금 내가 보는 쪽이 아바타 정면이 되게, 아바타 앞 1.2 m로 옮겨요");
                recenter.Click += (_, __) => VrRecenter();
                _vrMenu.Items.Add(recenter);
                var desk = new MenuItem { Header = "PC 화면" };
                foreach (var (label, mode) in new[] { ("헤드셋 화면 (내 눈, VRChat처럼)", "eye"), ("미리보기 카메라", "orbit") })
                {
                    var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "vrDesktop", IsChecked = _settings.VrDesktop == mode };
                    mi.Click += (_, __) => SetVrDesktop(mode);
                    desk.Items.Add(mi);
                }
                _vrMenu.Items.Add(desk);
                var smooth = new MenuItem { Header = "부드러운 회전", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrSmoothTurn };
                ToolTip.SetTip(smooth, "켜면 오른쪽 스틱을 기울인 만큼 계속 돌아요 (VRChat Comfort Turning 끔). 끄면 30°씩 끊어 돌아요");
                smooth.Click += (_, __) => SetVrSmoothTurn(!_settings.VrSmoothTurn);
                _vrMenu.Items.Add(smooth);
                var res = new MenuItem { Header = $"VR 해상도: {_settings.VrResolution * 100:0}%" };
                ToolTip.SetTip(res, "헤드셋에 보내는 화면 크기예요 (SteamVR 권장 크기 기준). 끊기거나 재투영이 많으면 낮춰 보세요");
                foreach (var v in new[] { 0.6, 0.8, 1.0, 1.2 })
                {
                    var mi = new MenuItem { Header = $"{v * 100:0}%" + (v == 1.0 ? " (SteamVR 권장)" : ""), ToggleType = MenuItemToggleType.Radio, GroupName = "vrRes", IsChecked = System.Math.Abs(_settings.VrResolution - v) < 0.001 };
                    mi.Click += (_, __) => SetVrResolution(v);
                    res.Items.Add(mi);
                }
                _vrMenu.Items.Add(res);
                _vrMenu.Items.Add(new Separator());
                var world = new MenuItem { Header = "월드 거울", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrMirror };
                ToolTip.SetTip(world, "VRChat 월드의 큰 거울처럼 아바타를 불러온 자리 정면 2 m에 거울을 세워요. 좌우가 바뀌어 비치고 내 머리도 보여요");
                world.Click += (_, __) => SetVrMirror(!_settings.VrMirror, null);
                _vrMenu.Items.Add(world);
                var personal = new MenuItem { Header = "개인 거울", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrPersonalMirror };
                ToolTip.SetTip(personal, "VRChat 개인 거울처럼 내 앞에 작은 거울을 놓아요. 걸으면 따라오고, 그립으로 잡아 옮기고, 두 손으로 잡고 벌리면 크기가 바뀌어요");
                personal.Click += (_, __) => SetVrMirror(null, !_settings.VrPersonalMirror);
                _vrMenu.Items.Add(personal);
                var summon = new MenuItem { Header = "개인 거울 내 앞으로" };
                summon.Click += (_, __) => VrMirrorSummon();
                _vrMenu.Items.Add(summon);
                var mq = new MenuItem { Header = $"거울 화질: {_settings.VrMirrorQuality * 100:0}%" };
                ToolTip.SetTip(mq, "거울에 비치는 화면의 크기예요 (눈 화면 기준). 거울을 그리는 만큼 더 무거워지니 끊기면 낮춰 보세요");
                foreach (var v in new[] { 0.5, 0.75, 1.0 })
                {
                    var mi = new MenuItem { Header = $"{v * 100:0}%", ToggleType = MenuItemToggleType.Radio, GroupName = "vrMirrorQ", IsChecked = System.Math.Abs(_settings.VrMirrorQuality - v) < 0.001 };
                    mi.Click += (_, __) => SetVrMirrorQuality(v);
                    mq.Items.Add(mi);
                }
                mq.IsEnabled = _settings.VrMirror || _settings.VrPersonalMirror;
                _vrMenu.Items.Add(mq);
                AddVrGrabMenu();
                AddVrCameraMenu();
                AddVrEditMenu();
                AddVrPanelMenu();
            }
            if (!_vrWanted) return;
            _vrMenu.Items.Add(new Separator());
            var text = _vrStatus["text"].AsString;
            _vrMenu.Items.Add(new MenuItem { Header = text.Length > 0 ? text : "연결하는 중…", IsEnabled = false });
            foreach (var k in new[] { "error", "inputProblem" })
                if (_vrStatus[k].AsString.Length > 0) _vrMenu.Items.Add(new MenuItem { Header = _vrStatus[k].AsString, IsEnabled = false });
            foreach (var d in _vrStatus["devices"].Items)
            {
                var kind = d["kind"].AsString;
                if (kind == "base") continue;   // 베이스 스테이션은 줄이지 않고 숨긴다 (위치를 재는 장치일 뿐)
                var name = kind switch { "hmd" => "헤드셋", "controller" => "컨트롤러", "tracker" => "트래커", _ => "장치" };
                var role = d["role"].AsString switch { "left" => " (왼손)", "right" => " (오른손)", _ => "" };
                var model = string.Join(" ", new[] { d["maker"].AsString, d["model"].AsString }.Where(s => s.Length > 0));
                var track = d["tracked"].AsBool ? "" : " · 위치 읽을 수 없음";
                _vrMenu.Items.Add(new MenuItem { Header = $"{name}{role}{(model.Length > 0 ? " · " + model : "")}{track}", IsEnabled = false });
            }
        }
    }
}
