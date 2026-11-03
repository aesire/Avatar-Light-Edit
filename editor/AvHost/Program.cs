// Avalonia Host 진입점. Fluent 테마(기본 밀도) + Windows 라이트/다크·강조색 연동.
// UI와 무관한 편집 코어는 Host/Core(Document·RenderSync 등), 렌더러 실행·연결은 Host/Shared에 있다.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace VrcProto.AvHost
{
    public sealed class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            // VRCPROTO_THEME=light|dark 로 고정, 기본은 Windows 설정
            var theme = Environment.GetEnvironmentVariable("VRCPROTO_THEME")?.ToLowerInvariant();
            RequestedThemeVariant = theme == "light" ? ThemeVariant.Light : theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Default;

            // 설정 화면형 팔레트: 회색 바탕(Base) · 작업 창(Panel) · 흰 카드(Card). Fluent 테마 키와 겹치지 않게 앱 전용 키로 둔다
            ResetPalette();
            FieldStyles.Install(this);
            InputBar.Install();
            // 앱 로고를 모든 창의 아이콘으로 (작업 표시줄·Alt+Tab) — 2026-11-02
            using (var ico = Logo("ALEdit.ico"))
                if (ico != null) Styles.Add(new Style(x => x.Is<Window>()) { Setters = { new Setter(Window.IconProperty, new WindowIcon(ico)) } });
            if (PlatformSettings != null) PlatformSettings.ColorValuesChanged += (_, __) => { if (!_customAccent) ApplyAccent(); };
        }

        /// <summary>앱 로고 (editor/branding을 build.sh가 dll 리소스로 넣음): "ALEdit.ico", "ALEdit-64.png"</summary>
        public static System.IO.Stream? Logo(string name) => typeof(App).Assembly.GetManifestResourceStream(name);

        static ResourceDictionary Palette(bool dark)
        {
            SolidColorBrush B(uint argb) => new SolidColorBrush(Color.FromUInt32(argb));
            return dark
                ? new ResourceDictionary
                {
                    [Ui.KText] = B(0xFFFFFFFF), [Ui.KText2] = B(0xC5FFFFFF), [Ui.KText3] = B(0x87FFFFFF),
                    [Ui.KTitleBar] = B(0xFF24282F), [Ui.KToolbar] = B(0xFF282D36),
                    [Ui.KBase] = B(0xFF1B1B1F), [Ui.KPanel] = B(0xFF232328), [Ui.KCard] = B(0xFF2B2B31), [Ui.KCardStroke] = B(0xFF38383F),
                    [Ui.KDivider] = B(0xFF35353C), [Ui.KHover] = B(0x12FFFFFF), [Ui.KSelect] = B(0x1CFFFFFF), [Ui.KStroke] = B(0x33FFFFFF),
                    [Ui.KCaution] = B(0xFFFCE100), [Ui.KCautionBg] = B(0xFF433519), [Ui.KCritical] = B(0xFFFF99A4), [Ui.KSuccess] = B(0xFF6CCB5F),
                    // 입력 칸·드롭다운 (2026-10-22): 검은 칸 + 굵은 테두리 대신 카드 위에 옅게 밝힌 칸 + 가는 테두리. 반투명이라 테마 바탕색이 바뀌어도 어울린다
                    ["TextControlBackground"] = B(0x0FFFFFFF), ["TextControlBackgroundPointerOver"] = B(0x18FFFFFF), ["TextControlBackgroundFocused"] = B(0x33000000),
                    ["TextControlBorderBrush"] = B(0x1CFFFFFF), ["TextControlBorderBrushPointerOver"] = B(0x30FFFFFF), ["TextControlBackgroundDisabled"] = B(0x08FFFFFF), ["TextControlBorderBrushDisabled"] = B(0x10FFFFFF),
                    ["ComboBoxBackground"] = B(0x0FFFFFFF), ["ComboBoxBackgroundPointerOver"] = B(0x1AFFFFFF), ["ComboBoxBackgroundPressed"] = B(0x0AFFFFFF), ["ComboBoxBackgroundDisabled"] = B(0x08FFFFFF),
                    ["ComboBoxBorderBrush"] = B(0x1CFFFFFF), ["ComboBoxBorderBrushPointerOver"] = B(0x30FFFFFF), ["ComboBoxBorderBrushPressed"] = B(0x24FFFFFF), ["ComboBoxBorderBrushDisabled"] = B(0x10FFFFFF),
                    ["ComboBoxDropDownGlyphForeground"] = B(0x99FFFFFF), ["ComboBoxDropDownBorderBrush"] = B(0xFF38383F),
                    ["ComboBoxItemBackgroundPointerOver"] = B(0x14FFFFFF), ["ComboBoxItemBackgroundPressed"] = B(0x0CFFFFFF),
                    ["ComboBoxItemBackgroundSelected"] = B(0x1CFFFFFF), ["ComboBoxItemBackgroundSelectedPointerOver"] = B(0x2CFFFFFF), ["ComboBoxItemBackgroundSelectedPressed"] = B(0x1CFFFFFF),
                }
                : new ResourceDictionary
                {
                    [Ui.KText] = B(0xE4000000), [Ui.KText2] = B(0x9E000000), [Ui.KText3] = B(0x72000000),
                    [Ui.KTitleBar] = B(0xFFE6EBF3), [Ui.KToolbar] = B(0xFFEDF1F8),
                    [Ui.KBase] = B(0xFFF3F3F5), [Ui.KPanel] = B(0xFFFAFAFB), [Ui.KCard] = B(0xFFFFFFFF), [Ui.KCardStroke] = B(0xFFE3E3E8),
                    [Ui.KDivider] = B(0xFFEDEDF0), [Ui.KHover] = B(0x0A000000), [Ui.KSelect] = B(0x10000000), [Ui.KStroke] = B(0x24000000),
                    [Ui.KCaution] = B(0xFF9D5D00), [Ui.KCautionBg] = B(0xFFFFF4CE), [Ui.KCritical] = B(0xFFC42B1C), [Ui.KSuccess] = B(0xFF0F7B0F),
                    ["TextControlBackground"] = B(0x08000000), ["TextControlBackgroundPointerOver"] = B(0x0E000000), ["TextControlBackgroundFocused"] = B(0xFFFFFFFF),
                    ["TextControlBorderBrush"] = B(0x1C000000), ["TextControlBorderBrushPointerOver"] = B(0x30000000), ["TextControlBackgroundDisabled"] = B(0x05000000), ["TextControlBorderBrushDisabled"] = B(0x10000000),
                    ["ComboBoxBackground"] = B(0x08000000), ["ComboBoxBackgroundPointerOver"] = B(0x10000000), ["ComboBoxBackgroundPressed"] = B(0x05000000), ["ComboBoxBackgroundDisabled"] = B(0x05000000),
                    ["ComboBoxBorderBrush"] = B(0x1C000000), ["ComboBoxBorderBrushPointerOver"] = B(0x30000000), ["ComboBoxBorderBrushPressed"] = B(0x24000000), ["ComboBoxBorderBrushDisabled"] = B(0x10000000),
                    ["ComboBoxDropDownGlyphForeground"] = B(0x8C000000), ["ComboBoxDropDownBorderBrush"] = B(0xFFE3E3E8),
                    ["ComboBoxItemBackgroundPointerOver"] = B(0x0A000000), ["ComboBoxItemBackgroundPressed"] = B(0x06000000),
                    ["ComboBoxItemBackgroundSelected"] = B(0x10000000), ["ComboBoxItemBackgroundSelectedPointerOver"] = B(0x18000000), ["ComboBoxItemBackgroundSelectedPressed"] = B(0x0E000000),
                };
        }

        /// <summary>기본 팔레트로 되돌린다 (테마를 다시 칠하기 전에 — Theme.Apply).</summary>
        public void ResetPalette()
        {
            Resources.ThemeDictionaries[ThemeVariant.Dark] = Palette(dark: true);
            Resources.ThemeDictionaries[ThemeVariant.Light] = Palette(dark: false);
            ApplyAccent();
        }

        bool _customAccent;

        /// <summary>Windows 강조색. 다크에서는 밝은 변형을 글자색으로 쓴다 (WinUI와 같음).</summary>
        public void ApplyAccent()
        {
            _customAccent = false;
            var cv = PlatformSettings?.GetColorValues();
            SetAccentCore(cv?.AccentColor1 ?? Color.FromRgb(0x00, 0x78, 0xD4));
        }

        /// <summary>테마가 고른 강조색 (Windows 강조색이 바뀌어도 그대로)</summary>
        public void SetAccent(Color accent) { _customAccent = true; SetAccentCore(accent); }

        void SetAccentCore(Color accent)
        {
            Resources[Ui.KAccent] = new SolidColorBrush(accent);
            ((ResourceDictionary)Resources.ThemeDictionaries[ThemeVariant.Dark])[Ui.KAccentText] = new SolidColorBrush(Lerp(accent, Colors.White, 0.45));
            ((ResourceDictionary)Resources.ThemeDictionaries[ThemeVariant.Light])[Ui.KAccentText] = new SolidColorBrush(Lerp(accent, Colors.Black, 0.25));
        }

        static Color Lerp(Color a, Color b, double t)
            => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var args = RunMode.Setup(desktop.Args ?? Array.Empty<string>());
                Crash.Install(quiet: args.Contains("--ui-shots"));   // 설정 위치(테스트 모드)가 정해진 뒤
                // 메인 창을 닫으면 끝낸다 (기본값은 마지막 창: 떠 있는 창·다이얼 창이 남으면 프로세스가 살아 있었다)
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = new MainWindow(args);
            }
            base.OnFrameworkInitializationCompleted();
        }
    }

    /// <summary>
    /// 실행 방식. 보통 실행은 실행 파일 옆 texts/<언어>.tsv(빌드가 editor/texts에서 복사)를 글 표로 읽는다.
    /// --test: 테스트 모드 — 설정을 %APPDATA%\ALEdit\test\avhost.json에 따로 두고(평소 설정을 건드리지 않음), 창 제목에 [테스트],
    ///         글 표를 지켜보다 저장하면 바로 다시 읽는다 (Ctrl+Shift+R 다시 읽기, Ctrl+Shift+T 원문 ↔ 바꾼 글). run_test.sh / run_test.cmd가 이렇게 띄운다.
    /// --lang ko|en|ja: 화면 언어를 설정과 상관없이 이번만.
    /// --texts 경로: 글 표 파일이나 폴더 — 그 폴더의 언어별 표를 쓴다 (테스트 스크립트는 저장소의 editor/texts/ko.tsv를 바로 준다 → 고치면 빌드 없이 보인다).
    /// </summary>
    public static class RunMode
    {
        public static bool Test { get; private set; }

        /// <summary>실행 방식 인자를 처리하고 나머지(열 파일 등)를 돌려준다. 첫 창을 만들기 전에 불러야 한다 (글 바꾸기 훅·설정 위치).</summary>
        public static string[] Setup(string[] args)
        {
            var rest = new List<string>();
            string? texts = null, lang = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--test") Test = true;
                else if (args[i] == "--texts" && i + 1 < args.Length) texts = args[++i];
                else if (args[i] == "--lang" && i + 1 < args.Length) lang = args[++i];   // 화면 언어를 이번만 (ko · en · ja, 화면 찍기용)
                else rest.Add(args[i]);
            }
            if (Test) Settings.Profile = "test";
            // --texts는 표 파일이나 폴더 — 언어(설정 "화면 › 언어")에 맞는 표를 그 폴더에서 고른다 (2026-11-02)
            var folder = texts == null ? System.IO.Path.Combine(AppContext.BaseDirectory, "texts")
                : System.IO.Directory.Exists(texts) ? System.IO.Path.GetFullPath(texts) : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(texts))!;
            Texts.Init(folder, lang ?? Settings.Load().UiLang, watch: Test);
            return rest.ToArray();
        }
    }

    public static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

        [STAThread]
        public static int Main(string[] args)
        {
            if (!args.Contains("--ui-shots")) MoveOldFolders();   // 화면 확인은 사용자 폴더를 건드리지 않게
            if (args.Length > 0 && args[0] == "--build-shader-pack") return BuildShaderPack(args);
            if (args.Length > 0 && args[0] == "--build-project-shaders") return BuildProjectShaders(args);
            if (args.Length > 0 && args[0] == "--upload-prepare") return UploadPrepare(args);
            if (args.Length > 2 && args[0] == "--export") return ExportCli(args);
            // 경로가 너무 길면 네이티브 dll을 못 읽고 아무 말 없이 꺼진다 (2026-11-01, 렌더러 안쪽 파일은 앱 폴더보다 100자쯤 더 길다)
            if (AppContext.BaseDirectory.Length > 150)
            {
                MessageBoxW(IntPtr.Zero, "프로그램 폴더 경로가 너무 깁니다.\n\nC:\\ALEdit처럼 짧은 경로로 폴더를 이동한 뒤 다시 실행해 주세요.\n\n지금 위치: " + AppContext.BaseDirectory, "Avatar Light Edit", 0x10);
                return 1;
            }
            int code;
            // 멀티코어 JIT (2026-10-27, 시작 속도): 지난 실행에서 쓴 메서드를 다른 코어에서 미리 컴파일해 둔다 — 창·페이지를 처음 만들 때의 JIT 기다림이 준다.
            //   기록은 %LOCALAPPDATA%\ALEdit\jit (빌드가 바뀐 어셈블리는 런타임이 알아서 건너뛰고 다시 적는다)
            try
            {
                var jit = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "jit");
                System.IO.Directory.CreateDirectory(jit);
                System.Runtime.ProfileOptimization.SetProfileRoot(jit);
                System.Runtime.ProfileOptimization.StartProfile(args.Contains("--ui-shots") ? "shots.prof" : "editor.prof");
            }
            catch { }
            // 렌더러를 창보다 먼저 띄운다 (Prestart.cs) — 창이 뜨는 동안 Unity도 같이 켜진다. 시험용 --no-prestart로 끌 수 있다
            if (!args.Contains("--no-prestart")) { try { Prestart.Begin(); } catch (Exception e) { Crash.Write("prestart", e); } }
            // 팝업(드롭다운 목록·메뉴·툴팁)을 따로 뜬 창 대신 창 안에 그린다 — VR 에디터 창과 디스코드 "창" 공유에 함께 보이게 (설정 vrOverlayPopups, 다시 시작하면 적용)
            var win32 = new Win32PlatformOptions { OverlayPopups = Settings.Load().VrOverlayPopups };
            try { code = AppBuilder.Configure<App>().UsePlatformDetect().With(win32).LogToTrace().StartWithClassicDesktopLifetime(args); }
            catch (Exception e)
            {
                Crash.Write("fatal", e);
                // 창을 띄우기 전에 죽으면 아무것도 보이지 않는다 → 알린다 (2026-11-01). 화면 확인 모드는 묻지 않게
                if (!args.Contains("--ui-shots")) MessageBoxW(IntPtr.Zero, "프로그램을 시작하지 못했습니다.\n\n" + e.Message + "\n\n기록: " + Crash.Folder, "Avatar Light Edit", 0x10);
                throw;
            }
            Prestart.Dispose();   // 넘겨주지 못한 렌더러가 남아 있으면 (보통은 없다)
            Crash.CleanExit();
            // 창이 다 닫힌 뒤에도 남은 전경 스레드(장치·파이프 등)가 프로세스를 붙잡지 않게 여기서 끝낸다 ("종료가 안 된다", 2026-10-22)
            Environment.Exit(code);
            return code;
        }

        /// <summary>
        /// 이름 바꿈 (2026-11-02, VrcProto → ALEdit): 예전 설정(%APPDATA%\VrcProto)·데이터(%LOCALAPPDATA%\VrcProto) 폴더가 있고
        ///   새 폴더로 옮긴다. 새 폴더가 이미 있으면 거기 없는 항목만 하나씩 옮긴다 — 지난번에 일부를 못 옮겼어도(렌더러가 파일을 잡고 있는 등)
        ///   다음 실행에서 마저 옮긴다. 이름이 겹치는 항목은 새 쪽을 두고 예전 것은 남긴다. 다 옮기면 빈 예전 폴더를 지운다.
        /// </summary>
        static void MoveOldFolders()
        {
            foreach (var sf in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                try
                {
                    var root = Environment.GetFolderPath(sf);
                    string old = System.IO.Path.Combine(root, "VrcProto"), now = System.IO.Path.Combine(root, "ALEdit");
                    if (!System.IO.Directory.Exists(old)) continue;
                    if (!System.IO.Directory.Exists(now)) { System.IO.Directory.Move(old, now); continue; }
                    foreach (var e in System.IO.Directory.EnumerateFileSystemEntries(old).ToList())
                    {
                        var to = System.IO.Path.Combine(now, System.IO.Path.GetFileName(e));
                        if (System.IO.File.Exists(to) || System.IO.Directory.Exists(to)) continue;
                        try { if (System.IO.Directory.Exists(e)) System.IO.Directory.Move(e, to); else System.IO.File.Move(e, to); }
                        catch (Exception x) { Crash.Write("rename", x); }
                    }
                    if (!System.IO.Directory.EnumerateFileSystemEntries(old).Any()) System.IO.Directory.Delete(old);
                }
                catch (Exception e) { Crash.Write("rename", e); }
            }
        }

        /// <summary>
        /// 업로드 준비를 창 없이 (시험용): 프로젝트 만들기 → SDK·기믹 설치 → 원본 가져오기 → 변경 적용. 로그인·업로드는 하지 않는다.
        ///   dotnet VrcProtoAv.dll --upload-prepare &lt;x.ir.json | 세션.alesav&gt; [--gimmicks ma,liltoon] [--weld mm] [--project 폴더] [--open]
        /// </summary>
        static int UploadPrepare(string[] args)
        {
            string? Opt(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var path = args[1];
            VrcProto.Host.Core.Document doc;
            if (path.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase)) doc = new VrcProto.Host.Core.Document(VrcProto.Protocol.Ir.IrScene.Load(path), path);
            else
            {
                var sess = VrcProto.Host.Core.Session.Read(path);
                doc = new VrcProto.Host.Core.Document(VrcProto.Protocol.Ir.IrScene.Load(sess.IrPath), sess.IrPath);
                doc.LoadChangeset(sess.Changeset);
            }
            if (Opt("--weld") is string mm)
            {
                var me = new VrcProto.Host.Core.MeshEdits(doc);
                var (dv, dt) = me.WeldAll(me.MeshIds(), float.Parse(mm, System.Globalization.CultureInfo.InvariantCulture) / 1000f, true, true);
                Console.WriteLine($"정점 합치기: 정점 −{dv}, 삼각형 −{dt}");
            }
            var gimmicks = (Opt("--gimmicks") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var guid = doc.Original.Raw["source"]["guid"].AsString;
            var project = Opt("--project") ?? Upload.ProjectFor(guid.Length > 0 ? guid : doc.Original.Name);
            Console.WriteLine("프로젝트: " + project + " · 기믹: " + (gimmicks.Count > 0 ? string.Join(",", gimmicks) : "(없음)"));
            try
            {
                var unity = Upload.Prepare(doc, project, doc.Original.Name, gimmicks, null, Console.WriteLine,
                    (i, st, note) => { if (st >= 2) Console.WriteLine($"[{(st == 2 ? "ok" : "FAIL")}] 단계 {i + 1}" + (note != null ? " · " + note : "")); }).GetAwaiter().GetResult();
                if (args.Contains("--open")) Upload.OpenUnity(unity, project);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("실패: " + e.Message); return 1; }
        }

        /// <summary>
        /// 기존 Unity 프로젝트로 내보내기를 창 없이 (시험용). 원본에 눈 깜빡임 보정이 있으면 에디터의 깜빡임 맞추기를 뺀다 (창의 경고 대신 출력).
        ///   dotnet VrcProtoAv.dll --export &lt;x.ir.json | 세션.alesav&gt; &lt;Unity 프로젝트&gt; [--name 이름] [--blinkfix] [--keep-blinkfix]
        ///   --mouthline: 시험용으로 mouth_straight 다문 입 다듬기 (끝 30% · 가운데 130% · 끝 올리기 0.8mm)
        ///   --blinkfix: 시험용으로 블렌드쉐이프가 가장 많은 메시에 깜빡임 맞추기를 넣는다 (blink 이름 → 첫 다른 블렌드쉐이프 50%)
        /// </summary>
        static int ExportCli(string[] args)
        {
            string? Opt(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var path = args[1];
            VrcProto.Host.Core.Document doc;
            if (path.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase)) doc = new VrcProto.Host.Core.Document(VrcProto.Protocol.Ir.IrScene.Load(path), path);
            else
            {
                var sess = VrcProto.Host.Core.Session.Read(path);
                doc = new VrcProto.Host.Core.Document(VrcProto.Protocol.Ir.IrScene.Load(sess.IrPath), sess.IrPath);
                doc.LoadChangeset(sess.Changeset);
            }
            if (args.Contains("--mouthline"))
            {
                // 시험용: 블렌드쉐이프가 가장 많은 메시의 mouth_straight를 기본 값으로 다듬는다
                var m = doc.Original.Meshes.OrderByDescending(x => x.BlendShapes.Count).First();
                var md = new VrcProto.Host.Core.MeshEdits(doc).Original(m.Id)!;
                var op = VrcProto.Protocol.MeshOps.MakeMouthLine(md, "mouth_straight", 0.3f, 1.3f, 0.0008f);
                if (op != null) { doc.Set(VrcProto.Host.Core.Addr.MeshEdit(m.Id), VrcProto.Host.Core.BlinkFixPlan.Ordered(JNodeArr(op, doc.Get(VrcProto.Host.Core.Addr.MeshEdit(m.Id)))), "시험"); Console.WriteLine($"다문 입 다듬기 (시험): {m.Name} 점 {op["points"].Count / 6}개"); }
            }
            if (args.Contains("--blinkfix"))
            {
                var m = doc.Original.Meshes.OrderByDescending(x => x.BlendShapes.Count).First();
                var names = m.BlendShapes.Select(b => b.Name).ToList();
                var targets = names.Where(n => n.IndexOf("blink", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var src = names.First(n => !targets.Contains(n));
                VrcProto.Host.Core.BlinkFixPlan.Set(doc, m.Id, VrcProto.Protocol.MeshOps.MakeBlinkFix(targets, new[] { (src, 0.5f) }), "시험");
                Console.WriteLine($"깜빡임 맞추기 (시험): {m.Name} 깜빡임 {targets.Count}개, 눈 편집 {src}");
            }
            var existing = VrcProto.Host.Core.UploadPlan.ExistingBlinkFix(doc.Original);
            bool exclude = existing.Count > 0 && VrcProto.Host.Core.UploadPlan.UsesBlinkFix(doc) && !args.Contains("--keep-blinkfix");
            bool excludeMouth = VrcProto.Host.Core.UploadPlan.ExistingMouthFix(doc.Original) && VrcProto.Host.Core.UploadPlan.UsesBlinkFix(doc, VrcProto.Protocol.MeshOps.MouthFix) && !args.Contains("--keep-blinkfix");
            if (existing.Count > 0) Console.WriteLine("원본의 눈 깜빡임 보정: " + string.Join(", ", existing) + (exclude ? " → 에디터의 Blink Fix를 뺌" : ""));
            try
            {
                var st = Upload.Export(doc, args[2], Opt("--name") ?? doc.Original.Name, exclude, Console.WriteLine,
                    (i, s, note) => { if (s >= 2) Console.WriteLine($"[{(s == 2 ? "ok" : "FAIL")}] 단계 {i + 1}" + (note != null ? " · " + note : "")); },
                    System.Threading.CancellationToken.None, excludeMouth).GetAwaiter().GetResult();
                Console.WriteLine("장면: " + st["scene"].AsString + " · Unity가 찾은 기존 보정: " + string.Join(", ", st["existingBlinkFix"].Items.Select(x => x.AsString)) + " · 뺀 메시: " + st["blinkFixSkipped"].AsInt);
                return st["ok"].AsBool ? 0 : 2;
            }
            catch (Exception e) { Console.Error.WriteLine("실패: " + e.Message); return 1; }
        }

        static VrcProto.Protocol.JNode JNodeArr(VrcProto.Protocol.JNode first, VrcProto.Protocol.JNode rest)
        {
            var a = VrcProto.Protocol.JNode.Arr().Add(first);
            foreach (var x in rest.Items) a.Add(x);
            return a;
        }

        /// <summary>
        /// 가져오기 후 자동 제안과 같은 경로를 창 없이: IR의 source.project에서 머티리얼 셰이더 원본을 찾아 팩을 만든다.
        ///   dotnet VrcProtoAv.dll --build-project-shaders &lt;x.ir.json&gt; [셰이더GUID ...]  (GUID를 안 주면 lilToon 패키지 밖의 셰이더 전부)
        /// </summary>
        static int BuildProjectShaders(string[] args)
        {
            var scene = VrcProto.Protocol.Ir.IrScene.Load(args[1]);
            var project = scene.Raw["source"]["project"].AsStringOrNull;
            if (string.IsNullOrEmpty(project)) { Console.Error.WriteLine("IR에 source.project 가 없음 (Unity 씬·프리팹 가져오기로 만든 IR이 아님)"); return 1; }
            var guids = args.Length > 2 ? args.Skip(2).ToList() : scene.Materials.Select(m => m.ShaderGuid).Where(g => g.Length == 32).Distinct().ToList();
            var found = ImportSim.UnityImport.FindShaderSources(project, guids);
            if (args.Length <= 2) foreach (var k in found.Where(kv => kv.Value.Root.Contains("jp.lilxyzw.liltoon")).Select(kv => kv.Key).ToList()) found.Remove(k);
            foreach (var kv in found) Console.WriteLine($"  {kv.Key}  {kv.Value.Shader}  (범위 {kv.Value.Root})");
            if (found.Count == 0) { Console.WriteLine("찾은 셰이더 없음"); return 0; }
            var src = ShaderPacks.ScanProject(found);
            Console.WriteLine($"셰이더 {src.Shaders.Count}개, 파일 {src.Files.Count}개, 원본 머티리얼 {src.Materials.Count}개");
            var unity = ShaderPacks.FindUnity("2022.3.22f1");
            if (unity == null) { Console.Error.WriteLine("Unity를 찾지 못함"); return 2; }
            var name = "project_" + string.Join("_", src.Shaders.Select(s => s.Name.Split('/').Last())).Replace(' ', '_');
            if (name.Length > 60) name = name.Substring(0, 60).TrimEnd('_');
            var r = ShaderPacks.Build(src, name, ShaderPacks.AvatarMaterials(scene, src), unity, Console.WriteLine).GetAwaiter().GetResult();
            foreach (var s in r.Compiled) Console.WriteLine("ok    " + s);
            foreach (var e in r.Errors) Console.WriteLine("error " + e);
            Console.WriteLine(r.Ok ? $"팩 생성: {name}" : "실패");
            return r.Ok ? 0 : 3;
        }

        /// <summary>
        /// 창 없이 셰이더 팩 만들기 (시험·자동화·배포용 기본 팩):
        ///   dotnet VrcProtoAv.dll --build-shader-pack &lt;.unitypackage|폴더&gt; [--name 이름] [--ir 아바타.ir.json] [--unity 2022.3.22f1]
        ///                         [--only "셰이더 이름;…"] [--out 폴더] [--spec 변형목록]
        /// </summary>
        static int BuildShaderPack(string[] args)
        {
            string? Opt(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            if (args.Length < 2) { Console.Error.WriteLine("사용: --build-shader-pack <.unitypackage|폴더> [--name 이름] [--ir x.ir.json] [--unity 버전] [--only \"이름;…\"] [--out 폴더] [--spec 변형목록]"); return 1; }
            var only = Opt("--only") is string o ? new HashSet<string>(o.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : null;
            var src = ShaderPacks.Scan(args[1], onlyNames: only);
            if (only != null) foreach (var n in only.Where(n => !src.Shaders.Any(s => s.Name == n))) Console.Error.WriteLine("  (원본에 없음) " + n);
            Console.WriteLine($"셰이더 {src.Shaders.Count}개, 파일 {src.Files.Count}개, 원본 머티리얼 {src.Materials.Count}개");
            foreach (var s in src.Shaders) Console.WriteLine($"  {s.Guid}  {s.Name}");
            var unity = ShaderPacks.FindUnity(Opt("--unity") ?? "2022.3.22f1");
            if (unity == null) { Console.Error.WriteLine("Unity를 찾지 못함 (UNITY_EXE)"); return 2; }
            var ir = Opt("--ir");
            var extra = ShaderPacks.AvatarMaterials(ir != null ? VrcProto.Protocol.Ir.IrScene.Load(ir) : null, src);
            var name = Opt("--name") ?? System.IO.Path.GetFileNameWithoutExtension(args[1].TrimEnd('\\', '/'));
            var outDir = Opt("--out") ?? ShaderPacks.PackDir;
            if (Opt("--spec") != null) src.Materials.Clear(); // 변형은 목록대로만 (원본 패키지의 예제 머티리얼로 팩이 커지지 않게)
            var r = ShaderPacks.Build(src, name, extra, unity, Console.WriteLine, outDir, Opt("--spec")).GetAwaiter().GetResult();
            foreach (var s in r.Compiled) Console.WriteLine("ok    " + s);
            foreach (var e in r.Errors) Console.WriteLine("error " + e);
            Console.WriteLine(r.Ok ? $"팩 생성: {System.IO.Path.Combine(outDir, name)}.bundle (아바타 머티리얼 {extra.Count}개 포함)" : "실패");
            return r.Ok ? 0 : 3;
        }
    }
}
