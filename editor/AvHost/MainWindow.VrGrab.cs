// 메인 창: VR 메뉴의 "잡기" 묶음 — 그립으로 본·아바타 잡기 · 본 잡기 설정 · 손 공.
// (2026-11-03 자세 찍기·고정·모션 녹화를 빼고 MainWindow.VrPose.cs에서 남은 것 — 뺀 것은 old/dropped/vr-body)
using Avalonia.Controls;
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        /// <summary>VR 메뉴의 "잡기" 묶음.</summary>
        void AddVrGrabMenu()
        {
            _vrMenu.Items.Add(new Separator());
            var bones = new MenuItem { Header = "그립으로 본·아바타 잡기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrBoneGrab };
            ToolTip.SetTip(bones, "컨트롤러를 관절에 가까이 대면 뼈대와 관절 공이 보여요. 노란 공을 그립으로 잡고 옮기면 그 관절이 따라와요 — 손·발·머리·척추는 방향도 컨트롤러를 따라요. 초록 막대 끝을 잡으면 그 본만 돌리고(컨트롤러를 비틀면 본도 비틀려요), 보라 공(머리카락·치마 PhysBone)은 잡아 끌다가 트리거를 당기면 그 자리에 고정돼요. 관절이 없는 몸 쪽이나 트리거를 당긴 채 그립을 쥐면 아바타째 잡아 옮기고 돌려요. 관절을 그립으로 짧게 쥐었다 놓으면 그 관절이 골라져 고리(그 축으로만 돌리기)와 화살표(그 축으로만 옮기기)가 나와요 — 다시 짧게 쥐면 풀려요. 잡은 채 트리거를 당기면 손이 움직인 만큼의 1/4만 따라와 세밀하게 맞춰요. 함께 세운 아바타도 돼요. 지금 아바타는 자세 페이지·아바타 자리에 기록돼요 (실행 취소로 되돌려요)");
            bones.Click += (_, __) => SetVrBoneGrab(!_settings.VrBoneGrab);
            _vrMenu.Items.Add(bones);
            AddVrBoneOptions();
            var ctl = new MenuItem { Header = "손 공 보이기", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrControllers };
            ToolTip.SetTip(ctl, "컨트롤러 끝에 공을 그려 내 손 자리를 보여요. 이 공이 잡는 점이에요 — 잡을 수 있으면 노랑, 잡는 중이면 주황");
            ctl.Click += (_, __) =>
            {
                _settings.VrControllers = !_settings.VrControllers; _settings.Save();
                _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("controllers", _settings.VrControllers));
                SetStatus(_settings.VrControllers ? "VR에 손 공을 보여요 (컨트롤러 끝, 잡는 점)." : "VR 손 공을 껐어요.");
            };
            _vrMenu.Items.Add(ctl);
        }

        /// <summary>본 잡기 설정 (Avatar Pose System 참고, 2026-10-25): 다른 관절은 제자리 · 관절 잠금 · 손잡이 크기.</summary>
        void AddVrBoneOptions()
        {
            var opt = new MenuItem { Header = "본 잡기 설정", IsEnabled = _settings.VrBoneGrab };
            var pin = new MenuItem { Header = "다른 관절은 제자리", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrBonePinOthers };
            ToolTip.SetTip(pin, "관절 하나를 움직여도 두 손·두 발·머리는 그 자리에 있어요. 엉덩이를 잡고 내리면 발은 바닥에 둔 채 무릎이 굽어 앉고, 척추를 돌려도 손발은 그대로예요. 끄면 잡은 본 아래가 모두 같이 움직여요");
            pin.Click += (_, __) =>
            {
                _settings.VrBonePinOthers = !_settings.VrBonePinOthers;
                SendVrBoneOptions(_settings.VrBonePinOthers ? "다른 관절은 제자리: 관절을 움직여도 손·발·머리는 그 자리에 있어요." : "다른 관절은 제자리를 껐어요. 잡은 본 아래가 같이 움직여요.");
            };
            opt.Items.Add(pin);
            var lockItem = new MenuItem { Header = "관절 잠금 (아바타째만 잡기)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = _settings.VrBoneLock };
            ToolTip.SetTip(lockItem, "관절·막대·PhysBone은 잡히지 않고 아바타째 잡아 옮기기만 돼요. 다 맞춘 자세가 실수로 바뀌지 않게 해요");
            lockItem.Click += (_, __) =>
            {
                _settings.VrBoneLock = !_settings.VrBoneLock;
                SendVrBoneOptions(_settings.VrBoneLock ? "관절을 잠갔어요. 그립으로 아바타째 옮기기만 돼요." : "관절 잠금을 풀었어요.");
            };
            opt.Items.Add(lockItem);
            var size = new MenuItem { Header = "손잡이 크기" };
            ToolTip.SetTip(size, "관절 공·막대 끝·PhysBone 공의 크기와 잡히는 거리예요. 가늘게 할수록 촘촘한 관절을 골라 잡기 쉬워요");
            foreach (var (label, v) in new[] { ("보통", 1.0), ("가늘게", 0.6), ("더 가늘게", 0.35) })
            {
                var mi = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, GroupName = "vrBoneHandle", IsChecked = System.Math.Abs(_settings.VrBoneHandle - v) < 0.001 };
                mi.Click += (_, __) => { _settings.VrBoneHandle = v; SendVrBoneOptions($"손잡이 크기: {label}"); };
                size.Items.Add(mi);
            }
            opt.Items.Add(size);
            _vrMenu.Items.Add(opt);
        }

        void SendVrBoneOptions(string status)
        {
            _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("bonePinOthers", _settings.VrBonePinOthers).Set("boneLock", _settings.VrBoneLock).Set("boneHandle", (float)_settings.VrBoneHandle));
            SetStatus(status);
        }

        void SetVrBoneGrab(bool on)
        {
            _settings.VrBoneGrab = on; _settings.Save();
            _ctx.Render?.Send(Msg.Make(Msg.Vr).Set("boneGrab", on));
            SetStatus(on ? "본·아바타 잡기: 관절에 컨트롤러를 대고 그립으로 잡으면 자세가, 몸 쪽(또는 트리거를 당긴 채)을 잡으면 아바타째 옮겨져요." : "본·아바타 잡기를 껐어요.");
        }
    }
}
