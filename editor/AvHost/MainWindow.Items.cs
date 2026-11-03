// 메인 창: 의상·헤어·악세서리 붙이기 · 빼기 · 붙일 곳 바꾸기.
// 아이템 목록이 바뀌면 가져오기 재료(IR source)로 IR을 다시 만들고, 지금까지 바꾼 값(Changeset)을 옮겨 다시 연다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using ImportSim;
using VrcProto.Host.Core;
using VrcProto.Protocol;
using VrcProto.Protocol.Ir;
using Path = System.IO.Path;

namespace VrcProto.AvHost
{
    public sealed partial class MainWindow
    {
        bool _rebuilding;
        AvatarPanel? _avatarPanel;

        Recipe? CurrentRecipe(bool explain)
        {
            var doc = _ctx.Doc;
            if (doc == null) return null;
            var r = Recipe.From(doc.Original);
            if (r == null && explain)
                Error("이 아바타에는 붙일 수 없어요", "다시 만들 재료가 이 파일에 없어요 (예전에 가져온 파일이나 샘플). Unity 씬·프리팹이나 .unitypackage에서 다시 가져와 주세요.");
            return r;
        }

        /// <summary>패키지를 골라 붙일 아이템을 고른다 (files가 있으면 고르기를 건너뜀).</summary>
        async void AddItemDialog() => await AddItem(null);

