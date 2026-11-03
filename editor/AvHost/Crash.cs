// 충돌 안전장치 (2026-10-22): 처리하지 못한 예외에도 앱이 말없이 꺼지지 않게 하고, 무슨 일이 있었는지 파일로 남긴다.
//   · UI 스레드 예외 (async void 이벤트 처리기 포함 — 예외가 디스패처로 다시 던져진다): 기록하고 계속한다. 상태줄 + 대화상자 한 번
//   · 끝나지 않은 Task의 예외: 기록만 (관찰한 것으로 표시)
//   · 다른 스레드의 예외: 막을 수 없어 기록만 하고 꺼진다 → 다음 실행 때 알린다
//   기록: 설정 폴더 아래 crash/crash-<시각>.txt — 예외·스택·버전·최근 로그 줄·열린 아바타.
//   실행 중 표시(crash/running): 시작할 때 만들고 정상 종료에서 지운다. 다음 실행 때 남아 있으면 "지난번에 갑자기 꺼졌어요".
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace VrcProto.AvHost
{
    public static class Crash
    {
        public static string Folder => Path.Combine(Settings.Folder, "crash");
        static string RunningFlag => Path.Combine(Folder, "running");

        /// <summary>최근 로그 줄 (MainWindow 로그 페이지)</summary>
        public static Func<string>? RecentLog;
        /// <summary>열린 아바타·세션 한두 줄</summary>
        public static Func<string>? Context;
        /// <summary>UI 스레드 예외를 받아 계속할 때 (상태줄·대화상자). 메시지 한 줄 + 기록 파일</summary>
        public static Action<string, string>? Recovered;

        /// <summary>지난 실행이 정상 종료하지 않았으면 그때 남긴 기록 파일 (없으면 빈 문자열), 정상 종료였으면 null. Install에서 정해진다</summary>
        public static string? LastRunCrashed { get; private set; }

        static bool _installed, _quiet;
        static int _burst; static DateTime _burstStart = DateTime.UtcNow;
        static string _lastKey = ""; static DateTime _lastShown;
        static Exception? _lastWritten; static string? _lastPath;

        /// <param name="quiet">화면 확인 모드 등: 실행 중 표시를 남기지 않고 지난 충돌도 알리지 않는다</param>
        public static void Install(bool quiet)
        {
            if (_installed) return;
            _installed = true; _quiet = quiet;
            if (!quiet)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    if (File.Exists(RunningFlag))
                    {
                        var since = File.GetLastWriteTimeUtc(RunningFlag);
                        LastRunCrashed = Directory.GetFiles(Folder, "crash-*-fatal.txt").Where(f => File.GetLastWriteTimeUtc(f) >= since).OrderBy(f => f).LastOrDefault() ?? "";
                    }
                    File.WriteAllText(RunningFlag, Process.GetCurrentProcess().Id.ToString());
                }
                catch { }
            }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                // 여기까지 오면 프로세스는 끝난다 (UI 스레드가 아닌 곳의 예외). 기록만 남긴다
                Write("fatal", e.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("task", e.Exception);
                e.SetObserved();
            };
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                // 같은 예외가 잠깐 사이에 쏟아지면(그리기·배치 중 반복) 계속해도 쓸 수 없으므로 기록하고 끈다
                var now = DateTime.UtcNow;
                if (now - _burstStart > TimeSpan.FromSeconds(10)) { _burstStart = now; _burst = 0; }
                var file = Write(++_burst > 30 ? "fatal" : "ui", e.Exception);
                if (_burst > 30) return;
                e.Handled = true;
                var key = e.Exception.GetType().FullName + e.Exception.Message;
                bool show = key != _lastKey || now - _lastShown > TimeSpan.FromMinutes(1);
                _lastKey = key;
                if (show) { _lastShown = now; try { Recovered?.Invoke(Describe(e.Exception), file ?? ""); } catch { } }
            };
        }

        /// <summary>정상 종료: 실행 중 표시를 지운다.</summary>
        public static void CleanExit()
        {
            if (_quiet) return;
            try { File.Delete(RunningFlag); } catch { }
        }

        /// <summary>사용자에게 보일 한 줄 (예외 종류 + 메시지)</summary>
        public static string Describe(Exception e) => e.GetType().Name + ": " + e.Message;

        /// <summary>기록 파일을 쓰고 경로를 돌려준다 (쓰지 못하면 null). 기록 중 예외는 삼킨다 — 여기서 다시 터지면 안 된다.</summary>
        public static string? Write(string kind, Exception? e)
        {
            if (e != null && ReferenceEquals(e, _lastWritten)) return _lastPath;   // 같은 예외가 두 길로 올 때 (UI 처리기 → Main)
            _lastWritten = e;
            try
            {
                Directory.CreateDirectory(Folder);
                var now = DateTime.Now;
                var path = Path.Combine(Folder, $"crash-{now:yyyyMMdd-HHmmss-fff}-{kind}.txt");
                var sb = new StringBuilder();
                sb.Append("Avatar Light Edit 충돌 기록 (").Append(kind == "fatal" ? "앱이 꺼짐" : kind == "ui" ? "UI 예외 — 계속함" : "작업 예외 — 계속함").Append(")\n");
                sb.Append("시각: ").Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\n');
                sb.Append("버전: ").Append(Updater.CurrentText ?? "dev").Append(" · 빌드 ").Append(BuildTime()).Append('\n');
                sb.Append(".NET ").Append(Environment.Version).Append(" · ").Append(Environment.OSVersion).Append(" · 64비트 ").Append(Environment.Is64BitProcess).Append('\n');
                sb.Append("메모리: ").Append(Environment.WorkingSet / (1024 * 1024)).Append(" MB\n");
                try { if (Context?.Invoke() is string c && c.Length > 0) sb.Append(c.TrimEnd()).Append('\n'); } catch { }
                sb.Append("\n== 예외\n").Append(e?.ToString() ?? "(예외 정보 없음)").Append('\n');
                try
                {
                    if (RecentLog?.Invoke() is string log && log.Length > 0)
                    {
                        var lines = log.TrimEnd('\n').Split('\n');
                        sb.Append("\n== 최근 로그 (").Append(Math.Min(lines.Length, 80)).Append("줄)\n");
                        foreach (var l in lines.Skip(Math.Max(0, lines.Length - 80))) sb.Append(l).Append('\n');
                    }
                }
                catch { }
                File.WriteAllText(path, sb.ToString());
                Trim();
                return _lastPath = path;
            }
            catch { return null; }
        }

        static string BuildTime()
        {
            try { return File.GetLastWriteTime(typeof(Crash).Assembly.Location).ToString("yyyy-MM-dd HH:mm"); } catch { return "?"; }
        }

        /// <summary>기록은 최근 50개만 남긴다</summary>
        static void Trim()
        {
            var files = Directory.GetFiles(Folder, "crash-*.txt").OrderByDescending(f => f).Skip(50);
            foreach (var f in files) { try { File.Delete(f); } catch { } }
        }

        public static void OpenFolder(string? select = null)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (select != null && File.Exists(select)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{select}\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
