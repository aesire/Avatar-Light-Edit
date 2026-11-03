// 메인 창: 충돌 안전장치 (2026-10-22) — 예외 알림, 지난번 충돌 알림, 자동 저장·복구.
//   자동 저장: 바꾼 값이 있으면 2분마다 설정 폴더 recovery/autosave.alesav에 세션을 따로 쓴다 (사용자 세션 파일은 건드리지 않음,
//   저장해도 "저장됨"으로 바꾸지 않음). 정상 종료·세션 저장·다른 파일 열기에서 지운다. 시작할 때 남아 있으면 복구할지 묻는다.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        static string RecoveryPath => Path.Combine(Settings.Folder, "recovery", "autosave" + Session.Extension);
        DispatcherTimer? _autoSave;
        bool _autoPending, _keepRecovery, _closeAsking;

        /// <summary>생성자 끝에서: 충돌 기록에 넣을 정보, 예외 알림, 자동 저장 타이머.</summary>
        void SetupSafety()
        {
            Crash.RecentLog = () => _logBuf.ToString();
            Crash.Context = () =>
            {
                var d = _ctx.Doc;
                if (d == null) return "열린 아바타: 없음";
                return $"열린 아바타: {d.Original.Name} · IR {d.IrPath}\n세션: {_sessionPath ?? "(저장 안 함)"} · 변경한 값 {d.Overrides.Count}개 · 저장되지 않음 {d.IsDirty}"
                    + $"\n렌더러 연결 {_ctx.Render?.IsConnected == true} · 페이지 {_side.Current}";
            };
            Crash.Recovered = (msg, file) =>
            {
                AppendLog("error", "처리하지 못한 오류 (계속함): " + msg + (file.Length > 0 ? " · 로그" + file : ""));
                SetStatus("오류가 있었지만 계속할 수 있습니다 · " + msg);
                if (_shotsDir != null) return;
                // 대화상자 안에서 또 예외가 나도 Crash가 다시 받는다 (같은 예외는 1분 안에 다시 띄우지 않음)
                Dispatcher.UIThread.Post(async () =>
                {
                    var r = await Dialog.Ask(this, "예기치 못한 오류가 있었어요",
                        msg + "\n\n앱은 계속 사용할 수 있습니다. 방금 하던 동작은 끝나지 않았을 수 있으니 결과를 확인해 주세요."
                        + (file.Length > 0 ? "\n\n로그를 저장했습니다. 버그를 제보하실 때 이 파일을 첨부해 주세요." : ""),
                        "확인", "기록 폴더 열기");
                    if (r == 1) Crash.OpenFolder(file);
                });
            };
            if (Settings.LoadError is string se)
            {
                AppendLog("warn", $"설정 파일을 읽지 못해 기본값으로 시작했어요 ({se}) · 깨진 파일: {Settings.FilePath}.broken");
                SetStatus("설정 파일이 깨져 기본 설정으로 시작했어요 (로그 참고).");
            }
            if (_shotsDir != null) return;
            _ctx.DocumentOpened += () => { if (_ctx.Doc != null) _ctx.Doc.Changed += _ => _autoPending = true; };
            _autoSave = new DispatcherTimer(TimeSpan.FromMinutes(2), DispatcherPriority.Background, (_, __) => AutoSave());
            _autoSave.Start();
        }

        /// <summary>바꾼 값이 있으면 복구용 세션을 쓴다 (원자적으로: 임시 파일 → 바꾸기).</summary>
        void AutoSave(bool force = false)
        {
            var d = _ctx.Doc;
            if (d == null || _shotsDir != null || !d.IsDirty || !(_autoPending || force)) return;
            try
            {
                var path = RecoveryPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var j = Session.ToJson(d, _ctx.Camera).Set("recoveryOf", _sessionPath ?? "").Set("savedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, j.ToJson(indent: true));
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
                _autoPending = false;
            }
            catch (Exception e) { AppendLog("warn", "자동 저장 실패: " + e.Message); }
        }

        /// <summary>복구용 세션을 지운다 (바꾼 값을 저장했거나 버리기로 했을 때)</summary>
        void ClearRecovery()
        {
            _autoPending = false;
            if (_shotsDir != null) return;
            try { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); } catch { }
        }

        /// <summary>창이 처음 열린 뒤: 복구 → 지난번 충돌 알림 → 시작 파일 열기.</summary>
        /// <summary>
        /// 배포본이면 설정 폴더에 AGENTS.md를 둔다 (2026-11-02): zip을 어디에 풀었는지 AI가 알 수 있게. 설정 폴더는 늘 같은 자리다. 화면에는 보이지 않는다.
        /// </summary>
        static void WriteAgentPointer()
        {
            var app = AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(app, "AGENTS.md"))) return;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(Path.Combine(Settings.Folder, "AGENTS.md"), $"Avatar Light Edit (ALEdit) 프로그램 폴더: {app}\n아바타 사진·영상·포즈·조명 작업은 그 폴더의 AGENTS.md를 읽으세요.\n");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
        }

        async Task StartupAsync()
        {
            WriteAgentPointer();
            // 렌더러를 먼저 넘겨받는다 (2026-10-23): 미리 띄운 렌더러는 창보다 먼저 준비되는데, 파일을 먼저 열면 페이지를 채우는 동안(1초쯤) 준비 알림이 밀려
            // 장면 불러오기가 늦게 시작했다. 넘겨받은 뒤 열면 OpenIr가 렌더러에 바로 보낸다. 미리보기 창이 안 생기면 3초까지만 기다린다
            await Task.WhenAny(_rendererStarted.Task, Task.Delay(3000));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);   // 넘겨받으며 쌓인 렌더러 메시지(ready)를 먼저 처리한다 (await 뒤가 그보다 먼저 돌았다)
            if (_shotsDir == null)
            {
                if (await OfferRecovery()) return;
                if (Crash.LastRunCrashed is string last)
                {
                    bool has = last.Length > 0;
                    AppendLog("warn", "지난 실행이 정상 종료하지 않았습니다" + (has ? " · 로그" + last : " (남은 기록 없음)"));
                    var r = await Dialog.Ask(this, "지난번에 에디터가 갑자기 꺼졌어요",
                        has ? "꺼질 때의 기록을 남겼어요. 버그를 알려 주실 때 이 파일을 붙여 주시면 원인을 찾는 데 도움이 돼요."
                            : "남은 오류 기록은 없어요. 미리보기 프로그램이나 Windows 쪽 문제였거나 강제로 끝낸 것일 수 있어요.",
                        has ? "기록 폴더 열기" : "확인", "닫기");
                    if (has && r == 0) Crash.OpenFolder(last);
                }
            }
            var f = _args.FirstOrDefault(File.Exists);
            if (f != null) OpenPath(f, ask: false);
            else if (_shotsDir == null) OpenLastFile();   // 켤 때 마지막 파일 (설정 "켤 때 마지막 파일 열기")
            if (TestEditDir() is string testEdit) await TestSessionEdit(testEdit);
            if (TestPresetDir() is string testPreset) await TestStudioPreset(testPreset);
            await TestUpdate();
            // 시험용 (테스트 모드만): --test-crash ui|async|task — 일부러 예외를 내 안전장치를 확인한다
            int i = Array.IndexOf(_args, "--test-crash");
            if (RunMode.Test && i >= 0 && i + 1 < _args.Length) TestCrash(_args[i + 1]);
            // 시험용 (테스트 모드만): --test-float — 가져온 파일 창과 자세 › 손 탭을 띄운다 (창 공유에 찍히는지 확인)
            if (RunMode.Test && _args.Contains("--test-float"))
            {
                await Task.Delay(1500);
                OpenFilesWindow();
                _side.Select("pose");
                await Task.Delay(300);
                _pages["pose"].GetVisualDescendants().OfType<SubTabs>().First().Float("hands");
            }
            // 시험용 (테스트 모드만): --test-inputbar num|text — 머티리얼 페이지의 숫자 칸·찾기 칸에 입력 바를 띄운다
            int ib = Array.IndexOf(_args, "--test-inputbar");
            if (RunMode.Test && ib >= 0)
            {
                await Task.Delay(1500);
                _side.Select("material");
                await Task.Delay(500);
                bool num = ib + 1 < _args.Length && _args[ib + 1] == "num";
                if (!num) { _side.Select("shapes"); await Task.Delay(500); }
                if (_pages[num ? "material" : "shapes"].GetVisualDescendants().OfType<TextBox>().FirstOrDefault(x => x.IsEffectivelyVisible && !x.IsReadOnly && InputBar.IsNumberField(x) == num) is TextBox tb) InputBar.Show(tb);
            }
            // 시험용 (테스트 모드만): --test-pinfloat — 블렌드쉐이프 묶음 창에 핀을 꽂고 다른 메시를 골라 본다
            if (RunMode.Test && _args.Contains("--test-pinfloat"))
            {
                await Task.Delay(1500);
                _side.Select("shapes");
                await Task.Delay(500);
                var r = await _blendPanel.TestPinnedFloat(rid => _ctx.Select(rid, null));
                AppendLog("info", "[pinfloat] " + r);
                try { File.WriteAllText(Path.Combine(Settings.Folder, "test-pinfloat.txt"), r); } catch { }
                SetStatus(r);
            }
            // 시험용 (테스트 모드만): --test-picker — 머티리얼 페이지 첫 드롭다운의 큰 선택 창을 띄운다 (모양 확인)
            if (RunMode.Test && (_args.Contains("--test-picker") || _args.Contains("--test-dropdown")))
            {
                await Task.Delay(1500);
                _side.Select("material");
                await Task.Delay(500);
                if (_pages["material"].GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(x => x.IsEffectivelyVisible && x.ItemCount > 1) is ComboBox cb) { if (_args.Contains("--test-dropdown")) cb.IsDropDownOpen = true; else FieldStyles.ShowPicker(cb); }
            }
        }

        void TestCrash(string kind)
        {
            AppendLog("info", "시험: 일부러 예외 내기 · " + kind);
            if (kind == "ui") Dispatcher.UIThread.Post(() => throw new InvalidOperationException("시험용 UI 예외"));
            else if (kind == "async") ThrowLater();
            else if (kind == "task") { _ = Task.Run(() => throw new InvalidOperationException("시험용 Task 예외")); Dispatcher.UIThread.Post(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }, DispatcherPriority.Background); }
            async void ThrowLater() { await Task.Delay(300); throw new InvalidOperationException("시험용 async void 예외"); }
        }

        /// <summary>남은 복구용 세션이 있으면 묻고, 복구했으면 true.</summary>
        async Task<bool> OfferRecovery()
        {
            var path = RecoveryPath;
            // 세션 확장자를 바꾸기 전(2026-11-03)의 자동 저장도 복구한다
            var old = Path.ChangeExtension(path, Session.OldExtension);
            if (!File.Exists(path) && File.Exists(old)) { try { File.Move(old, path); } catch (IOException) { } }
            if (!File.Exists(path)) return false;
            JNode j; Session.Loaded s;
            try { j = JNode.Parse(File.ReadAllText(path)); s = Session.FromJson(j, Path.GetDirectoryName(path)!); }
            catch (Exception e) { AppendLog("warn", "복구용 세션을 읽지 못해 삭제했습니다: " + e.Message); ClearRecovery(); return false; }
            var of = j["recoveryOf"].AsString;
            int n = s.Changeset.Items.Count();
            var r = await Dialog.Ask(this, "저장하지 않은 작업을 복구할까요?",
                $"지난번에 저장하지 않고 끝난 작업이 있습니다 ({j["savedAt"].AsString} 자동 저장, 변경한 값 {n}개).\n"
                + (of.Length > 0 ? "세션: " + Path.GetFileName(of) : "아바타: " + Path.GetFileName(s.IrPath))
                + "\n\n복구하면 그때 상태로 엽니다. 저장해야 세션 파일에 유지됩니다.",
                "복구", "버리기");
            if (r != 0) { ClearRecovery(); AppendLog("info", "복구용 세션을 버렸어요"); return false; }
            if (!File.Exists(s.IrPath)) { Error("복구하지 못했어요", "가져온 아바타 파일이 없습니다.\n" + s.IrPath); ClearRecovery(); return false; }
            if (!OpenIr(s.IrPath, s.Changeset, of.Length > 0 && File.Exists(of) ? of : null, s.Variants)) return false;
            _ctx.Camera = s.Camera;
            _ctx.Doc?.MarkUnsaved();
            _autoPending = true;
            AutoSave();   // OpenIr가 지운 복구 파일을 바로 다시 쓴다 (복구 직후 또 꺼져도 남게)
            RefreshTitle();
            SetStatus("저장하지 않은 작업을 복구했어요 · 저장하면 세션 파일에 남아요 (Ctrl+S)");
            return true;
        }

        async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            // Windows 종료·로그오프: 묻지 않고 복구용으로 저장해 두고 닫는다 (다음 실행 때 복구)
            if (e.CloseReason == WindowCloseReason.OSShutdown) { AutoSave(force: true); _keepRecovery = _ctx.Doc?.IsDirty == true; return; }
            // 화면 확인 모드는 시험이 값을 바꿔 둔 채 닫는다 — 보이지 않는 묻는 창에서 멈추지 않게 묻지 않는다
            if (_closeConfirmed || _shotsDir != null || _ctx.Doc?.IsDirty != true) return;
            e.Cancel = true;
            if (_closeAsking) return;   // 이미 묻는 중 (닫기를 여러 번 누름)
            _closeAsking = true;
            try
            {
                // 컨트롤 보드가 에디터를 숨겼거나 최소화된 채 닫으면 묻는 창이 보이지 않았다 → 먼저 보이게
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!await ConfirmDiscard()) return;
            }
            finally { _closeAsking = false; }
            _closeConfirmed = true;
            Close();
        }
    }
}
