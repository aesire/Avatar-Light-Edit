// 메인 창: 문서 열기 · Unity 가져오기 · 세션 저장 · 셰이더 팩, 제목·실행 취소 상태.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VrcProto.Host.Shared;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        // ---------------- 문서 ----------------
        /// <summary>
        /// 이 파일을 놓거나 열면 무엇을 하는지 (2026-10-27): 끌어다 놓을 때 미리 알려 주고, 저장 질문보다 먼저 가린다.
        /// 세션 없는 PNG·JPG·HDR = 스튜디오 배경, .anim = 자세. 아바타가 없을 때 이 둘은 null.
        /// </summary>
        string? OpenPathAction(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool doc = _ctx.Doc != null;
            if (StudioPreset.IsPresetFile(path)) return "스튜디오 세팅 불러오기";   // .json보다 먼저
            if (Session.IsSessionFile(path)) return "세션 열기";
            if (SessionPack.IsPack(path)) return "파일까지 담은 세션 열기";
            if (Packages.IsPackage(path)) return doc ? "패키지 붙이기·가져오기" : "패키지 가져오기";
            if (ImportSim.UnityImport.CanImport(path)) return "Unity 씬·프리팹 가져오기";
            if (path.EndsWith(".ir.json", StringComparison.OrdinalIgnoreCase) || ext == ".json") return "가져온 파일 열기";
            if (ext == ".png" && SafeHasSession(path)) return "이미지에 넣은 세션 열기";
            if (ext is ".png" or ".jpg" or ".jpeg" or ".hdr") return doc ? "스튜디오 배경으로 쓰기" : null;
            if (ext == ".anim") return doc ? "자세로 불러오기" : null;
            return null;
        }

        static bool SafeHasSession(string path) { try { return PngText.ReadSession(path) != null; } catch { return false; } }

        async void OpenPath(string path, bool ask = true)
        {
            if (OpenPathAction(path) is not string act)
            {
                Error("이 파일은 열 수 없어요", Path.GetFileName(path) + "\n\n아바타·세션(.alesav·.alexp)·Unity 씬·프리팹·패키지·가져온 파일을 열 수 있습니다."
                    + (_ctx.Doc == null ? "\n이미지(배경)와 .anim(자세)은 아바타를 먼저 연 다음에 놓아 주세요." : "\n이미지는 스튜디오 배경, .anim은 자세로 사용할 수 있습니다."));
                return;
            }
            if (act == "스튜디오 세팅 불러오기") { _side.Select("studio"); _studioPanel?.LoadPreset(path); return; }   // 아바타 값이 아니라 저장을 묻지 않는다
            // 지금 아바타에 더하는 것은 저장을 묻지 않는다
            if (act == "스튜디오 배경으로 쓰기") { _side.Select("studio"); _studioPanel?.UseBackground(path); SetStatus("스튜디오 배경으로 사용했습니다 · " + Path.GetFileName(path)); return; }
            if (act == "자세로 불러오기") { _side.Select("pose"); _posePanel?.LoadAnim(path); return; }
            if (ask && !await ConfirmDiscard()) return;
            if (Session.IsSessionFile(path)) OpenSession(path);
            else if (SessionPack.IsPack(path)) OpenPack(path);
            else if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) OpenPngSession(path);
            else if (Packages.IsPackage(path)) { OnPackagesDropped(new[] { path }); }
            else if (ImportSim.UnityImport.CanImport(path)) ImportUnity(path);
            else OpenIr(path, null, null);
        }

        // 저장할지는 파일을 고른 뒤에 묻는다 (2026-10-27: 고르기를 취소해도 먼저 물어 번거로웠다)
        async void ImportUnityDialog()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("Unity 씬·프리팹·패키지 가져오기 (패키지는 여러 개 고를 수 있어요)"), AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Unity 씬·프리팹·패키지") { Patterns = new[] { "*.unity", "*.prefab", "*.unitypackage", "*.zip" } } },
            });
            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
            if (paths.Length == 0 || !await ConfirmDiscard()) return;
            // 패키지는 여러 개를 함께 (아바타 + 머티리얼·셰이더 패키지), 씬·프리팹은 하나만
            var pkgs = paths.Where(Packages.IsPackage).ToArray();
            if (pkgs.Length > 0) { if (await Unzip(pkgs) is string[] got) ImportUnity(got); return; }
            ImportUnity(paths[0]);
        }

        readonly HashSet<string> _shaderOffered = new HashSet<string>();   // 이번 실행에서 이미 물어본 셰이더 GUID (다시 묻지 않음)
        bool _packBuilding;

        /// <summary>
        /// 미리보기에 없는 셰이더가 원본 프로젝트(Assets·Packages·Library/PackageCache)에 있으면 셰이더 팩으로 만들지 묻는다.
        /// Unity에서 제대로 보이던 아바타는 셰이더 원본이 프로젝트에 반드시 있으므로, 이것으로 대부분 맞춰진다.
        /// 프로젝트 위치는 가져오기가 IR에 남긴 source.project (Unity 씬·프리팹 가져오기로 연 IR만).
        /// </summary>
        async void OfferProjectShaders(JNode loaded)
        {
            var doc = _ctx.Doc;
            var project = doc?.Original.Raw["source"]["project"].AsStringOrNull;
            if (doc == null || _packBuilding || string.IsNullOrEmpty(project) || !Directory.Exists(project)) return;
            // 미지원 + 근사 표시(기본 팩의 가까운 키워드 조합, 잠긴 셰이더를 원본으로 대신 그림). 원본 프로젝트의 셰이더로 만든 팩이 정확하다
            var items = loaded["materials"].Items.Where(x => !x["supported"].AsBool || x["approx"].AsBool).ToList();
            bool onlyApprox = items.All(x => x["supported"].AsBool);
            var missing = items.Select(x => doc.Original.FindMaterial(x["guid"].AsString)?.ShaderGuid ?? "")
                .Where(g => g.Length == 32 && _shaderOffered.Add(g)).Distinct().ToList();
            if (missing.Count == 0) return;

            SetStatus("미리보기에 없는 셰이더를 원본 프로젝트에서 찾는 중…");
            Dictionary<string, (string Shader, string Root)> found;
            ShaderPacks.Source src;
            try
            {
                found = await Task.Run(() => ImportSim.UnityImport.FindShaderSources(project, missing));
                if (found.Count == 0) { if (!onlyApprox) SetStatus($"미지원 셰이더 {missing.Count}개는 원본 프로젝트에서도 찾지 못했어요."); return; }
                src = await Task.Run(() => ShaderPacks.ScanProject(found));
            }
            catch (Exception e) { AppendLog("warn", "셰이더 원본 찾기 실패: " + e.Message); return; }
            var unity = ShaderPacks.FindUnity(_unityVersion);
            var names = string.Join("\n", src.Shaders.Select(s => "· " + s.Name));
            AppendLog("info", $"원본 프로젝트에서 찾은 셰이더 {src.Shaders.Count}개: " + string.Join(", ", src.Shaders.Select(s => s.Name)));
            if (unity == null)
            {
                // 근사 표시만 있을 때는 Unity 없이도 볼 만하므로 조용히 둔다
                if (!onlyApprox) SetStatus($"셰이더 팩을 만들려면 Unity {_unityVersion}가 필요해요 (UNITY_EXE).");
                return;
            }
            var r = await Dialog.Ask(this, "원본 프로젝트의 셰이더로 맞출까요?",
                (onlyApprox
                    ? $"이 아바타의 셰이더 {src.Shaders.Count}개는 기본 셰이더 팩으로 비슷하게 보이고 있습니다 (일부 기능이 빠질 수 있음).\n{names}\n\n"
                    : $"이 아바타가 사용하는 셰이더 {src.Shaders.Count}개가 미리보기에 없지만 원본 프로젝트에 있습니다.\n{names}\n\n")
                + $"이 PC의 Unity {_unityVersion}로 컴파일해 셰이더 팩을 만들면 Unity에서처럼 표시됩니다. 몇 분 걸릴 수 있고, 셰이더 파일만 읽습니다.",
                "셰이더 팩 만들기", "나중에");
            if (r != 0) { SetStatus("나중에 열기 메뉴의 \"셰이더 팩 추가…\"로 만들 수 있어요."); return; }

            _packBuilding = true;
            _sceneItem.Set(StatusItem.State.Busy, "셰이더 팩 만드는 중");
            var name = "project_" + string.Join("_", src.Shaders.Select(s => s.Name.Split('/').Last())).Replace(' ', '_');
            if (name.Length > 60) name = name.Substring(0, 60).TrimEnd('_');
            ShaderPacks.Result result;
            try
            {
                var extra = ShaderPacks.AvatarMaterials(doc.Original, src);
                result = await Task.Run(() => ShaderPacks.Build(src, name, extra, unity, s => Dispatcher.UIThread.Post(() => SetStatus(s))));
            }
            catch (Exception e) { _packBuilding = false; Error("셰이더 팩을 만들지 못했어요", e.Message); return; }
            _packBuilding = false;
            foreach (var e in result.Errors) AppendLog("warn", "셰이더 팩: " + e);
            if (!result.Ok) { Error("셰이더 팩을 만들지 못했어요", string.Join("\n", result.Errors.Take(5))); return; }
            AppendLog("info", $"셰이더 팩 {name}: 셰이더 {result.Compiled.Count}개 · 미리보기를 다시 시작해요");
            RestartRenderer();
        }

        /// <summary>셰이더 팩을 만들고, 만들었으면 미리보기를 다시 시작해 불러온다 (ready → 장면 다시 불러오기).</summary>
        /// <summary>VRChat에 올리기 (UploadWindow). 원본 패키지에 lilToon 같은 에셋이 이미 들었는지는 가져오기 색인으로 본다.</summary>
        async void UploadDialog()
        {
            if (_ctx.Doc == null) { SetStatus("먼저 아바타를 열어 주세요."); return; }
            var recipe = Recipe.From(_ctx.Doc.Original);
            Func<string, bool>? has = null;
            if (recipe != null && recipe.Sources.Count > 0)
            {
                try { var imp = await Task.Run(() => Items.Open(recipe.Sources)); has = g => imp.Db.Get(g) != null; }
                catch (Exception e) { AppendLog("warn", "원본 색인을 읽지 못했습니다: " + e.Message); }
            }
            await new UploadWindow(_ctx, has, _settings.UploadThumbs[Upload.AvatarKey(_ctx.Doc!)].AsString, () => SetUploadThumb("")).ShowDialog(this);
        }

        /// <summary>기존 Unity 프로젝트에 새 장면으로 내보내기 (ExportWindow).</summary>
        async void ExportDialog()
        {
            if (_ctx.Doc == null) { SetStatus("먼저 아바타를 열어 주세요."); return; }
            await new ExportWindow(_ctx).ShowDialog(this);
        }

        async void ShaderPackDialog()
        {
            var built = await new ShaderPackWindow(_ctx.Doc?.Original, _unityVersion).ShowDialog<bool>(this);
            if (!built) return;
            AppendLog("info", "셰이더 팩을 만들었습니다 · 미리보기를 다시 시작합니다 (" + ShaderPacks.PackDir + ")");
            RestartRenderer();
        }

        /// <summary>씬·프리팹 전체를 해석해 루트 하나를 골라 IR로 만들고 연다 (ImportWindow).</summary>
        async void ImportUnity(params string[] paths)
        {
            SetStatus("Unity에서 가져오는 중 · " + string.Join(", ", paths.Select(Path.GetFileName)));
            ImportWindow.Choices = _settings.ImportChoices;   // 지난번 선택 채우기 · 저장
            var r = await new ImportWindow(paths).ShowDialog<ImportResult?>(this);
            if (r != null) _settings.Save();
            if (r == null) { SetStatus("가져오기를 취소했어요."); return; }
            AppendLog("info", $"가져옴: {string.Join(", ", paths)} → {r.IrPath} (경고 {r.Warnings.Count}, 씬 전체 해석 경고 {r.SceneWarnings})");
            if (r.Timing.Length > 0) AppendLog("info", "가져오기 단계별 시간: " + r.Timing);
            foreach (var w in r.Warnings) AppendLog("warn", w);
            OpenIr(r.IrPath, null, null);
        }

        /// <summary>zip 안의 .unitypackage를 꺼내 패키지 목록으로 바꾼다 (BOOTH 배포 zip). 패키지가 하나도 없으면 null.</summary>
        async Task<string[]?> Unzip(string[] files)
        {
            if (!files.Any(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) return files;
            SetStatus("zip에서 패키지 꺼내는 중…");
            var notes = new List<string>();
            string[] got;
            try { got = await Task.Run(() => Packages.Expand(files, notes)); }
            catch (Exception e) { Error("zip을 열지 못했어요", e.Message); return null; }
            foreach (var n in notes) AppendLog("warn", n);
            if (got.Length == 0) { Error("패키지가 없어요", string.Join("\n", notes)); return null; }
            AppendLog("info", "zip에서 꺼냄: " + string.Join(", ", got));
            SetStatus("");
            return got;
        }

        async void OpenIrDialog()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("가져온 파일 열기"),
                FileTypeFilter = new[] { new FilePickerFileType("가져온 파일") { Patterns = new[] { "*.ir.json" } }, FilePickerFileTypes.All },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string p && await ConfirmDiscard()) OpenIr(p, null, null);
        }

        async void OpenSessionDialog()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("세션 열기"),
                FileTypeFilter = new[] { new FilePickerFileType("세션") { Patterns = new[] { "*" + Session.Extension, "*" + Session.OldExtension, "*" + SessionPack.Extension } } },
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is string p && await ConfirmDiscard())
            {
                if (SessionPack.IsPack(p)) OpenPack(p); else OpenSession(p);
            }
        }

        static string ImportsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "imports");

        /// <summary>
        /// 파일까지 담은 세션 (.alexp, 2026-11-03): 가져오기 폴더 아래에 풀고 세션처럼 연다. 저장하면 같은 묶음 파일에 다시 담는다.
        /// </summary>
        async void OpenPack(string path)
        {
            SetStatus("묶음 파일을 푸는 중… · " + Path.GetFileName(path));
            Session.Loaded s;
            try { s = await Task.Run(() => SessionPack.Open(path, SessionPack.ExtractDir(ImportsRoot, path))); }
            catch (Exception e) { Error("묶음 파일을 열지 못했어요", e.Message); return; }
            if (OpenIr(s.IrPath, s.Changeset, path, s.Variants))
            {
                _ctx.Camera = s.Camera;
                ApplySessionStudio(s.Studio);
            }
        }

        async void OpenSession(string path)
        {
            Session.Loaded s;
            try { s = Session.Read(path); }
            catch (Exception e) { Error("세션을 열지 못했어요", e.Message); return; }
            var ir = File.Exists(s.IrPath) ? s.IrPath : await FindMissingIr(s, Path.GetFileName(path));
            if (ir == null) return;
            if (OpenIr(ir, s.Changeset, path, s.Variants))
            {
                _ctx.Camera = s.Camera;
                if (ir != s.IrPath) _ctx.Doc?.MarkUnsaved();   // 세션에 새 IR 경로를 적으려면 저장
                ApplySessionStudio(s.Studio);
            }
        }

        /// <summary>세션에 스튜디오 값이 있으면 넣고 스튜디오 페이지를 연다 (바닥은 이 페이지에서만 깔린다).</summary>
        void ApplySessionStudio(JNode studio)
        {
            if (studio.Kind != JKind.Object) return;
            if (studio.Has("floorFit") && _posePanel != null) { _settings.ViewFloorFit = studio["floorFit"].AsBool; _posePanel.FloorFit = _settings.ViewFloorFit; }
            _studioPanel?.ApplySessionStudio(studio);
            _side.Select("studio");
        }

        /// <summary>
        /// 세션·사진의 아바타 IR이 없을 때 (2026-10-27: 가져온 파일 캐시를 지웠거나 다른 PC에서 열면 "열지 못했어요"로 끝났다).
        /// 세션에 재료가 적혀 있고 재료 파일이 다 있으면 다시 만든다. 아니면 IR 파일을 직접 고른다. 취소하면 null.
        /// </summary>
        async Task<string?> FindMissingIr(Session.Loaded s, string what)
        {
            var recipe = Recipe.FromSource(s.Source);
            var missing = recipe?.MissingSources() ?? new List<string>();
            bool canBuild = recipe != null && missing.Count == 0;
            var why = recipe == null ? "이 세션에는 재료(원본 패키지) 기록이 없어요 (2026-10-27 전에 저장한 세션). 가져온 파일을 직접 찾아 주세요."
                : missing.Count > 0 ? "소스 파일도 이동하거나 삭제한 것 같습니다:\n" + string.Join("\n", missing.Take(4)) + "\n\n가져온 파일을 직접 찾거나, 소스 파일을 원래 위치에 두고 다시 열어 주세요."
                : "가져올 때 쓴 재료(원본 패키지)가 그대로 있어서 다시 만들 수 있어요. 바꾼 값은 그대로 옮겨요.";
            var buttons = canBuild ? new[] { "다시 만들기", "가져온 파일 찾기…", "취소" } : new[] { "가져온 파일 찾기…", "취소" };
            var r = await Dialog.Ask(this, "가져온 아바타 파일이 없어요", $"{what}이(가) 쓰던 가져온 파일이 없어요 (가져온 파일을 지웠거나 다른 PC예요).\n{s.IrPath}\n\n{why}", buttons);
            if (canBuild && r == 0)
            {
                _sceneItem.Set(StatusItem.State.Busy, "재료로 아바타를 다시 만드는 중");
                SetStatus("재료로 아바타를 다시 만드는 중… (가져오기와 같은 시간이 걸려요)");
                try
                {
                    // 이 PC의 가져오기 폴더에, 예전과 같은 폴더 이름으로 (다른 PC의 경로는 쓸 수 없을 수 있다)
                    var name = Path.GetFileName(Path.GetDirectoryName(s.IrPath) ?? "");
                    var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALEdit", "imports", name.Length > 0 ? name : "session");
                    Directory.CreateDirectory(outDir);
                    var (built, warns, _) = await Task.Run(() => Items.Build(recipe!, outDir));
                    foreach (var w in warns) AppendLog("warn", w);
                    AppendLog("info", "세션의 아바타 파일을 소스 파일로 다시 만들었습니다 → " + built);
                    return built;
                }
                catch (Exception e) { _sceneItem.Set(StatusItem.State.Off, "장면 없음"); Error("다시 만들지 못했어요", e.Message); return null; }
            }
            if (r != (canBuild ? 1 : 0)) return null;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("가져온 아바타 파일 찾기"),
                FileTypeFilter = new[] { new FilePickerFileType("가져온 파일") { Patterns = new[] { "*.ir.json" } }, FilePickerFileTypes.All },
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        /// <summary>촬영 PNG에 넣어 둔 세션으로 연다 (PngText). 저장할 곳은 정해지지 않은 새 세션 — 저장하면 .alesav를 고른다.</summary>
        async void OpenPngSession(string path)
        {
            var s = PngText.ReadSession(path);
            if (s == null) { Error("이 이미지에는 세션이 없어요", "이 에디터에서 \"세션 넣기\"를 켜고 촬영한 PNG만 같은 상태로 다시 열 수 있습니다.\n" + path); return; }
            var ir = File.Exists(s.IrPath) ? s.IrPath : await FindMissingIr(s, Path.GetFileName(path));
            if (ir == null) return;
            if (OpenIr(ir, s.Changeset, null, s.Variants))
            {
                _ctx.Camera = s.Camera;
                _ctx.Doc?.MarkUnsaved();
                ApplySessionStudio(s.Studio);
                SetStatus($"이미지에서 세션을 열었어요 · {Path.GetFileName(path)}");
            }
        }

        bool OpenIr(string irPath, JNode? changeset, string? sessionPath, JNode? variants = null)
        {
            IrScene scene;
            Boot("파일 읽기 시작");
            // 렌더러에는 IR을 읽기 전에 먼저 시킨다 (2026-11-12 로딩 시간): 렌더러는 경로만 있으면 되는데, 에디터가 IR을 다 읽을 때까지(큰 아바타 0.15~0.25초) 놀고 있었다
            bool early = _ctx.Render?.IsConnected == true && File.Exists(irPath);
            if (early) _ctx.Render!.Send(Msg.Make(Msg.LoadScene).Set("irPath", Path.GetFullPath(irPath)).Set("texCache", _settings.TextureCache));
            try { scene = IrScene.Load(irPath); }
            catch (Exception e)
            {
                Error("가져온 파일을 열지 못했어요", irPath + "\n" + e.Message);
                if (early && _ctx.Doc != null) SendLoadScene();   // 렌더러를 앞의 문서로 되돌린다
                return false;
            }
            Boot("파일 읽음");
            ClearRecovery();   // 앞의 문서는 저장했거나 버리기로 했다 (복구할 때는 OfferRecovery가 바로 다시 쓴다)
            var doc = new Document(scene, Path.GetFullPath(irPath));
            if (changeset != null) doc.LoadChangeset(changeset);
            if (variants != null) doc.LoadVariants(variants);
            _sync = new RenderSync(doc, new CtxSink(_ctx));
            _sync.OriginalEnded += () => RefreshOriginalButton();
            RefreshOriginalButton();
            doc.Changed += _ => { RefreshTitle(); RefreshUndoButtons(); RefreshHistory(); };
            RefreshHistory();
            _sessionPath = sessionPath;
            _ctx.Camera = JNode.Null;
            _sceneLoadedOnce = false;
            _closeConfirmed = false;
            // 렌더러에 먼저 보낸다 (2026-10-23): 페이지를 채우는 데 1초쯤 걸려(큰 시험 장면) 그동안 렌더러가 놀았다. 장면 결과(sceneLoaded)는 페이지를 다 채운 뒤에 처리된다 (같은 UI 스레드)
            bool connected = _ctx.Render?.IsConnected == true;
            if (connected) SendLoadScene(doc, sent: early);
            // 몸 메시를 처음부터 골라 두고(페이지가 한 번만 그린다), 그 원본은 페이지를 채우는 동안 백그라운드에서 읽는다
            var first = RendererPicker.BestBody(scene) ?? scene.Renderers.FirstOrDefault()?.Id;
            var meshes = new MeshEdits(doc);
            if (first != null && scene.FindRenderer(first) is { } fr) meshes.Prefetch(fr.Mesh);
            Later.Clear();
            _ctx.SetDocument(doc, first, meshes);
            Boot("페이지에 문서 채움");
            _openTiming = "페이지 채우기 " + EditorContext.TimingLine(_ctx.OpenTimings);
            AppendLog("info", _openTiming);
            var warm = WarmUp();
            Boot("페이지 예열");
            var warmLine = $"페이지 예열 {warm:0} ms — {_warmDetail}";
            _openTiming += Environment.NewLine + warmLine;
            AppendLog("info", warmLine);
            RefreshTitle(); RefreshUndoButtons();
            if (!connected) _sceneItem.Set(StatusItem.State.Busy, $"{scene.Name} · 미리보기 기다리는 중");
            SetStatus($"{scene.Name} 열림 · 메시 {scene.Renderers.Count}개 · 머티리얼 {scene.Materials.Count}개");
            RememberLast(sessionPath ?? irPath);
            return true;
        }

        /// <summary>켤 때 다시 열 파일 (세션이 있으면 세션). 화면 확인 모드는 남기지 않는다</summary>
        void RememberLast(string path)
        {
            if (_shotsDir != null) return;
            var full = Path.GetFullPath(path);
            bool recentSame = _settings.RecentFiles.Count > 0 && string.Equals(_settings.RecentFiles[0], full, StringComparison.OrdinalIgnoreCase);
            if (_settings.LastFile == full && recentSame) return;
            RememberRecent(full);
            _settings.LastFile = full;
            _settings.Save();
        }

        /// <summary>
        /// 켤 때 마지막 파일 열기 (2026-10-22 사용자 요청): 시작 인자로 받은 파일이 없고, 복구할 작업도 없을 때.
        /// 세션이면 그 안의 아바타 IR이 아직 있는지 먼저 본다 (없으면 조용히 건너뛰고 로그만 — 시작할 때 오류 창을 띄우지 않게).
        /// </summary>
        void OpenLastFile()
        {
            var f = _settings.LastFile;
            if (!_settings.OpenLastOnStart || string.IsNullOrEmpty(f)) return;
            if (!File.Exists(f)) { AppendLog("info", "마지막 파일이 없어 열지 않았습니다: " + f); return; }
            if (Session.IsSessionFile(f))
            {
                try { if (!File.Exists(Session.Read(f).IrPath)) { AppendLog("info", "마지막 세션의 아바타 파일이 없어 열지 않았습니다: " + f); return; } }
                catch (Exception e) { AppendLog("warn", "마지막 세션을 읽지 못했습니다: " + e.Message); return; }
                OpenSession(f);
            }
            else if (SessionPack.IsPack(f)) OpenPack(f);
            else if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) OpenIr(f, null, null);
            else return;
            if (_ctx.Doc != null) SetStatus("지난번에 작업하던 파일을 열었습니다 · " + Path.GetFileName(f) + " (설정에서 끌 수 있습니다)");
        }

        sealed class CtxSink : IRenderSink
        {
            readonly EditorContext _c; public CtxSink(EditorContext c) { _c = c; }
            public bool IsConnected => _c.Render?.IsConnected == true;
            public void Send(JNode m) => _c.Render?.Send(m);
        }

        /// <summary>세션 저장. pack: 파일까지 담은 세션(.alexp)으로 — 지금 연 것이 묶음이면 Ctrl+S도 묶음으로 다시 담는다.</summary>
        async Task<bool> SaveSession(bool saveAs, bool pack = false)
        {
            if (_ctx.Doc == null) return false;
            var path = _sessionPath;
            if (!saveAs && path != null && SessionPack.IsPack(path)) pack = true;
            var ext = pack ? SessionPack.Extension : Session.Extension;
            if (saveAs || path == null)
            {
                var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Texts.T(pack ? "파일까지 담아 저장" : "세션 저장"), DefaultExtension = ext.TrimStart('.'),
                    SuggestedFileName = (_ctx.Doc.Original.Name.Length > 0 ? _ctx.Doc.Original.Name : "session") + ext,
                    FileTypeChoices = new[] { pack ? new FilePickerFileType("파일까지 담은 세션") { Patterns = new[] { "*" + SessionPack.Extension } }
                                                   : new FilePickerFileType("세션") { Patterns = new[] { "*" + Session.Extension } } },
                });
                path = f?.TryGetLocalPath();
                if (path == null) return false;
                if (pack && !SessionPack.IsPack(path)) path += SessionPack.Extension;
            }
            try
            {
                if (pack)
                {
                    SetStatus("아바타 파일·텍스처를 담는 중… · " + Path.GetFileName(path));
                    var doc = _ctx.Doc; var to = path;
                    var json = Session.ToJson(doc, _ctx.Camera); var ir = doc.IrPath; var name = doc.Original.Name;
                    var res = await Task.Run(() => SessionPack.Save(to, json, ir, name));
                    doc.MarkSaved();
                    foreach (var m in res.Missing) AppendLog("warn", "묶음: 파일이 없어 담지 못했습니다 · " + m);
                    AppendLog("info", $"묶음 저장: 파일 {res.Files}개 · {res.Bytes / 1048576.0:0.0} MB → {path}");
                }
                else Session.Save(path, _ctx.Doc, _ctx.Camera);
                _sessionPath = path;
                RememberLast(path);
                ClearRecovery();
                RefreshTitle();
                SetStatus("저장했습니다 · " + Path.GetFileName(path));
                return true;
            }
            catch (Exception e) { Error("저장하지 못했어요", e.Message); return false; }
        }

        /// <summary>저장 안 한 변경이 있으면 묻는다. 계속해도 되면 true.</summary>
        async Task<bool> ConfirmDiscard()
        {
            if (_ctx.Doc?.IsDirty != true) return true;
            var r = await Dialog.Ask(this, "저장하지 않은 변경이 있어요", "지금까지 바꾼 값을 세션으로 저장할까요?", "저장", "저장 안 함", "취소");
            if (r == 0) return await SaveSession(false);
            return r == 1;
        }

        void Error(string title, string text)
        {
            SetStatus(title);
            AppendLog("error", title + ": " + text);
            _ = Dialog.Ask(this, title, text, "확인");
        }

        void RefreshTitle()
        {
            var name = _sessionPath != null ? Path.GetFileName(_sessionPath) : _ctx.Doc != null ? Path.GetFileName(_ctx.Doc.IrPath) : null;
            bool dirty = _ctx.Doc?.IsDirty == true;
            Title = (RunMode.Test ? "[테스트] " : "") + (name != null ? name + (dirty ? " •" : "") + " — " : "") + AppName;
            _fileText.Text = name != null ? name + (dirty ? "  •" : "") : "";
            int n = _ctx.Doc?.Overrides.Count ?? 0;
            if (_ctx.Doc == null) _changesItem.Set(StatusItem.State.Off, "열린 파일 없음");
            else if (n == 0) _changesItem.Set(StatusItem.State.Off, "바꾼 값 없음");
            else _changesItem.Set(dirty ? StatusItem.State.Warn : StatusItem.State.On, $"변경한 값 {n}개 · " + (dirty ? "저장 안 됨" : "저장됨"));
        }

        void RefreshUndoButtons()
        {
            var d = _ctx.Doc;
            _undoBtn.IsEnabled = d?.CanUndo == true;
            ToolTip.SetTip(_undoBtn, "실행 취소 (Ctrl+Z)" + (d?.UndoLabel is string u && u.Length > 0 ? $"\n{u}" : ""));
            _redoBtn.IsEnabled = d?.CanRedo == true;
            ToolTip.SetTip(_redoBtn, "다시 실행 (Ctrl+Y)" + (d?.RedoLabel is string r && r.Length > 0 ? $"\n{r}" : ""));
            _saveBtn.IsEnabled = d != null;
        }

        // 무엇을 되돌렸는지 상태 줄에 (2026-10-27: 다른 페이지에서 바꾼 값이면 아무 일도 없던 것처럼 보였다)
        void Undo()
        {
            var d = _ctx.Doc; if (d == null) return;
            if (!d.CanUndo) { SetStatus("더 되돌릴 것이 없어요"); return; }
            var label = d.UndoLabel; d.Undo(); RefreshUndoButtons();
            SetStatus("되돌렸습니다: " + (string.IsNullOrEmpty(label) ? "마지막 변경" : label) + " (다시 하기 Ctrl+Y)");
        }
        void Redo()
        {
            var d = _ctx.Doc; if (d == null) return;
            if (!d.CanRedo) { SetStatus("다시 할 것이 없어요"); return; }
            var label = d.RedoLabel; d.Redo(); RefreshUndoButtons();
            SetStatus("다시 했습니다: " + (string.IsNullOrEmpty(label) ? "변경" : label));
        }
    }
}