        async Task AddItem(string[]? files)
        {
            var recipe = CurrentRecipe(true);
            if (recipe == null || _rebuilding) return;
            if (files == null)
            {
                var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Texts.T("의상·헤어·악세서리 패키지 고르기 (머티리얼 패키지도 함께)"), AllowMultiple = true,
                    FileTypeFilter = new[] { new FilePickerFileType("Unity 패키지 (BOOTH zip도 돼요)") { Patterns = new[] { "*.unitypackage", "*.zip" } } },
                });
                files = picked.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
                if (files.Length == 0) return;
            }
            if (await Unzip(files) is not string[] pkgs) return;
            files = pkgs;
            var res = await new ItemWindow(recipe, files).ShowDialog<ItemResult?>(this);
            if (res == null) { SetStatus("붙이기를 취소했어요."); return; }
            var items = recipe.Items.Where(s => s.Prefab != res.Spec.Prefab).ToList();
            items.Add(res.Spec);
            if (!await Rebuild(recipe.With(res.NewSources, items), $"{res.Name} 붙이는 중…", $"{res.Name}을(를) 붙였어요")) return;
            await OfferHideBase(res.Spec.Prefab, res.Name);
        }

        /// <summary>헤어·의상을 붙였는데 아바타의 기본 헤어·옷이 보이고 있으면 숨길지 묻는다 (기본 묶음 끄기, 실행 취소 한 번)</summary>
        async Task OfferHideBase(string prefab, string name)
        {
            var o = _ctx.Doc?.Original.Outfits.FirstOrDefault(x => x.Prefab == prefab);
            if (o == null || _avatarPanel == null || (o.Kind != "hair" && o.Kind != "outfit") || !_avatarPanel.AnyVisible(o.Kind)) return;
            var label = Wardrobe.KindLabel(o.Kind);
            if (await Dialog.Ask(this, $"기본 {label}를 숨길까요?", $"{name}을(를) {Items.KindLabel(o.Kind)}로 붙였어요. 아바타에 원래 있던 {label}가 겹쳐 보일 수 있어요. 아바타 페이지 \"기본 옷·헤어\"에서 언제든 다시 켤 수 있어요.", "숨기기", "그대로 두기") != 0) return;
            if (_avatarPanel.SetGroup(o.Kind, false)) SetStatus($"기본 {label}를 숨겼어요 (실행 취소로 되돌려요)");
        }

        async void RemoveItem(string prefab)
        {
            var recipe = CurrentRecipe(true);
            if (recipe == null || _rebuilding) return;
            var name = _ctx.Doc!.Original.Outfits.FirstOrDefault(o => o.Prefab == prefab)?.Name ?? "아이템";
            if (await Dialog.Ask(this, $"{name} 빼기", "아바타에서 이 아이템을 빼요. 이 아이템에 바꿔 둔 값(켜고 끄기·블렌드쉐이프·머티리얼)도 함께 없어져요. 실행 취소로 되돌릴 수 없어요.", "빼기", "취소") != 0) return;
            await Rebuild(recipe.With(Array.Empty<string>(), recipe.Items.Where(s => s.Prefab != prefab)), $"{name} 빼는 중…", $"{name}을(를) 뺐어요");
        }

        async void PlaceItem(string prefab)
        {
            var recipe = CurrentRecipe(true);
            if (recipe == null || _rebuilding) return;
            var o = _ctx.Doc!.Original.Outfits.FirstOrDefault(x => x.Prefab == prefab);
            var cur = recipe.Items.FirstOrDefault(s => s.Prefab == prefab);
            if (o == null || cur == null) return;
            var report = new OutfitMerge.Report { AddedFrom = prefab, Kind = o.Kind, Reason = o.Reason, Target = o.Target, NeedsPlacement = o.NeedsPlacement, NearOrigin = o.NearOrigin };
            List<string> bones = _ctx.Doc.Original.Humanoid.Keys.ToList();
            var spec = await PlacementDialog.Ask(this, o.Name, report, bones, cur.Kind == "auto" ? null : cur);
            if (spec == null) return;
            _ctx.Doc.Reset(Addr.NodeTransform(o.Node), "옮긴 자리 버리기");   // 새로 붙이는 자리가 핸들로 옮긴 자리를 대신한다
            spec.Prefab = prefab;
            var items = recipe.Items.Select(s => s.Prefab == prefab ? spec : s).ToList();
            await Rebuild(recipe.With(Array.Empty<string>(), items), $"{o.Name} 다시 붙이는 중…", $"{o.Name}을(를) {Items.BoneLabel(spec.Target)}에 붙였어요");
        }

        /// <summary>헤어·머리 악세서리를 분석된 머리 크기에 맞춰 자동으로 다시 붙인다.</summary>
        async void AutoScaleItem(string prefab)
        {
            var recipe = CurrentRecipe(true);
            if (recipe == null || _rebuilding) return;
            var o = _ctx.Doc!.Original.Outfits.FirstOrDefault(x => x.Prefab == prefab);
            var spec = recipe.Items.FirstOrDefault(x => x.Prefab == prefab);
            if (o == null || spec == null || o.HeadScale <= 0 || double.IsNaN(o.HeadScale) || double.IsInfinity(o.HeadScale))
            {
                SetStatus("이 아이템에서 머리 크기 비율을 계산하지 못했어요.");
                return;
            }
            spec.Scale = Math.Clamp(o.HeadScale, 0.01, 100);
            await Rebuild(recipe.With(Array.Empty<string>(), recipe.Items), $"{o.Name} 머리 크기 맞추는 중…", $"{o.Name}을(를) 머리 크기에 맞췄어요 (×{spec.Scale:0.###})");
        }

        /// <summary>선택한 아이템의 전체 크기 배율을 직접 입력한다.</summary>
        async void ScaleItem(string prefab)
        {
            var recipe = CurrentRecipe(true);
            if (recipe == null || _rebuilding) return;
            var o = _ctx.Doc!.Original.Outfits.FirstOrDefault(x => x.Prefab == prefab);
            var spec = recipe.Items.FirstOrDefault(x => x.Prefab == prefab);
            if (o == null || spec == null) return;
            var text = await Dialog.Prompt(this, "아이템 크기", $"{o.Name}의 전체 크기 배율을 입력하세요.\n1 = 현재 크기 · 1.1 = 10% 크게 · 0.9 = 10% 작게\n아이템 루트와 붙인 본·부품에 함께 적용돼요.", spec.Scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            if (text == null) return;
            if (!Ui.TryParse(text, out var scale) || scale < 0.01 || scale > 100)
            {
                Error("크기를 바꾸지 못했어요", "0.01에서 100 사이의 숫자를 입력해 주세요.");
                return;
            }
            spec.Scale = scale;
            await Rebuild(recipe.With(Array.Empty<string>(), recipe.Items), $"{o.Name} 크기 바꾸는 중…", $"{o.Name} 크기를 ×{scale:0.###}로 바꿨어요");
        }

        // ---------------- 커서로 놓기 ----------------
        // 렌더러가 아이템을 커서 아래 표면 위로 옮겨 보이고, 누르면 붙일 뼈와 그 뼈 기준 위치·회전·크기를 보낸다 (itemPlaced).
        // 그 값을 가져오기 재료(Spec.Offset)에 적고 IR을 다시 만든다 → 세션·다시 만들기에도 그 자리가 남는다.
        string? _placingPrefab;

        void CursorPlaceItem(string prefab)
        {
            var recipe = CurrentRecipe(true);
            if (recipe == null || _rebuilding) return;
            var o = _ctx.Doc!.Original.Outfits.FirstOrDefault(x => x.Prefab == prefab);
            if (o == null) return;
            if (_ctx.Render == null || !_ctx.Render.IsConnected) { SetStatus("미리보기가 켜져야 커서로 놓을 수 있어요."); return; }
            if (!_ctx.Doc.Get(Addr.NodeActive(o.Node)).AsBool) { SetStatus($"{o.Name}이(가) 꺼져 있어요. 입혀 보기를 켠 뒤 놓아 주세요."); return; }
            _placingPrefab = prefab;
            _ctx.Render.Send(Msg.Make(Msg.PlaceItem).Set("node", o.Node));
            _viewport.Activate();
        }

        async void OnItemPlaced(JNode m)
        {
            var prefab = _placingPrefab; _placingPrefab = null;
            if (prefab == null || m["cancelled"].AsBool) return;
            await PlaceAt(prefab, m);
        }

        /// <summary>VR로 잡아 다른 뼈 가까이 놓은 아이템 (VrItemGrab, 2026-10-27) — 커서로 놓기와 같이 그 뼈에 다시 붙인다.</summary>
        async void OnVrItemPlaced(JNode m)
        {
            if (_rebuilding) { SetStatus("다시 만드는 중이라 놓은 자리를 적지 못했어요. 잠시 뒤 다시 놓아 주세요."); return; }
            var o = _ctx.Doc?.Original.Outfits.FirstOrDefault(x => x.Node == m["node"].AsInt);
            if (o == null) return;
            await PlaceAt(o.Prefab, m);
        }

        async Task PlaceAt(string prefab, JNode m)
        {
            var recipe = CurrentRecipe(true);
            var o = _ctx.Doc?.Original.Outfits.FirstOrDefault(x => x.Prefab == prefab);
            var cur = recipe?.Items.FirstOrDefault(s => s.Prefab == prefab);
            if (recipe == null || o == null || cur == null || m["offset"].Count < 10) return;
            var bone = m["bone"].AsString;
            _ctx.Doc!.Reset(Addr.NodeTransform(o.Node), "옮긴 자리 버리기");   // 놓은 자리(Offset)가 핸들로 옮긴 자리를 대신한다
            var spec = new OutfitMerge.Spec
            {
                Prefab = prefab, Target = bone, KeepWorld = true,
                Kind = cur.Kind != "auto" ? cur.Kind : o.Kind == "outfit" || o.Kind == "hair" ? o.Kind : "accessory",
                Offset = m["offset"].Items.Select(x => x.AsDouble).ToArray(),
            };
            var items = recipe.Items.Select(s => s.Prefab == prefab ? spec : s).ToList();
            await Rebuild(recipe.With(Array.Empty<string>(), items), $"{o.Name} 놓는 중…", $"{o.Name}을(를) {Items.BoneLabel(bone)}에 놓았어요");
        }

        // ---------------- 가져온 파일 창 ----------------
        ImportedFilesPanel? _filesPanel;

        /// <summary>아바타 페이지 "가져온 파일…": 에디터 위에 떠 있는 창 (자기 페이지에 묶이지 않음, 핀 가능)</summary>
        void OpenFilesWindow()
        {
            if (_filesPanel == null)
            {
                _filesPanel = new ImportedFilesPanel(_ctx)
                {
                    ReplaceSource = ReplaceSource, RemoveSource = RemoveSource, OpenIr = p => OpenPath(p), PlaceItem = PlaceItem, RemoveItem = RemoveItem, AddItem = AddItemDialog,
                    Confirm = async (title, text, yes) => await Dialog.Ask(this, title, text, yes, "취소") == 0,
                    PickFile = async title =>
                    {
                        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
                        return picked.Select(f => f.TryGetLocalPath()).FirstOrDefault(x => x != null);
                    },
                };
            }
            else _filesPanel.Refresh();
            var f = _floats.Show("files", null, "가져온 파일", _filesPanel, canDock: false, new Avalonia.Size(520, 600));
            f.DockRequested -= CloseFiles; f.DockRequested += CloseFiles;
        }

        void CloseFiles()
        {
            var f = _floats.Find("files");
            if (f == null) return;
            f.Release();
            _floats.Close(f);
        }

        // ---------------- 머티리얼·에셋 창 ----------------
        AssetsPanel? _assetsPanel;

        /// <summary>머티리얼 페이지 탭 막대의 단추: 머티리얼·텍스처·메시 목록을 에디터 위에 떠 있는 창으로 (자기 페이지에 묶이지 않음, 핀 가능)</summary>
        void OpenAssetsWindow()
        {
            if (_assetsPanel == null)
                _assetsPanel = new AssetsPanel(_ctx)
                {
                    OpenMaterial = (renderer, mat) => { _ctx.Select(renderer, mat); _side.Select("material"); },
                    Confirm = async (title, text, yes) => await Dialog.Ask(this, title, text, yes, "취소") == 0,
                };
            else _assetsPanel.Refresh();
            var f = _floats.Show("assets", null, "머티리얼·에셋", _assetsPanel, canDock: false, new Avalonia.Size(560, 640));
            f.DockRequested -= CloseAssets; f.DockRequested += CloseAssets;
        }

        void CloseAssets()
        {
            var f = _floats.Find("assets");
            if (f == null) return;
            f.Release();
            _floats.Close(f);
        }

        /// <summary>재료 경로를 바꾸고 다시 만든다 (옮긴 패키지 다시 찾기·새 버전으로 바꾸기)</summary>
        async Task ReplaceSource(string old, string now)
        {
            var r = CurrentRecipe(true);
            if (r == null || _rebuilding) return;
            var srcs = r.Sources.Select(s => string.Equals(s, old, StringComparison.OrdinalIgnoreCase) ? now : s).ToList();
            await Rebuild(new Recipe { Sources = srcs, Guid = r.Guid, Root = r.Root, Items = r.Items }, $"{Path.GetFileName(now)}로 바꿔 다시 만드는 중…", $"재료를 {Path.GetFileName(now)}로 바꿨어요");
        }

        /// <summary>재료에서 빼고 다시 만든다 (그 패키지의 아이템이 남아 있으면 다시 만들기가 실패해 그대로)</summary>
        async Task RemoveSource(string path)
        {
            var r = CurrentRecipe(true);
            if (r == null || _rebuilding || r.Sources.Count == 0 || string.Equals(r.Sources[0], path, StringComparison.OrdinalIgnoreCase)) return;
            var srcs = r.Sources.Where(s => !string.Equals(s, path, StringComparison.OrdinalIgnoreCase)).ToList();
            await Rebuild(new Recipe { Sources = srcs, Guid = r.Guid, Root = r.Root, Items = r.Items }, $"{Path.GetFileName(path)} 빼고 다시 만드는 중…", $"{Path.GetFileName(path)}을(를) 재료에서 뺐어요");
        }

        /// <summary>재료대로 IR을 다시 만들고, 바꾼 값을 옮겨 다시 연다. 세션은 저장하지 않은 상태가 된다 (IR 경로가 바뀜).</summary>
        async Task<bool> Rebuild(Recipe recipe, string busy, string done)
        {
            var doc = _ctx.Doc;
            if (doc == null) return false;
            var missing = recipe.MissingSources();
            if (missing.Count > 0) { Error("가져올 파일이 없어요", "이동하거나 삭제한 것 같습니다:\n" + string.Join("\n", missing)); return false; }
            _rebuilding = true;
            _sceneItem.Set(StatusItem.State.Busy, busy);
            SetStatus(busy);
            string path; List<string> warns;
            try
            {
                var outDir = Path.GetDirectoryName(doc.IrPath)!;
                (path, warns, _) = await Task.Run(() => Items.Build(recipe, outDir));
            }
            catch (Exception e) { _rebuilding = false; Error("다시 만들지 못했어요", e.Message); return false; }
            _rebuilding = false;
            foreach (var w in warns) AppendLog("warn", w);

            IrScene next;
            try { next = IrScene.Load(path); }
            catch (Exception e) { Error("새로 만든 파일을 열지 못했어요", e.Message); return false; }
            var changes = Recompose.RemapChangeset(doc.ChangesetToJson(), doc.Original, next);
            var camera = _ctx.Camera;
            // 변형(비교 시트)도 지금 값처럼 새 IR로 옮긴다 (없어진 대상의 값은 버려짐)
            var variants = JNode.Arr();
            foreach (var v in doc.Variants) variants.Add(v.ToJson().Set("changeset", Recompose.RemapChangeset(v.Changeset, doc.Original, next)));
            if (!OpenIr(path, changes, _sessionPath, variants)) return false;
            _ctx.Camera = camera;
            _ctx.Doc!.MarkUnsaved();
            RefreshTitle();
            foreach (var o in next.Outfits.Where(x => x.NeedsPlacement)) AppendLog("warn", $"{o.Name}: 어디에 붙일지 정하지 못해 아바타 루트에 그대로 있어요 (아바타 → 붙일 곳 바꾸기)");
            AppendLog("info", $"{done} → {path}");
            SetStatus(done);
            return true;
        }

        /// <summary>.unitypackage·zip을 끌어다 놓았을 때: 열린 아바타가 있으면 붙일지 새 아바타로 열지 묻는다.</summary>
        async void OnPackagesDropped(string[] files)
        {
            if (CurrentRecipe(false) != null)
            {
                var r = await Dialog.Ask(this, "패키지를 어떻게 쓸까요?", string.Join(", ", files.Select(Path.GetFileName)) + "\n\n지금 아바타에 의상·헤어·액세서리로 장착하거나, 새 아바타로 가져올 수 있습니다.", "지금 아바타에 붙이기", "새 아바타로 가져오기", "취소");
                if (r == 0) { await AddItem(files); return; }
                if (r != 1) return;
            }
            if (!await ConfirmDiscard()) return;
            if (await Unzip(files) is string[] pkgs) ImportUnity(pkgs);
        }
    }
}
