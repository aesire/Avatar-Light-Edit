// Unity 렌더 서버 프로세스의 실행·감시·재기동과 파이프 연결.
// 상태의 단일 원천은 Host이므로 Unity가 죽으면 다시 띄우고 Document로 복원한다 (MainWindow가 ready에서 처리).
// UI 프레임워크에 묶이지 않는다: Host가 UI 스레드로 보내는 post와 뷰포트(IUnityViewport)만 넘긴다.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.Host.Shared
{
    /// <summary>Unity가 -parentHWND로 붙을 빈 자식 창.</summary>
    public interface IUnityViewport
    {
        IntPtr HostHandle { get; }
        /// <summary>Unity 창을 찾으면 크기를 맞추고 true.</summary>
        bool FindUnityWindow();
        void ForgetUnityWindow();
    }

    public sealed class RenderHost : IRenderSink, IDisposable
    {
        Action<Action> _post;
        IUnityViewport _viewport;
        public string ExePath { get; }
        Process? _proc;
        PipeChannel? _channel;
        PipeChannel.Server? _server;
        CancellationTokenSource? _cts;
        bool _disposed;
        int _restarts; DateTime _restartWindow = DateTime.UtcNow;
        int _generation;

        public event Action<JNode>? Message;      // UI 스레드
        public event Action<string>? Status;      // UI 스레드
        public bool IsConnected => _channel?.IsOpen == true;
        public string LogPath { get; }

        public RenderHost(Action<Action> postToUi, IUnityViewport viewport, string exePath)
        {
            _post = postToUi; _viewport = viewport; ExePath = exePath;
            LogPath = Path.Combine(Path.GetTempPath(), "VrcProtoRenderer.log");
        }

        /// <summary>미리 띄운 렌더러를 실제 미리보기 창에 넘길 때 (AvHost Prestart): 이후 UI 스레드 전달과 뷰포트를 바꾼다. 다시 시작할 때도 새 뷰포트로.</summary>
        public void Rebind(Action<Action> postToUi, IUnityViewport viewport) { _post = postToUi; _viewport = viewport; }

        public static string? FindRenderer()
        {
            var env = Environment.GetEnvironmentVariable("VRCPROTO_RENDERER");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var dir = AppContext.BaseDirectory;
            foreach (var rel in new[] { "Renderer/VrcProtoRenderer.exe", "../../Unity/Build/VrcProtoRenderer.exe" }) // 배포 폴더 / 개발 중(bin/av)
            {
                var p = Path.GetFullPath(Path.Combine(dir, rel));
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public void Start()
        {
            if (_disposed) return;
            var name = PipeChannel.NewPipeName();
            _server = new PipeChannel.Server(name);
            _cts = new CancellationTokenSource();
            var accept = _server.AcceptAsync(_cts.Token);

            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(ExePath) ?? ".",
            };
            // -force-gfx-direct: 단일 스레드 렌더링 (VR 제출을 그린 바로 뒤에, VrView). -parentHWND로 붙이면 ProjectSettings m_MTRendering 0이어도
            // LegacyJobified로 돌았다 (2026-10-23 로그)
            foreach (var a in new[] { "-parentHWND", _viewport.HostHandle.ToInt64().ToString(), "-pipe", name, "-logFile", LogPath, "-screen-fullscreen", "0", "-force-d3d11", "-force-gfx-direct" })
                psi.ArgumentList.Add(a);
            RaiseStatus("미리보기 시작 중…");
            try
            {
                _proc = Process.Start(psi);
            }
            catch (Exception e) { RaiseStatus("미리보기 프로그램을 실행하지 못했습니다: " + e.Message); return; }
            if (_proc == null) return;
            _proc.EnableRaisingEvents = true;
            var proc = _proc;
            proc.Exited += (_, __) => _post(() => OnExited(proc));

            accept.ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                var ch = t.Result;
                ch.OnMessage += m => _post(() => Message?.Invoke(m));
                ch.OnClosed += e => _post(() => RaiseStatus(e == null ? "미리보기 연결이 끊어졌어요" : "미리보기 연결 오류: " + e.Message));
                _channel = ch;
                ch.Start();
                ch.Send(Msg.Make(Msg.Hello).Set("protocolVersion", Msg.ProtocolVersion));
            }, TaskScheduler.Default);

            // Unity 창이 생길 때까지 찾아서 크기를 맞춘다 (200ms 간격, 최대 30초)
            _ = FindWindowLoop(++_generation);
        }

        async Task FindWindowLoop(int gen)
        {
            for (int i = 0; i < 150 && gen == _generation && !_disposed; i++)
            {
                await Task.Delay(200).ConfigureAwait(false);
                var found = new TaskCompletionSource<bool>();
                _post(() => found.SetResult(_viewport.FindUnityWindow()));
                if (await found.Task.ConfigureAwait(false)) return;
            }
        }

        void OnExited(Process p)
        {
            if (p != _proc) return;
            _channel?.Dispose(); _channel = null;
            _cts?.Cancel(); _server?.Dispose(); _server = null;
            _viewport.ForgetUnityWindow();
            if (_disposed) return;
            if (DateTime.UtcNow - _restartWindow > TimeSpan.FromMinutes(1)) { _restartWindow = DateTime.UtcNow; _restarts = 0; }
            if (++_restarts > 3)
            {
                RaiseStatus($"미리보기 프로그램이 계속 꺼져요 (코드 {SafeExitCode(p)}). 로그: {LogPath}");
                return;
            }
            RaiseStatus($"미리보기 프로그램이 꺼졌어요 (코드 {SafeExitCode(p)}) — 다시 시작해요");
            Task.Delay(1000).ContinueWith(_ => _post(Start), TaskScheduler.Default);
        }

        static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

        /// <summary>사용자가 요청한 재시작 (렌더러 응답 없음 등).</summary>
        public void Restart()
        {
            _restarts = 0;
            if (_proc != null && !_proc.HasExited) { try { _proc.Kill(); } catch { } } // Exited → OnExited → Start
            else Start();
        }

        public void Send(JNode msg) => _channel?.Send(msg);

        void RaiseStatus(string s) => Status?.Invoke(s);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            try { _channel?.Send(Msg.Make(Msg.Quit)); } catch { }
            _channel?.Dispose();
            _cts?.Cancel(); _server?.Dispose();
            if (_proc != null && !_proc.HasExited)
            {
                if (!_proc.WaitForExit(2000)) { try { _proc.Kill(); } catch { } }
            }
        }
    }
}
