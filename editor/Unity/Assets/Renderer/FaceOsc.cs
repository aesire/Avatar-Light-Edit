// 얼굴 값 소스: OSC (VRCFaceTracking처럼 받기, 2026-10-24 사용자: Project Babble을 참조해 VRCFT처럼).
// VRChat에서는 Babble(입)·EyeTrackVR/Baballonia(눈) 같은 트래커 앱 → VRCFaceTracking → 아바타 파라미터(v2/…)로 이어진다.
// 이 소스는 VRCFT 자리에서 트래커 앱이 보내는 OSC를 바로 받는다 (기본 UDP 8888 = Babble 앱의 기본 보내는 포트, VRCFT Babble 모듈과 같은 자리 — VRCFT는 꺼 둔다).
//   주소의 마지막 부분을 이름으로 본다 (앞 경로·접두어는 무시): float · int · bool(T/F) 하나 또는 여러 개
//     Babble          /cheekPuffLeft … /mouthFunnel … /tongueOut … /tongueTwistRight (45개, ARKit 이름 + 혀·볼 빨기)
//                     VRCFT Babble 모듈처럼 mouthFunnel·mouthPucker는 ×4, mouthLeft·mouthRight는 ×2 (모델 값이 작다) — 경로 없는 이름에만
//     VRCFT v2 파라미터 /avatar/parameters/v2/JawOpen, FT/v2/… (VRCFT의 보내기를 이쪽으로 돌렸을 때): EyeLidLeft·EyeLeftX·SmileSadLeft·JawX 처럼 합친 값은 풀어서
//     Baballonia 눈   /LeftEyeX·/LeftEyeY·/LeftEyeLid(뜬 정도)·/RightEye… (VRCFT Babble 모듈이 읽는 이름, EyeTrackVR의 EyesY·…Widen도)
//     Unified Expressions 이름 (EyeClosedLeft·MouthCornerPullLeft·LipFunnelUpperLeft …)
//     VRChat 기본 시선 OSC /tracking/eye/LeftRightPitchYaw(왼 pitch·yaw·오른 pitch·yaw, 도) · CenterPitchYaw · EyesClosedAmount (Baballonia 등)
// 값은 FaceTracker의 얼굴 값(ARKit 이름 + 혀·볼 빨기)으로 바꾼다. 나머지(몸·카메라 등)는 무시한다.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace VrcProto.Viewer
{
    public sealed class OscFaceSource : IFaceSource
    {
        readonly UdpClient _udp;
        readonly Thread _thread;
        readonly object _lock = new object();
        readonly Dictionary<string, float> _cur = new Dictionary<string, float>();
        volatile bool _running = true;
        bool _fresh;
        float _fpsAt; float _fps; float _lastAt = -10; int _pending, _frames;   // fps = 새 값이 온 렌더 프레임 수 (트래커 앱이 더 빠르면 화면 fps)
        readonly int _port;
        string _sender = "";
        static Dictionary<string, string> _names;   // 정규화 이름 → 얼굴 값 이름

        public OscFaceSource(int port)
        {
            _port = port;
            if (_names == null)
            {
                _names = new Dictionary<string, string>();
                foreach (var a in FaceRetarget.ArKit) _names[FaceRetarget.Norm(a)] = a;
                foreach (var a in FaceRetarget.Extra) _names[FaceRetarget.Norm(a)] = a;
                foreach (var kv in Unified) _names[kv.Key] = kv.Value;
            }
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            _udp.Client.ReceiveTimeout = 500;
            _thread = new Thread(Run) { IsBackground = true, Name = "FaceOSC" };
            _thread.Start();
        }

        public string Name => "osc";
        public bool Ok => true;
        public bool Face => Time.unscaledTime - _lastAt < 1f;
        public float Fps => Face ? _fps : 0;
        public string Text => Face ? $"트래커 앱에서 받는 중 (UDP {_port}{_sender})"
            : $"UDP {_port}에서 기다리는 중 · Babble 앱의 OSC 보내기 포트를 {_port}로 (VRCFaceTracking이 같은 포트를 쓰면 꺼 주세요)";

        /// <summary>Unified Expressions 이름(정규화) → 얼굴 값. 좌우·위아래로 나뉜 입술은 같은 값으로 (큰 쪽).</summary>
        static readonly Dictionary<string, string> Unified = new Dictionary<string, string>
        {
            ["eyeclosedleft"] = "eyeBlinkLeft", ["eyeclosedright"] = "eyeBlinkRight",
            ["browlowererleft"] = "browDownLeft", ["browlowererright"] = "browDownRight", ["browpinchleft"] = "browDownLeft", ["browpinchright"] = "browDownRight",
            ["browinnerupleft"] = "browInnerUp", ["browinnerupright"] = "browInnerUp",
            ["mouthclosed"] = "mouthClose", ["lipsuckupper"] = "mouthRollUpper", ["lipsucklower"] = "mouthRollLower",
            ["lipsuckupperleft"] = "mouthRollUpper", ["lipsuckupperright"] = "mouthRollUpper", ["lipsucklowerleft"] = "mouthRollLower", ["lipsucklowerright"] = "mouthRollLower",
            ["lipfunnel"] = "mouthFunnel", ["lipfunnelupperleft"] = "mouthFunnel", ["lipfunnelupperright"] = "mouthFunnel", ["lipfunnellowerleft"] = "mouthFunnel", ["lipfunnellowerright"] = "mouthFunnel",
            ["lippucker"] = "mouthPucker", ["lippuckerupperleft"] = "mouthPucker", ["lippuckerupperright"] = "mouthPucker", ["lippuckerlowerleft"] = "mouthPucker", ["lippuckerlowerright"] = "mouthPucker",
            ["mouthupperleft"] = "mouthLeft", ["mouthlowerleft"] = "mouthLeft", ["mouthupperright"] = "mouthRight", ["mouthlowerright"] = "mouthRight",
            ["mouthcornerpullleft"] = "mouthSmileLeft", ["mouthcornerpullright"] = "mouthSmileRight",
            ["mouthsadleft"] = "mouthFrownLeft", ["mouthsadright"] = "mouthFrownRight",
            ["mouthraiserupper"] = "mouthShrugUpper", ["mouthraiserlower"] = "mouthShrugLower",
            ["cheekpuff"] = "cheekPuff", ["cheekpuffleft"] = "cheekPuff", ["cheekpuffright"] = "cheekPuff",
        };

        // 주소마다 한 번만 풀어 둔다: Baballonia는 10 ms마다 메시지 51개를 따로따로(번들 없이) 보내서 초당 5천 개쯤 온다.
        // 패킷마다 byte[]·문자열을 만들면 GC가 자주 돌아 화면(VR 포함)이 끊겼다 (2026-10-24 사용자) — 받는 버퍼·인자 버퍼를 다시 쓰고 주소는 해시로 찾는다.
        sealed class Route { public int Kind; public string Key, Fallback, Sender; public float Mul = 1; }
        const int RNone = 0, RGaze = 1, RV2 = 2, RKey = 3, REye = 4;
        readonly Dictionary<ulong, Route> _routes = new Dictionary<ulong, Route>();
        readonly byte[] _buf = new byte[65536];
        readonly float[] _args = new float[8];
        int _eyeAt = int.MinValue;   // Baballonia 눈 값을 마지막으로 받은 때 (Environment.TickCount)
        float _lidL = -1, _lidR = -1; int _gazeSeen, _gazeIgnored;   // 진단용 (FaceTracker가 1초마다 로그에 남긴다)
        /// <summary>진단: 마지막으로 받은 Baballonia 눈꺼풀(뜬 정도)과 VRChat 시선 OSC 수.</summary>
        public string EyeDiag => $"OSC Lid L {_lidL:0.00} R {_lidR:0.00} · 시선 OSC {_gazeSeen}개(무시 {_gazeIgnored})";

        /// <summary>Baballonia·EyeTrackVR 눈 값 (VRCFT Babble 모듈이 읽는 이름): X·Y = 시선(-1~1, +X 오른쪽·+Y 위), Lid = 뜬 정도(1 뜸), Widen = 크게.</summary>
        static readonly HashSet<string> EyeNames = new HashSet<string>
        {
            "LeftEyeX", "LeftEyeY", "RightEyeX", "RightEyeY", "EyesY", "LeftEyeLid", "RightEyeLid", "LeftEyeWiden", "RightEyeWiden",
        };

        void Run()
        {
            while (_running)
            {
                int n;
                try { n = _udp.Client.Receive(_buf); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { return; }
                try { lock (_lock) { Packet(_buf, 0, n); _pending++; } } catch { /* 깨진 패킷은 버린다 */ }
            }
        }

        static bool IsBundle(byte[] b, int off, int len) =>
            len >= 16 && b[off] == '#' && b[off + 1] == 'b' && b[off + 2] == 'u' && b[off + 3] == 'n' && b[off + 4] == 'd' && b[off + 5] == 'l' && b[off + 6] == 'e' && b[off + 7] == 0;

        void Packet(byte[] b, int off, int len)
        {
            if (IsBundle(b, off, len))
            {
                int p = off + 16;
                while (p + 4 <= off + len)
                {
                    int size = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]; p += 4;
                    if (size <= 0 || p + size > off + len) break;
                    Packet(b, p, size);
                    p += size;
                }
                return;
            }
            int q = off, end = off + len;
            // 주소: FNV-1a 해시로 풀어 둔 처리를 찾는다 (처음 보는 주소만 문자열로 만든다)
            ulong h = 14695981039346656037UL;
            int s = q;
            while (q < end && b[q] != 0) { h = (h ^ b[q]) * 1099511628211UL; q++; }
            int alen = q - s;
            h ^= (ulong)alen << 56;
            q = (q + 4) & ~3;
            if (!_routes.TryGetValue(h, out var route)) _routes[h] = route = Resolve(Encoding.UTF8.GetString(b, s, alen));
            if (route.Kind == RNone) return;
            if (q >= end || b[q] != ',') return;
            int t = q + 1;
            while (q < end && b[q] != 0) q++;
            int tend = q;
            q = (q + 4) & ~3;
            int count = 0;
            for (int i = t; i < tend && count < _args.Length; i++)
                switch (b[i])
                {
                    case (byte)'f': if (q + 4 > end) return; _args[count++] = BigFloat(b, q); q += 4; break;
                    case (byte)'i': if (q + 4 > end) return; _args[count++] = (b[q] << 24) | (b[q + 1] << 16) | (b[q + 2] << 8) | b[q + 3]; q += 4; break;
                    case (byte)'T': _args[count++] = 1; break;
                    case (byte)'F': _args[count++] = 0; break;
                    default: return;
                }
            if (count == 0) return;
            Message(route, _args, count);
        }

        void Set(string name, float v) => _cur[name] = Mathf.Clamp01(v);
        void Signed(float v, string neg, string pos) { Set(neg, -v); Set(pos, v); }

        /// <summary>주소 하나를 어떻게 처리할지 정한다 (주소마다 한 번).</summary>
        static Route Resolve(string address)
        {
            // VRChat 기본 시선 OSC
            if (address.StartsWith("/tracking/eye/", StringComparison.Ordinal))
                return new Route { Kind = RGaze, Key = address.Substring(14), Sender = " · VRChat 시선 OSC" };
            int slash = address.LastIndexOf('/');
            var name = address.Substring(slash + 1);
            if (EyeNames.Contains(name)) return new Route { Kind = REye, Key = name, Sender = " · Baballonia 눈" };
            _names.TryGetValue(FaceRetarget.Norm(name), out var key);
            if (address.Contains("v2/")) return new Route { Kind = RV2, Key = name, Fallback = key, Sender = " · VRCFT v2 파라미터" };
            if (key == null) return new Route { Kind = RNone };
            var r = new Route { Kind = RKey, Key = key };
            if (slash <= 0)   // Babble 앱: "/mouthFunnel" (경로 없음)
            {
                r.Sender = " · Babble";
                if (key == "mouthFunnel" || key == "mouthPucker") r.Mul = 4;
                else if (key == "mouthLeft" || key == "mouthRight") r.Mul = 2;
            }
            return r;
        }

        void Message(Route r, float[] a, int count)
        {
            float v = a[0];
            switch (r.Kind)
            {
                case RGaze:
                {
                    // Baballonia 눈 값을 받고 있으면 "VRChat 기본 시선으로도 보내기"는 무시한다 (Baballonia는 그 순서가 왼 pitch·오른 yaw·오른 pitch·왼 yaw로 섞여 있다)
                    _gazeSeen++;
                    if (unchecked(Environment.TickCount - _eyeAt) < 1000) { _gazeIgnored++; return; }
                    // 도: pitch 아래 +, yaw 오른쪽 +
                    void Gaze(string side, float pitch, float yaw)
                    {
                        float x = Mathf.Clamp(yaw / 30f, -1, 1), y = Mathf.Clamp(-pitch / 25f, -1, 1);
                        bool left = side == "Left";
                        Signed(left ? x : -x, "eyeLookOut" + side, "eyeLookIn" + side);
                        Signed(y, "eyeLookDown" + side, "eyeLookUp" + side);
                    }
                    if (r.Key == "LeftRightPitchYaw" && count >= 4) { Gaze("Left", a[0], a[1]); Gaze("Right", a[2], a[3]); }
                    else if (r.Key == "CenterPitchYaw" && count >= 2) { Gaze("Left", a[0], a[1]); Gaze("Right", a[0], a[1]); }
                    else if (r.Key == "EyesClosedAmount") { Set("eyeBlinkLeft", v); Set("eyeBlinkRight", v); }
                    else return;
                    break;
                }
                case REye:
                    _eyeAt = Environment.TickCount;
                    switch (r.Key)
                    {
                        case "LeftEyeX": Signed(v, "eyeLookOutLeft", "eyeLookInLeft"); break;      // +X = 오른쪽 = 왼눈 안쪽 (VRCFT v2 EyeLeftX와 같다)
                        case "RightEyeX": Signed(v, "eyeLookInRight", "eyeLookOutRight"); break;
                        case "LeftEyeY": Signed(v, "eyeLookDownLeft", "eyeLookUpLeft"); break;
                        case "RightEyeY": Signed(v, "eyeLookDownRight", "eyeLookUpRight"); break;
                        case "EyesY": Signed(v, "eyeLookDownLeft", "eyeLookUpLeft"); Signed(v, "eyeLookDownRight", "eyeLookUpRight"); break;
                        case "LeftEyeLid": _lidL = v; Set("eyeBlinkLeft", 1 - v); break;
                        case "RightEyeLid": _lidR = v; Set("eyeBlinkRight", 1 - v); break;
                        case "LeftEyeWiden": Set("eyeWideLeft", v); break;
                        case "RightEyeWiden": Set("eyeWideRight", v); break;
                    }
                    break;
                case RV2:
                    if (V2(r.Key, v)) break;
                    if (r.Fallback == null) return;
                    Set(r.Fallback, v);
                    return;
                case RKey:
                    Set(r.Key, v * r.Mul);
                    if (r.Sender == null) return;
                    break;
            }
            _sender = r.Sender;
        }

        /// <summary>VRCFT v2의 합친 파라미터를 푼다 (FaceFt.V2의 반대). 다룬 이름이면 true.</summary>
        bool V2(string name, float v)
        {
            switch (name)
            {
                case "EyeLidLeft": Set("eyeBlinkLeft", 1 - v / 0.75f); Set("eyeWideLeft", (v - 0.75f) / 0.25f); return true;
                case "EyeLidRight": Set("eyeBlinkRight", 1 - v / 0.75f); Set("eyeWideRight", (v - 0.75f) / 0.25f); return true;
                case "EyeLid": Set("eyeBlinkLeft", 1 - v / 0.75f); Set("eyeBlinkRight", 1 - v / 0.75f); Set("eyeWideLeft", (v - 0.75f) / 0.25f); Set("eyeWideRight", (v - 0.75f) / 0.25f); return true;
                case "EyeLeftX": Signed(v, "eyeLookOutLeft", "eyeLookInLeft"); return true;
                case "EyeRightX": Signed(v, "eyeLookInRight", "eyeLookOutRight"); return true;
                case "EyeX": Signed(v, "eyeLookOutLeft", "eyeLookInLeft"); Signed(v, "eyeLookInRight", "eyeLookOutRight"); return true;
                case "EyeLeftY": Signed(v, "eyeLookDownLeft", "eyeLookUpLeft"); return true;
                case "EyeRightY": Signed(v, "eyeLookDownRight", "eyeLookUpRight"); return true;
                case "EyeY": Signed(v, "eyeLookDownLeft", "eyeLookUpLeft"); Signed(v, "eyeLookDownRight", "eyeLookUpRight"); return true;
                case "JawX": Signed(v, "jawLeft", "jawRight"); return true;
                case "JawZ": Set("jawForward", v); return true;
                case "MouthX": case "MouthUpperX": case "MouthLowerX": Signed(v, "mouthLeft", "mouthRight"); return true;
                case "SmileSadLeft": case "SmileFrownLeft": Signed(v, "mouthFrownLeft", "mouthSmileLeft"); return true;
                case "SmileSadRight": case "SmileFrownRight": Signed(v, "mouthFrownRight", "mouthSmileRight"); return true;
                case "SmileSad": case "SmileFrown": Signed(v, "mouthFrownLeft", "mouthSmileLeft"); Signed(v, "mouthFrownRight", "mouthSmileRight"); return true;
                case "CheekPuffSuckLeft": Set("cheekPuff", v); Set("cheekSuckLeft", -v); return true;
                case "CheekPuffSuckRight": Set("cheekPuff", v); Set("cheekSuckRight", -v); return true;
                case "BrowExpressionLeft": Set("browOuterUpLeft", v); Set("browInnerUp", v); Set("browDownLeft", -v); return true;
                case "BrowExpressionRight": Set("browOuterUpRight", v); Set("browInnerUp", v); Set("browDownRight", -v); return true;
                case "TongueX": Signed(v, "tongueLeft", "tongueRight"); return true;
                case "TongueY": Signed(v, "tongueDown", "tongueUp"); return true;
                case "MouthUpperUp": Set("mouthUpperUpLeft", v); Set("mouthUpperUpRight", v); return true;
                case "MouthLowerDown": Set("mouthLowerDownLeft", v); Set("mouthLowerDownRight", v); return true;
                case "NoseSneer": Set("noseSneerLeft", v); Set("noseSneerRight", v); return true;
                case "EyeSquint": Set("eyeSquintLeft", v); Set("eyeSquintRight", v); return true;
                case "BrowPinch": case "BrowLowerer": case "BrowDown": Set("browDownLeft", v); Set("browDownRight", v); return true;
            }
            return false;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct IntFloat { [System.Runtime.InteropServices.FieldOffset(0)] public int I; [System.Runtime.InteropServices.FieldOffset(0)] public float F; }
        static float BigFloat(byte[] b, int p) => new IntFloat { I = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3] }.F;

        public bool Poll(Dictionary<string, float> values)
        {
            lock (_lock)
            {
                if (_pending == 0) return false;
                _pending = 0;
                values.Clear();
                foreach (var kv in _cur) values[kv.Key] = kv.Value;
            }
            _lastAt = Time.unscaledTime;
            _frames++;
            if (Time.unscaledTime - _fpsAt >= 1) { _fps = _frames / (Time.unscaledTime - _fpsAt); _frames = 0; _fpsAt = Time.unscaledTime; }
            return true;
        }

        public void Dispose()
        {
            _running = false;
            try { _udp.Close(); } catch { }
        }
    }
}
