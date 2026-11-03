// 메인 창: 함께 세운 아바타 (2026-10-25, GuestPanel · 렌더러 GuestAvatars). 아바타 페이지 "함께 세우기" 탭.
// 렌더러가 (다시) 준비되면 지금 아바타를 불러온 다음 목록을 보낸다. 옮긴 자리·자세는 렌더러가 알려 주면 설정에 적는다.
using VrcProto.Protocol;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        GuestPanel? _guestPanel;

        void BuildGuests()
        {
            _guestPanel = new GuestPanel(_ctx, _settings)
            {
                SendList = SendGuests,
                Select = SelectGuest,
                CopyPose = id => _ctx.Render?.Send(Msg.Make(Msg.Guests).Set("copyPose", id)),
            };
            _avatarPanel!.AddTab("guests", "함께 세우기", _guestPanel);
        }

        void SendGuests()
        {
            if (_guestPanel == null || _ctx.Render == null) return;
            if (_settings.Guests.Count == 0 && !_guestsSent) return;   // 한 번도 세운 적 없으면 보내지 않는다
            _guestsSent = true;
            _ctx.Render.Send(Msg.Make(Msg.Guests).Set("list", _guestPanel.ListMessage()));
        }

        bool _guestsSent;

        void SelectGuest(string id)
        {
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { SetStatus("미리보기가 켜져야 고를 수 있어요."); return; }
            if (!_settings.ViewPoseBones) { _settings.ViewPoseBones = true; ViewChanged(); }
            _ctx.Render.Send(Msg.Make(Msg.Select).Set("kind", "guest").Set("id", id));
            _viewport.Activate();
        }

        /// <summary>OnRendererMessage에서: 함께 세운 아바타 메시지면 처리하고 true.</summary>
        bool OnGuestMessage(JNode m)
        {
            switch (Msg.Type(m))
            {
                case Msg.GuestsState: _guestPanel?.OnState(m["guests"]); return true;
                case Msg.GuestMoved: _guestPanel?.OnMoved(m); return true;
                case Msg.GuestPoseEdited: _guestPanel?.OnPoseEdited(m); return true;
            }
            return false;
        }
    }
}
