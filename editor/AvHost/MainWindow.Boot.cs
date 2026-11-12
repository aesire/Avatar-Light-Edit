// 메인 창: 시작 시간 재기 (2026-10-22, 성능 개선) — 프로세스 시작부터 단계마다 몇 ms였는지 로그에 남긴다.
//   첫 장면이 보이면 한 줄로 "시작 시간: …". 테스트 모드 --test-timing이면 설정 폴더 test-timing.txt에 쓰고 끝낸다 (시작 속도를 견줄 때).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        static readonly DateTime ProcStart = Process.GetCurrentProcess().StartTime;
        readonly List<string> _boot = new List<string>();
        bool _bootDone;
        string _openTiming = "", _infoTiming = "";   // 페이지마다 걸린 시간 (OpenIr, sceneLoaded)
        string? _restText;   // --timing-rest: 장면이 보였을 때의 기록 (WarmRest가 끝나면 이어 적는다)

        void Boot(string step, DateTime? at = null)
        {
            if (_bootDone) return;
            // --test-heap (2026-11-12, 램 줄이기): 단계마다 쓰레기를 치운 뒤의 관리 힙(MB)을 함께 적는다. 치우느라 시간은 늘어나므로 시간을 잴 때는 빼고 돌린다
            string heap = RunMode.Test && _args.Contains("--test-heap") ? $" [{GC.GetTotalMemory(true) / 1048576}MB]" : "";
            _boot.Add($"{step} {((at ?? DateTime.Now) - ProcStart).TotalMilliseconds:0}{heap}");
        }

        /// <summary>첫 장면이 보였을 때 한 번: 로그에 남기고 (--test-timing이면) 파일로 쓰고 끝낸다</summary>
        void BootDone()
        {
            if (_bootDone) return;
            Boot("장면 보임");
            _bootDone = true;
            var line = "시작 시간 (ms, 프로세스 시작부터): " + string.Join(" · ", _boot);
            AppendLog("info", line);
            if (RunMode.Test && _args.Contains("--test-timing"))
            {
                long priv = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;
                long heap = GC.GetTotalMemory(true) / 1048576;   // 쓰레기를 치운 뒤 실제로 붙잡고 있는 관리 메모리
                var nl = Environment.NewLine;
                long privAfter = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;   // 치운 뒤에도 프로세스가 쥐고 있는 양 (2026-11-12)
                var text = line + nl + _openTiming + nl + _infoTiming + nl + $"Host 메모리 {priv} MB · 관리 힙(치운 뒤) {heap} MB · 치운 뒤 Host 메모리 {privAfter} MB";
                if (_args.Contains("--test-pages")) { PageTour(text); return; }
                // --timing-rest (2026-11-12): 숨은 페이지·탭 준비(WarmRest)가 끝날 때까지 기다렸다가 그 시간·메모리까지 적고 끝낸다
                if (_args.Contains("--timing-rest")) { _restText = text; return; }
                try { File.WriteAllText(Path.Combine(Settings.Folder, "test-timing.txt"), text); } catch { }
                _closeConfirmed = true;
                Close();
            }
        }

        /// <summary>
        /// 시험용 (--test-timing --test-pages): 장면이 보인 뒤 모든 페이지와 안쪽 탭을 차례로 열어 걸린 시간(배치까지)을 잰다 — 두 바퀴.
        /// 첫 바퀴는 처음 여는 탭(첫 클릭 렉), 둘째 바퀴는 다시 열 때. 30 ms를 넘는 것만 적는다.
        /// </summary>
        async void PageTour(string head)
        {
            var nl = Environment.NewLine;
            var lines = new List<string> { head };
            var keys = new[] { "avatar", "material", "shapes", "pose", "environment", "studio", "optimize", "upload", "log" };
            for (int lap = 1; lap <= 2; lap++)
            {
                var slow = new List<string>(); double total = 0, worst = 0;
                void Lap(string what, double ms) { total += ms; worst = Math.Max(worst, ms); if (ms >= 30) slow.Add($"{what} {ms:0}"); }
                foreach (var k in keys)
                {
                    await System.Threading.Tasks.Task.Delay(80);
                    _lastInput = DateTime.Now;   // 사람이 누르는 것처럼 (쉬는 동안 예열이 끼어들지 않게)
                    var sw = Stopwatch.StartNew();
                    _side.Select(k);
                    UpdateLayout();
                    Lap(k, sw.Elapsed.TotalMilliseconds);
                    foreach (var st in _pages[k].GetVisualDescendants().OfType<SubTabs>().ToList())
                        foreach (var t in st.Keys.Where(st.IsShown).ToList())
                        {
                            await System.Threading.Tasks.Task.Delay(40);
                            _lastInput = DateTime.Now;
                            sw.Restart();
                            st.Select(t);
                            UpdateLayout();
                            Lap(k + "/" + t, sw.Elapsed.TotalMilliseconds);
                        }
                }
                lines.Add($"페이지 돌아보기 {lap}바퀴: 합 {total:0} ms · 가장 긴 것 {worst:0} ms" + (slow.Count > 0 ? " — 30 ms 넘음: " + string.Join(" · ", slow) : ""));
            }

            // 메시 바꾸기: 블렌드쉐이프 페이지에서 다른 메시를 고른 순간 → 바로 머티리얼 페이지로 → 다시 블렌드쉐이프에서 메시를 바꾸고 쉰 뒤(예열) 머티리얼로
            if (_ctx.Doc is { } doc)
            {
                var others = doc.Original.Renderers.Where(r => r.Id != _ctx.SelectedRenderer && (doc.Original.FindMesh(r.Mesh)?.BlendShapes.Count ?? 0) > 0).Select(r => r).ToList();
                if (others.Count >= 2)
                {
                    double Step(Action a) { _lastInput = DateTime.Now; var sw = Stopwatch.StartNew(); a(); UpdateLayout(); return sw.Elapsed.TotalMilliseconds; }
                    await System.Threading.Tasks.Task.Delay(80);
                    Step(() => _side.Select("shapes"));
                    await System.Threading.Tasks.Task.Delay(80);
                    var pick1 = Step(() => _ctx.Select(others[0].Id, others[0].Materials.FirstOrDefault()));
                    await System.Threading.Tasks.Task.Delay(80);
                    var mat1 = Step(() => _side.Select("material"));
                    await System.Threading.Tasks.Task.Delay(80);
                    Step(() => _side.Select("shapes"));
                    await System.Threading.Tasks.Task.Delay(80);
                    var pick2 = Step(() => _ctx.Select(others[1].Id, others[1].Materials.FirstOrDefault()));
                    var idle = Stopwatch.StartNew();
                    while (Later.Pending || _warmCheck) { await System.Threading.Tasks.Task.Delay(100); if (idle.ElapsedMilliseconds > 15000) break; }
                    var idleMs = idle.ElapsedMilliseconds;
                    var mat2 = Step(() => _side.Select("material"));
                    if (_warmCheck) lines.Add("예열이 안 끝남: " + (FirstDirty() ?? "(찾지 못함)") + (Later.Pending ? " · 미룬 일 남음" : ""));
                    lines.Add($"메시 바꾸기: 고른 순간 {pick1:0} ms · 바로 머티리얼 {mat1:0} ms | 다시 고른 순간 {pick2:0} ms · 쉰 뒤({idleMs} ms 동안 예열) 머티리얼 {mat2:0} ms");
                }
            }
            lines.Add($"끝난 뒤 Host 메모리 {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB · 관리 힙(치운 뒤) {GC.GetTotalMemory(true) / 1048576} MB");
            try { File.WriteAllText(Path.Combine(Settings.Folder, "test-timing.txt"), string.Join(nl, lines)); } catch { }
            _closeConfirmed = true;
            Close();
        }
    }
}
