// 메인 창: 페이지 예열 (2026-10-24, 잔렉 줄이기).
//   페이지·탭은 모두 붙어 있고 보이기만 바뀐다(ShowPage, SubTabs.ShowView). 그래도 처음 보일 때 스타일·템플릿·배치를 하느라
//   블렌드쉐이프 페이지 1초, 깜빡임·입 맞추기 탭 0.4초, 머티리얼 섹션마다 0.1~0.3초씩 멈췄다 (큰 시험 장면, --test-pages 첫 바퀴).
//   ① 파일을 열 때(OpenIr) 모든 페이지와 탭을 투명하게 잠깐 보이게 해 배치까지 해 둔다 — 그동안 렌더러는 장면을 불러온다 (따로 프로세스).
//   ② 그 뒤로는 입력이 1초 넘게 멎으면 미룬 다시 그리기(Later)를 하나씩 하고, 다시 그려져 배치가 안 된 숨은 페이지를 예열한다.
using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        DateTime _lastInput = DateTime.Now;
        DispatcherTimer? _idle;

        /// <summary>입력이 있었던 때 (포인터·키). 끄는 중·누르는 중에는 예열하지 않는다</summary>
        void WarmSetup()
        {
            void Touch(object? _, RoutedEventArgs __) => _lastInput = DateTime.Now;
            AddHandler(PointerPressedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerReleasedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerMovedEvent, (s, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.GetCurrentPoint(this).Properties.IsRightButtonPressed) Touch(s, e); }, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(PointerWheelChangedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyDownEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
            _idle = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, __) => IdleTick());
            _idle.Start();
            // 페이지가 다시 그려질 만한 때만 숨은 페이지를 훑는다 (늘 훑으면 쉬는 동안에도 컨트롤 수만큼 돈다)
            _ctx.DocumentOpened += () => { _warmCheck = true; if (_ctx.Doc != null) _ctx.Doc.Changed += _ => _warmCheck = true; };
            _ctx.SelectionChanged += () => _warmCheck = true;
            _ctx.RendererInfoChanged += () => _warmCheck = true;
        }

        bool _warmCheck, _trimPending;
        string _warmDetail = "";   // 페이지마다 예열 시간·늘어난 관리 메모리 (로그·--test-timing)

        /// <summary>모든 페이지·탭 예열. 걸린 ms</summary>
        double WarmUp()
        {
            var sw = Stopwatch.StartNew();
            var parts = new System.Collections.Generic.List<string>();
            foreach (var (key, page) in _pages)
            {
                long m0 = GC.GetTotalMemory(false); var t0 = sw.Elapsed.TotalMilliseconds;
                WarmPage(page);
                parts.Add($"{key} {sw.Elapsed.TotalMilliseconds - t0:0}ms/{(GC.GetTotalMemory(false) - m0) / 1048576}MB/{page.GetVisualDescendants().Count()}개");
            }
            _warmDetail = string.Join(" · ", parts);
            return sw.Elapsed.TotalMilliseconds;
        }

        void WarmPage(Control page)
        {
            bool hidden = !page.IsVisible;
            if (hidden) { page.Opacity = 0; page.IsHitTestVisible = false; page.IsVisible = true; }
            UpdateLayout();
            foreach (var st in SubTabs.Inner(page).ToList()) st.WarmTabs(UpdateLayout);
            if (hidden) { page.IsVisible = false; page.Opacity = 1; page.IsHitTestVisible = true; }
        }

        /// <summary>
        /// 장면을 불러온 뒤 (sceneLoaded 다음): 미룬 다시 그리기를 모두 하고 남은 예열을 한다.
        /// 2026-11-12 (로딩 시간): 지금 보이는 페이지만 불러오기 단계 안에서 끝내고, 숨은 페이지·탭(머티리얼 탭 섹션 0.5~0.85초 등)은 바로 이어서 조각조각 한다 (WarmRest).
        ///   예전에는 전부 끝날 때까지 아바타가 이미 보이는데도 편집 창이 1초쯤 멈춰 있었다. 자동 시험(--ui-shots · --test-pages 등)은 예전처럼 한 번에 끝낸다.
        /// </summary>
        void FinishLoad()
        {
            var sw = Stopwatch.StartNew();
            double rebuilt = 0, pending = 0;
            var per = new System.Collections.Generic.Dictionary<string, double>();   // 페이지마다 예열 ms (2026-11-02, 어디가 큰지 보려고)
            bool all = _shotsDir != null || _args.Contains("--vr-panel-shots") || _args.Contains("--shapes-bench")
                || _args.Any(a => a.StartsWith("--test-") && a != "--test-timing" && a != "--test-heap");
            var shown = _side.Current;
            try
            {
                while (Later.RunOne()) { }
                rebuilt = sw.Elapsed.TotalMilliseconds;
                for (int i = 0; i < 500; i++)
                {
                    bool any = false;
                    foreach (var (key, page) in _pages)
                    {
                        if (key == "log") continue;
                        if (!all && key != shown) continue;   // 숨은 페이지는 WarmRest가
                        var t0 = sw.Elapsed.TotalMilliseconds;
                        if (!WarmStep(page)) continue;
                        per.TryGetValue(key, out var had);
                        per[key] = had + sw.Elapsed.TotalMilliseconds - t0;
                        any = true; break;
                    }
                    if (!any) break;
                }
                // 탭 막대 밖("기타" 메뉴) 섹션도 내용은 만들어 둔다 — 처음 열 때 만들기·스타일까지 하면 0.1~0.2초 (배치는 예전처럼 열 때)
                var p0 = sw.Elapsed.TotalMilliseconds;
                if (all) for (int i = 0; i < 200 && SubTabs.BuildOnePending(); i++) { }
                pending = sw.Elapsed.TotalMilliseconds - p0;
            }
            catch (Exception e) { AppendLog("warn", "편집 창 준비 중 오류: " + e.Message); }   // 예열이 실패해도 불러오기는 끝낸다
            _warmCheck = !all;
            _trimPending = !all;   // 다 준비되고 한동안 조용하면 한 번 (IdleTick)
            if (!all) WarmRest();
            var line = $"장면 뒤 편집 창 준비 {sw.Elapsed.TotalMilliseconds:0} ms (다시 그리기 {rebuilt:0} · 예열 {sw.Elapsed.TotalMilliseconds - rebuilt - pending:0} — "
                + string.Join(" · ", per.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0}")) + $" · 탭 밖 섹션 만들기 {pending:0})";
            _infoTiming += Environment.NewLine + line;
            AppendLog("info", line);
            SetStatus("준비됐어요. 편집 창에서 값을 바꾸면 미리보기에 바로 보여요.");
            BootDone();
        }

        // 남은 예열을 이어서 (2026-11-12): 한 번에 페이지 겉·탭 하나·탭 내용 하나씩 하고 화면에 넘겨준다. 입력이 있으면 0.3초 물러났다가 다시.
        //   쉬는 동안 하는 IdleTick(1초 뒤, 0.4초마다 하나)보다 훨씬 빨리 끝난다 — 보통 장면이 보인 뒤 1초 안. 문서가 바뀌면 그만둔다 (새 문서의 FinishLoad가 다시 시작).
        int _warmRun;
        void WarmRest()
        {
            int run = ++_warmRun;
            var doc = _ctx.Doc;
            var sw = Stopwatch.StartNew();
            double work = 0, longest = 0; int steps = 0;
            void Step()
            {
                if (run != _warmRun || doc == null || _ctx.Doc != doc) return;
                if ((DateTime.Now - _lastInput).TotalMilliseconds < 300)
                {
                    DispatcherTimer.RunOnce(Step, TimeSpan.FromMilliseconds(300), DispatcherPriority.Background);
                    return;
                }
                var t0 = sw.Elapsed.TotalMilliseconds;
                bool did = false;
                try
                {
                    if (Later.RunOne()) did = true;
                    else
                    {
                        foreach (var (key, page) in _pages)
                            if (key != "log" && WarmStep(page)) { did = true; break; }
                        if (!did) did = SubTabs.BuildOnePending();
                    }
                }
                catch (Exception e) { AppendLog("warn", "편집 창 준비 중 오류: " + e.Message); did = false; }
                work += sw.Elapsed.TotalMilliseconds - t0;
                longest = Math.Max(longest, sw.Elapsed.TotalMilliseconds - t0);
                if (did && ++steps < 2000) { Dispatcher.UIThread.Post(Step, DispatcherPriority.Background); return; }
                _warmCheck = false;
                var line = $"숨은 페이지·탭 준비 {work:0} ms ({steps}조각 · 가장 긴 조각 {longest:0} ms · 장면이 보인 뒤 {sw.Elapsed.TotalMilliseconds:0} ms에 끝남)";
                AppendLog("info", line);
                if (_restText != null)
                {
                    long priv = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576, heap = GC.GetTotalMemory(true) / 1048576;
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(Settings.Folder, "test-timing.txt"), _restText + Environment.NewLine + line + Environment.NewLine + $"다 준비된 뒤 Host 메모리 {priv} MB · 관리 힙(치운 뒤) {heap} MB · {TrimMemory()}"); } catch { }
                    _closeConfirmed = true;
                    Close();
                }
            }
            Dispatcher.UIThread.Post(Step, DispatcherPriority.Background);
        }

        /// <summary>
        /// 쓰지 않는 메모리를 Windows에 돌려준다 (2026-11-12, 램 줄이기). 불러오는 동안 잠깐 쓴 것(IR 글·JSON·임시 배열)은 치워도 프로세스가 계속 쥐고 있었다
        /// (치운 뒤 관리 힙 280 MB인데 프로세스는 520 MB). 0.1~0.3초 멈추므로 입력이 한동안 없을 때만 부른다 (IdleTick).
        /// </summary>
        string TrimMemory()
        {
            var sw = Stopwatch.StartNew();
            long before = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            long after = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;
            return $"메모리 돌려주기 {before} → {after} MB ({sw.Elapsed.TotalMilliseconds:0} ms)";
        }

        void IdleTick()
        {
            if (_ctx.Doc == null) return;
            var quiet = (DateTime.Now - _lastInput).TotalMilliseconds;
            // 미룬 다시 그리기는 0.4초만 멎어도 (메시를 고른 뒤 사이드바로 마우스를 옮기는 동안 끝나게), 예열은 1초. 한 번에 하나만
            if (quiet >= 400 && Later.RunOne()) { _warmCheck = true; return; }
            if (quiet < 1000) return;
            if (_warmCheck)
            {
                foreach (var (key, page) in _pages)
                    if (key != "log" && WarmStep(page)) return;   // 한 틱에 페이지 겉 또는 탭 하나 (로그는 글이 늘 붙어 늘 무효 — 보일 때 해도 짧다)
                _warmCheck = false;   // 다 예열됨
            }
            // 그다음 아직 만들지 않은 탭 내용 (머티리얼 "기타"·전문가 섹션 — 처음 열 때·찾기에서 멈추지 않게, 배치는 하지 않음)
            if (SubTabs.BuildOnePending()) return;
            // 준비가 다 끝나고 5초 넘게 조용하면 불러오는 동안 쓴 메모리를 한 번 돌려준다 (2026-11-12)
            if (_trimPending && quiet >= 5000)
            {
                _trimPending = false;
                AppendLog("info", TrimMemory());
            }
        }

        /// <summary>그 페이지에서 예열할 것 하나: 지금 보이는 부분(겉·지금 탭)이 배치 안 됐으면 그것, 아니면 숨은 탭 하나. 했으면 true</summary>
        bool WarmStep(Control page)
        {
            // 먼저 보이기를 건드리지 않고 찾는다 (페이지 보이기를 바꾸면 그 아래 모든 요소에 알림이 간다)
            bool own = DirtyBelow(page);
            var target = own ? null : SubTabs.Inner(page).FirstOrDefault(st => st.WarmOneDirty(null, DirtyBelow));
            if (!own && target == null) return false;
            bool hidden = !page.IsVisible;
            if (hidden) { page.Opacity = 0; page.IsHitTestVisible = false; page.IsVisible = true; }
            try
            {
                if (own) UpdateLayout();
                else target!.WarmOneDirty(UpdateLayout, DirtyBelow);
                return true;
            }
            finally { if (hidden) { page.IsVisible = false; page.Opacity = 1; page.IsHitTestVisible = true; } }
        }

        /// <summary>시험용: 배치가 안 된 채 남은 첫 요소의 경로 (예열이 끝나지 않을 때)</summary>
        internal string? FirstDirty()
        {
            string? Find(Visual v, string path)
            {
                foreach (var c in v.GetVisualChildren())
                {
                    var p = path + "/" + c.GetType().Name;
                    if (c is SubTabs st) { foreach (var view in st.Shown) if (Find(view, p + "[" + view.GetType().Name + "]") is string r) return r; continue; }
                    if (c is Control k && !k.IsVisible) continue;
                    if (c is Layoutable l && !l.IsMeasureValid) return p + $" (보임 {l.IsEffectivelyVisible}, 크기 {l.Bounds.Width:0}x{l.Bounds.Height:0})";
                    if (Find(c, p) is string r2) return r2;
                }
                return null;
            }
            foreach (var (key, page) in _pages) if (key != "log" && Find(page, key) is string r) return r;
            foreach (var (key, page) in _pages) if (key != "log" && SubTabs.Inner(page).Any(st => st.WarmOneDirty(null, DirtyBelow))) return key + " 의 숨은 탭";
            return null;
        }

        /// <summary>아래 보이는 것 중 배치가 안 된 것이 있는지 (숨은 탭은 SubTabs.WarmOneDirty가, 그 밖의 숨긴 것은 보일 때 하므로 빼고). 자기 자신은 보기 전환으로 늘 무효라 보지 않는다</summary>
        static bool DirtyBelow(Visual v)
        {
            foreach (var c in v.GetVisualChildren())
            {
                if (c is SubTabs st)
                {
                    foreach (var view in st.Shown) if (DirtyBelow(view)) return true;
                    continue;
                }
                if (c is Control k && !k.IsVisible) continue;
                if (c is Layoutable l && !l.IsMeasureValid) return true;
                if (DirtyBelow(c)) return true;
            }
            return false;
        }
    }
}
