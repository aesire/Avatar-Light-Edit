// ALEdit 업로드 프로젝트용 에디터 스크립트 (PLAN.md 3.10). Host가 업로드 프로젝트의 Assets/VrcProto/Editor/에 복사하고 Unity로 부른다.
//   1) (Host가 원본 .unitypackage를 Unity처럼 Assets/에 풀어 둔다 — batchmode의 ImportPackage는 비동기라 쓰지 않음)
//   2) Assemble       (batchmode) 아바타·아이템을 장면에 놓고 Host의 변경(apply.json)을 사본 에셋에 적용, Assets/VrcProto/Upload.unity 저장
//   3) OpenForUpload  (창 모드)   장면을 열고 VRChat SDK 제어판과 "ALEdit 업로드" 창을 띄운다. 로그인은 사용자가 SDK 창에서 직접.
//   내보내기 (apply.json에 "export"가 있을 때, 사용자의 기존 Unity 프로젝트): Export가 같은 Assemble로 Assets/VrcProto/Export/<이름>_<시각>/에
//      새 장면과 사본 머티리얼·메시를 만든다. 원본 장면·프리팹·임포트 설정·아바타 ID는 건드리지 않는다.
//      Unity가 그 프로젝트를 열고 있으면 Host가 VrcProto/export-request.json을 두고, 여기 ExportRequest가 보고 실행한다.
//   원본 프로젝트에서 올리기 (2026-11-03, 요청에 "upload": true): 내보내기와 같이 새 장면을 만든 뒤 SDK 창·"ALEdit 업로드" 창을 연다.
//      Unity 프로젝트에서 가져온 아바타는 업로드 프로젝트를 따로 만들지 않는다 (원본 Assets 전체를 복사하게 되므로). 올린 ID는 아바타마다 VrcProto/uploads/에.
//   원본에 이미 눈 깜빡임 보정 컴포넌트가 있으면 에디터의 깜빡임 맞추기(blinkFix)는 적용하지 않는다 (같은 보정이 두 번 들어감).
//   그 컴포넌트에 입 블렌드쉐이프 목록도 채워져 있으면 입 맞추기(mouthFix)도 뺀다.
// 원본 파일은 고치지 않는다: 머티리얼·메시는 Assets/VrcProto/ 아래 사본을 만들어 아바타 인스턴스가 쓰게 한다.
// VRChat SDK 코드는 직접 참조하지 않는다 (공개 API를 이름으로 찾아 부름). SDK가 없거나 API가 바뀌면 SDK 창의 Build & Publish로 올리도록 안내한다.
// 진행 상황은 <프로젝트>/VrcProto/status.json 에 쓴다 (Host가 읽음).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using VrcProto.Protocol;

namespace VrcProto.Upload
{
    public static class Setup
    {
        static string ProjectDir => Path.GetDirectoryName(Application.dataPath);
        static string WorkDir => Path.Combine(ProjectDir, "VrcProto");
        static string ApplyPath => Path.Combine(WorkDir, "apply.json");
        static string StatusPath => Path.Combine(WorkDir, "status.json");
        public const string ScenePath = "Assets/VrcProto/Upload.unity";
        // 결과 폴더·장면 (올리기 = Assets/VrcProto · Upload.unity, 내보내기 = apply.json의 export.dir · <이름>.unity)
        static string _out = "Assets/VrcProto", _scene = ScenePath;
        static bool _export, _forUpload;
        static readonly List<string> Log = new List<string>();

        static JNode Apply() => JNode.Parse(File.ReadAllText(ApplyPath));

        public static void Status(string stage, bool ok, string message, JNode extra = null)
        {
            var log = JNode.Arr(); foreach (var l in Log) log.Add(l);
            var j = JNode.Obj().Set("stage", stage).Set("ok", ok).Set("message", message).Set("time", DateTime.Now.ToString("o")).Set("log", log);
            if (extra != null) foreach (var kv in extra.Props) j.Set(kv.Key, kv.Value);
            // status.json은 마지막 시도만 담는다. 성공한 업로드는 uploads.json에 따로 쌓아 두고 여기엔 마지막 것만 붙인다
            var last = LastUpload();
            if (last != null) j.Set("lastUpload", last);
            WriteJson(StatusPath, j);
        }

        static void WriteJson(string path, JNode j)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path + ".tmp", j.ToJson(true));
            File.Copy(path + ".tmp", path, true);
            File.Delete(path + ".tmp");
        }

        // ---------------- 올린 기록 (아바타 ID) ----------------
        // Host가 준비할 때마다 장면을 새로 만들어 PipelineManager의 blueprintId가 사라진다.
        // 그대로 두면 다시 올릴 때마다 새 아바타가 생기므로, 올린 ID를 여기 적어 두고 장면을 만들 때 다시 넣는다.
        // 원본 프로젝트에서 올리기 (2026-11-03): 한 프로젝트에 아바타가 여럿이라 아바타마다 따로 (VrcProto/uploads/<원본 guid>_<루트>.json).
        static string UploadsPath => UploadKey != null ? Path.Combine(WorkDir, "uploads", UploadKey + ".json") : Path.Combine(WorkDir, "uploads.json");

        /// 원본 프로젝트에서 올릴 때 아바타를 가리키는 열쇠 (업로드 프로젝트는 null — 아바타 하나뿐)
        public static string UploadKey;

        static string KeyOf(JNode a)
        {
            var s = a["avatar"]["guid"].AsString + (a["avatar"]["root"].AsString.Length > 0 ? "_" + a["avatar"]["root"].AsString : "");
            return s.Length > 0 ? Safe(s) : null;
        }

        static JNode Uploads()
        {
            try { if (File.Exists(UploadsPath)) return JNode.Parse(File.ReadAllText(UploadsPath)); } catch (Exception e) { Debug.LogWarning("[VrcProto] uploads.json을 읽지 못함: " + e.Message); }
            return JNode.Obj().Set("history", JNode.Arr());
        }

        static JNode LastUpload()
        {
            var h = Uploads()["history"].Items.ToList();
            return h.Count > 0 ? h[h.Count - 1] : null;
        }

        /// 다음 업로드가 덮어쓸 아바타 ID (없으면 새 아바타로 올라간다)
        public static string SavedAvatarId => Uploads()["avatarId"].AsStringOrNull;

        public static void RecordUpload(string avatarId, string name)
        {
            var u = Uploads();
            var h = JNode.Arr(); foreach (var e in u["history"].Items) h.Add(e);
            h.Add(JNode.Obj().Set("avatarId", avatarId).Set("name", name).Set("time", DateTime.Now.ToString("o")));
            u.Set("avatarId", avatarId).Set("history", h);
            WriteJson(UploadsPath, u);
        }

        /// 새 아바타로 올리게 저장한 ID를 지운다 (기록은 남김)
        public static void ForgetAvatarId()
        {
            var u = Uploads();
            u.Remove("avatarId");
            WriteJson(UploadsPath, u);
        }

        static Type _pipelineManagerType;
        static Type PipelineManagerType => _pipelineManagerType ?? (_pipelineManagerType = FindType("VRC.Core.PipelineManager"));

        static Component PipelineManager(GameObject avatar)
        {
            var t = PipelineManagerType;
            return t != null && avatar != null ? avatar.GetComponent(t) : null;
        }

        public static string BlueprintId(GameObject avatar)
        {
            var pm = PipelineManager(avatar);
            return pm == null ? null : pm.GetType().GetField("blueprintId")?.GetValue(pm) as string;
        }

        /// 장면의 아바타에 아바타 ID를 넣는다 (null이면 지움). PipelineManager가 없으면 붙인다
        public static void SetBlueprintId(GameObject avatar, string id)
        {
            var t = PipelineManagerType;
            if (t == null || avatar == null) return;
            var pm = avatar.GetComponent(t) ?? avatar.AddComponent(t);
            var f = t.GetField("blueprintId");
            if (f == null) { Warn("! PipelineManager.blueprintId를 찾지 못했어요 (SDK 버전 확인)"); return; }
            Undo.RecordObject(pm, "ALEdit 아바타 ID");
            f.SetValue(pm, id ?? "");
            if (PrefabUtility.IsPartOfPrefabInstance(pm)) PrefabUtility.RecordPrefabInstancePropertyModifications(pm);
            EditorUtility.SetDirty(pm);
            if (avatar.scene.IsValid()) EditorSceneManager.MarkSceneDirty(avatar.scene);
        }

        static void Warn(string s) { Log.Add(s); Debug.LogWarning("[VrcProto] " + s); }
        static void Info(string s) { Log.Add(s); Debug.Log("[VrcProto] " + s); }

        // ---------------- 2) 장면 만들기 + 변경 적용 ----------------
        public static void Assemble()
        {
            var stage = _export ? "exported" : "assembled";
            try
            {
                var a = Apply();
                EnsureFolder(_out);
                foreach (var d in new[] { "Materials", "Meshes", "Textures" }) EnsureFolder(_out + "/" + d);
                var avatar = PlaceAvatar(a["avatar"]);
                if (avatar == null) { Status(stage, false, "아바타 원본(프리팹·장면)을 찾지 못했어요"); Fail(); return; }
                var root = avatar.transform;

                var outfitRoots = new List<(GameObject go, JNode o)>();
                foreach (var o in a["outfits"].Items)
                {
                    var go = PlaceOutfit(root, o);
                    if (go != null) outfitRoots.Add((go, o));
                }
                var saved = _export && !_forUpload ? null : SavedAvatarId;   // 내보내기는 원본의 아바타 ID 그대로, 원본 프로젝트에서 올리기는 지난번에 올린 ID
                if (saved != null)
                {
                    var had = BlueprintId(avatar);
                    if (had != saved) { SetBlueprintId(avatar, saved); Info($"지난번에 올린 아바타 ID를 넣었어요: {saved}" + (string.IsNullOrEmpty(had) ? "" : $" (원본의 {had} 대신)")); }
                }
                if (!_export) FixModelImporters(avatar);   // 내보내기는 사용자 프로젝트의 임포트 설정을 바꾸지 않는다
                var existing = ExistingBlinkFix(avatar, out bool existingMouth);
                int skippedBlink = existing.Count > 0 ? DropFix(a["meshes"], MeshOps.BlinkFix) : 0;
                int skippedMouth = existingMouth ? DropFix(a["meshes"], MeshOps.MouthFix) : 0;
                if (existing.Count > 0)
                    Warn($"원본에 이미 눈 깜빡임 보정이 있어요 ({string.Join(", ", existing)}) — 에디터의 깜빡임 맞추기는 " + (skippedBlink > 0 ? $"빼고 적용했어요 (메시 {skippedBlink}개)" : "쓰지 않았어요")
                        + (existingMouth ? ", 입 맞추기는 " + (skippedMouth > 0 ? $"빼고 적용했어요 (메시 {skippedMouth}개)" : "쓰지 않았어요") : ""));
                // 경로는 아이템 합치기(본 옮기기) 전에 찾는다 (IR과 같은 계층일 때)
                var renderers = avatar.GetComponentsInChildren<Renderer>(true);
                ApplyTextures(a["textures"]);
                ApplyMaterials(a["materials"], renderers, a["textures"]);
                ApplyMeshes(a["meshes"], renderers, a["ir"].AsStringOrNull);
                foreach (var r in a["renderers"].Items)
                {
                    var t = Find(root, r["path"].AsString);
                    var ren = t != null ? t.GetComponent<Renderer>() : null;
                    if (ren == null) { Warn("렌더러를 찾지 못함: " + r["path"].AsString); continue; }
                    if (r.Has("enabled")) ren.enabled = r["enabled"].AsBool;
                    if (ren is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                        foreach (var kv in r["blendShapes"].Props)
                        {
                            int i = smr.sharedMesh.GetBlendShapeIndex(kv.Key);
                            if (i >= 0) smr.SetBlendShapeWeight(i, kv.Value.AsFloat); else Warn($"블렌드쉐이프 없음: {r["path"].AsString} {kv.Key}");
                        }
                }
                foreach (var n in a["nodes"].Items)
                {
                    var t = Find(root, n["path"].AsString);
                    if (t == null) { Warn("오브젝트를 찾지 못함: " + n["path"].AsString); continue; }
                    t.gameObject.SetActive(n["active"].AsBool);
                }
                foreach (var (go, o) in outfitRoots) MergeOutfit(root, go, o);

                EditorSceneManager.MarkSceneDirty(avatar.scene);
                EditorSceneManager.SaveScene(avatar.scene, _scene);
                AssetDatabase.SaveAssets();
                if (_export)   // 내보내기 폴더에는 쓴 것만 남긴다
                    foreach (var d in new[] { "Materials", "Meshes", "Textures" })
                        if (AssetDatabase.FindAssets("", new[] { _out + "/" + d }).Length == 0) AssetDatabase.DeleteAsset(_out + "/" + d);
                var ex = JNode.Arr(); foreach (var n in existing) ex.Add(n);
                Status(stage, Log.Count(l => l.StartsWith("!")) == 0, _export ? "새 장면으로 내보냈어요" : "업로드할 장면을 만들었어요",
                    JNode.Obj().Set("scene", _scene).Set("avatar", avatar.name).Set("existingBlinkFix", ex).Set("blinkFixSkipped", skippedBlink).Set("mouthFixSkipped", skippedMouth));
                if (_export && !Application.isBatchMode)
                {
                    Selection.activeGameObject = avatar; EditorGUIUtility.PingObject(avatar);
                    if (existing.Count > 0 && skippedBlink + skippedMouth > 0)
                        EditorUtility.DisplayDialog("ALEdit 내보내기", $"원본에 이미 눈 깜빡임 보정({string.Join(", ", existing)})이 있어서, 에디터에서 켠 "
                            + (skippedBlink > 0 && skippedMouth > 0 ? "깜빡임·입 맞추기는" : skippedBlink > 0 ? "깜빡임 맞추기는" : "입 맞추기는") + " 내보내기에서 뺐어요.\n기존 보정은 그대로 남아 있어요.", "확인");
                }
            }
            catch (Exception e) { Status(stage, false, e.Message); Debug.LogException(e); Fail(); }
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ---------------- 원본의 눈 깜빡임 보정 ----------------
        // 도구마다 스크립트가 달라 필드 이름으로 알아본다 (importsim IrBlinkFix.cs와 같은 규칙):
        // 왼눈·오른눈 블렌드쉐이프 목록 필드를 함께 가진 MonoBehaviour, 또는 스크립트 이름이 BlinkFix·EyeFix 꼴인 것
        static readonly System.Text.RegularExpressions.Regex LeftEyeShapes = new System.Text.RegularExpressions.Regex(@"left_?eye.*blend_?shape|blend_?shape.*left_?eye", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex RightEyeShapes = new System.Text.RegularExpressions.Regex(@"right_?eye.*blend_?shape|blend_?shape.*right_?eye", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex MouthShapes = new System.Text.RegularExpressions.Regex(@"mouth.*blend_?shape|blend_?shape.*mouth", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static readonly System.Text.RegularExpressions.Regex BlinkFixScript = new System.Text.RegularExpressions.Regex(@"(blink|eye)[ _\-]?fix", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// 아바타 아래 눈 깜빡임 보정 컴포넌트의 이름 ("오브젝트 (스크립트)"). mouth: 그중 입 블렌드쉐이프 목록이 채워진 것이 있는지
        public static List<string> ExistingBlinkFix(GameObject avatar, out bool mouth)
        {
            var found = new List<string>();
            mouth = false;
            foreach (var mb in avatar.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var fields = new List<string>(); bool hasMouth = false;
                var it = new SerializedObject(mb).GetIterator();
                for (bool enter = true; it.NextVisible(enter); enter = false)
                {
                    fields.Add(it.name);
                    if (it.isArray && it.propertyType != SerializedPropertyType.String && it.arraySize > 0 && MouthShapes.IsMatch(it.name) && it.name.IndexOf("custom", StringComparison.OrdinalIgnoreCase) < 0) hasMouth = true;
                }
                var script = mb.GetType().Name;
                if (fields.Any(LeftEyeShapes.IsMatch) && fields.Any(RightEyeShapes.IsMatch) || BlinkFixScript.IsMatch(script))
                {
                    found.Add($"{mb.gameObject.name} ({script})");
                    mouth |= hasMouth;
                }
            }
            return found;
        }

        /// apply.json 메시 편집에서 kind(blinkFix·mouthFix) 연산을 뺀다. 반환: 연산을 뺀 메시 수
        static int DropFix(JNode meshes, string kind)
        {
            int n = 0;
            foreach (var e in meshes.Items)
            {
                var keep = JNode.Arr(); bool hit = false;
                foreach (var op in e["ops"].Items) if (op["op"].AsString == kind) hit = true; else keep.Add(op);
                if (hit) { e.Set("ops", keep); n++; }
            }
            return n;
        }

        // ---------------- 내보내기 (사용자의 기존 Unity 프로젝트) ----------------
        /// apply.json의 export.dir에 새 장면을 만든다. 창 모드면 먼저 열린 장면 저장 여부를 묻는다 (취소하면 아무것도 하지 않음)
        public static void Export() => Export(false);

        /// upload: 원본 프로젝트에서 올리기 (2026-11-03) — 지난번에 이 아바타로 올린 ID를 넣고, 다 되면 SDK 창·"ALEdit 업로드" 창을 연다
        public static void Export(bool upload)
        {
            Log.Clear();
            JNode a;
            try { a = Apply(); } catch (Exception e) { Status("exported", false, "apply.json을 읽지 못했어요: " + e.Message); Fail(); return; }
            var dir = a["export"]["dir"].AsString;
            if (!dir.StartsWith("Assets/")) { Status("exported", false, "내보낼 폴더가 없어요 (apply.json export.dir)"); Fail(); return; }
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            { Status("exported", false, "열린 장면 저장을 취소해서 내보내지 않았어요"); return; }
            _export = true; _forUpload = upload; _out = dir; _scene = dir + "/" + Safe(a["name"].AsString.Length > 0 ? a["name"].AsString : "Avatar") + ".unity";
            UploadKey = upload ? KeyOf(a) : null;
            try { Assemble(); }
            finally { _export = false; _forUpload = false; _out = "Assets/VrcProto"; _scene = ScenePath; }
            if (upload && !Application.isBatchMode) OpenExportedForUpload();
        }

        /// 원본 프로젝트에서 올리기: 방금 만든 장면의 아바타로 SDK 창과 "ALEdit 업로드" 창을 연다
        static void OpenExportedForUpload()
        {
            JNode st;
            try { st = JNode.Parse(File.ReadAllText(StatusPath)); } catch (Exception) { return; }
            if (st["stage"].AsString != "exported" || st["scene"].IsNull) return;
            var scene = st["scene"].AsString;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != scene) EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            var avatar = SceneAvatar();
            if (avatar != null) { Selection.activeGameObject = avatar; EditorGUIUtility.PingObject(avatar); }
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            if (avatar != null) UploadWindow.Open(avatar, Apply()["upload"]);
            else Warn("! 장면에서 아바타(VRCAvatarDescriptor)를 찾지 못했어요");
            Status("opened", avatar != null, avatar != null ? "새 장면을 만들고 올리기 창을 열었어요. VRChat SDK 창에서 로그인한 뒤 올려 주세요" : "장면에서 아바타를 찾지 못했어요",
                JNode.Obj().Set("scene", scene));
        }

        /// 내보내기 프로젝트(apply.json에 export가 있음)에서는 올리기용 메뉴를 쓰지 않는다 (임포트 설정을 고치고 장면을 지우므로)
        static bool IsExportProject()
        {
            try { return File.Exists(ApplyPath) && Apply().Has("export"); } catch (Exception) { return false; }
        }

        static void Fail() { if (Application.isBatchMode) EditorApplication.Exit(1); }

        /// 창 모드에서 장면을 다시 만든다 (업로드 스크립트를 고친 뒤나 SDK 검사에 걸린 뒤). 지난 사본 머티리얼·메시는 지우고 새로 만든다
        [MenuItem("ALEdit/업로드 장면 다시 만들기")]
        static void Rebuild()
        {
            if (IsExportProject()) { EditorUtility.DisplayDialog("ALEdit", "이 프로젝트는 내보내기용이라 업로드 장면을 만들지 않아요. 에디터에서 다시 내보내 주세요.", "확인"); return; }
            var cur = SceneAvatar();
            if (cur != null) RememberSceneId(cur);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var d in new[] { "Materials", "Meshes" }) AssetDatabase.DeleteAsset("Assets/VrcProto/" + d);
            AssetDatabase.DeleteAsset(ScenePath);
            Log.Clear();
            Assemble();
            if (File.Exists(StatusPath) && JNode.Parse(File.ReadAllText(StatusPath))["ok"].AsBool) OpenForUpload();
            else EditorUtility.DisplayDialog("ALEdit", "장면을 만들지 못했어요. 콘솔의 [VrcProto] 로그를 봐 주세요.", "확인");
        }

        static GameObject PlaceAvatar(JNode av)
        {
            var path = AssetDatabase.GUIDToAssetPath(av["guid"].AsString);
            if (string.IsNullOrEmpty(path)) path = av["path"].AsString;
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (_export) _scene = AssetDatabase.GenerateUniqueAssetPath(_scene);
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) return null;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                return go;
            }
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                // 장면 원본: 사본을 만들어 열고, IR의 루트(파일 안 fileID)와 같은 오브젝트를 찾는다. 다른 아바타는 꺼 둔다
                // 원본 장면은 열지 않고 사본만 연다
                if (_export) _scene = AssetDatabase.GenerateUniqueAssetPath(_scene);
                if (!AssetDatabase.CopyAsset(path, _scene)) return null;
                var scene = EditorSceneManager.OpenScene(_scene, OpenSceneMode.Single);
                long want = av["root"].AsLong;
                GameObject found = null;
                foreach (var g in scene.GetRootGameObjects())
                    foreach (var t in g.GetComponentsInChildren<Transform>(true))
                    {
                        var id = GlobalObjectId.GetGlobalObjectIdSlow(t.gameObject);
                        if ((long)id.targetObjectId == want || (long)id.targetPrefabId == want) { found = t.gameObject; break; }
                    }
                if (found != null)
                    foreach (var g in scene.GetRootGameObjects())
                        if (g != found && g.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().Name == "VRCAvatarDescriptor")) g.SetActive(false);
                return found;
            }
            return null;
        }

        static GameObject PlaceOutfit(Transform root, JNode o)
        {
            var path = AssetDatabase.GUIDToAssetPath(o["prefab"].AsString);
            var prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Warn("! 아이템 프리팹을 찾지 못함: " + o["name"].AsString); return null; }
            var parent = Find(root, o["parent"].AsString) ?? root;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            var f = o["trs"].ToFloatArray();
            if (f.Length >= 10)
            {
                go.transform.localPosition = new Vector3(f[0], f[1], f[2]);
                go.transform.localRotation = new Quaternion(f[3], f[4], f[5], f[6]);
                go.transform.localScale = new Vector3(f[7], f[8], f[9]);
            }
            go.SetActive(!o.Has("active") || o["active"].AsBool);
            Info($"아이템: {go.name} → {(parent == root ? "아바타 루트" : parent.name)} ({o["method"].AsString})");
            return go;
        }

        /// MA 설정이 없는 의상(본 이름 맞춤): MA가 있으면 MA Setup Outfit으로, 없으면 같은 이름 본 아래로 옮긴다 (월드 위치 유지)
        static void MergeOutfit(Transform root, GameObject go, JNode o)
        {
            if (o["method"].AsString != "name") return;
            var setup = FindType("nadena.dev.modular_avatar.core.editor.SetupOutfit");
            var m = setup?.GetMethod("SetupOutfitUI", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(GameObject) }, null);
            if (m != null)
            {
                try { m.Invoke(null, new object[] { go }); Info($"{go.name}: Modular Avatar Setup Outfit"); return; }
                catch (Exception e) { Warn($"{go.name}: MA Setup Outfit 실패, 본 이름으로 옮겨요 — {e.InnerException?.Message ?? e.Message}"); }
            }
            var avatarBones = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (!t.IsChildOf(go.transform) && !avatarBones.ContainsKey(t.name)) avatarBones[t.name] = t;
            int moved = 0;
            foreach (var t in go.GetComponentsInChildren<Transform>(true).Where(x => x != go.transform).ToList())
                if (avatarBones.TryGetValue(t.name, out var target) && t.parent != null && !avatarBones.ContainsKey(t.parent.name)) { t.SetParent(target, true); t.name = t.name + "_" + go.name; moved++; }
            Info($"{go.name}: 본 {moved}개를 아바타 본 아래로 옮김");
        }

        /// VRChat SDK 업로드 검사가 막는 FBX 임포트 설정을 SDK 창의 Auto Fix와 같게 고친다 (이 업로드 프로젝트 안의 사본만):
        /// Read/Write 켜기, 블렌드쉐이프 노멀이 Calculate면 Legacy Blendshape Normals 켜기. 메시 편집 전에 해야 Unity 메시가 바뀐 설정으로 읽힌다.
        static void FixModelImporters(GameObject avatar)
        {
            var paths = new HashSet<string>();
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
            {
                var m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
                if (m != null) { var p = AssetDatabase.GetAssetPath(m); if (!string.IsNullOrEmpty(p)) paths.Add(p); }
            }
            foreach (var p in paths)
            {
                if (!(AssetImporter.GetAtPath(p) is ModelImporter mi)) continue;
                var fixes = new List<string>();
                if (!mi.isReadable) { mi.isReadable = true; fixes.Add("Read/Write 켬"); }
                if (mi.importBlendShapeNormals == ModelImporterNormals.Calculate)
                {
                    var so = new SerializedObject(mi);
                    var legacy = so.FindProperty("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes");
                    if (legacy != null && !legacy.boolValue) { legacy.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); fixes.Add("Legacy Blendshape Normals 켬"); }
                }
                if (fixes.Count == 0) continue;
                mi.SaveAndReimport();
                Info($"임포트 설정 (VRChat 업로드 검사): {p} — {string.Join(", ", fixes)}");
            }
            // 텍스처: 밉맵이 있으면 Mip Streaming 켜기 (SDK 검사가 요구)
            var texPaths = new HashSet<string>();
            foreach (var r in avatar.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || m.shader == null) continue;
                    foreach (var name in m.GetTexturePropertyNames())
                    {
                        var t = m.GetTexture(name);
                        var p = t != null ? AssetDatabase.GetAssetPath(t) : null;
                        if (!string.IsNullOrEmpty(p)) texPaths.Add(p);
                    }
                }
            int streaming = 0;
            foreach (var p in texPaths)
            {
                if (!(AssetImporter.GetAtPath(p) is TextureImporter ti) || !ti.mipmapEnabled || ti.streamingMipmaps) continue;
                ti.streamingMipmaps = true;
                ti.SaveAndReimport();
                streaming++;
            }
            if (streaming > 0) Info($"임포트 설정 (VRChat 업로드 검사): 텍스처 {streaming}개 Mip Streaming 켬");
        }

        // ---------------- 에셋 ----------------
        static UnityEngine.Object LoadByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var p = key.Split(':');
            var path = AssetDatabase.GUIDToAssetPath(p[0]);
            if (string.IsNullOrEmpty(path)) return null;
            if (p.Length == 1) return AssetDatabase.LoadMainAssetAtPath(path);
            long fid = long.Parse(p[1]);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
                if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long l) && l == fid) return o;
            return null;
        }

        static readonly Dictionary<string, Texture> _copiedTex = new Dictionary<string, Texture>();

        /// 원본 패키지에 없는 텍스처(Host에서 만든 것)는 파일을 복사해 가져온다
        static void ApplyTextures(JNode textures)
        {
            foreach (var kv in textures.Props)
            {
                if (LoadByKey(kv.Key) is Texture) continue;
                var file = kv.Value["file"].AsString;
                if (!File.Exists(file)) { Warn("! 텍스처 파일 없음: " + file); continue; }
                var dst = AssetDatabase.GenerateUniqueAssetPath(_out + "/Textures/" + Path.GetFileName(file));
                File.Copy(file, Path.Combine(ProjectDir, dst), true);
                AssetDatabase.ImportAsset(dst);
                // 색 굽기 결과: 원본 텍스처의 임포트 설정(sRGB·밉맵·wrap·최대 크기·압축)을 그대로 따른다 (lilToon 굽기의 CopyTextureSetting과 같음)
                if (kv.Value["like"].AsStringOrNull is string like && LoadByKey(like) is Texture lt
                    && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(lt)) is TextureImporter from && AssetImporter.GetAtPath(dst) is TextureImporter to)
                {
                    var st = new TextureImporterSettings(); from.ReadTextureSettings(st); to.SetTextureSettings(st);
                    to.maxTextureSize = from.maxTextureSize; to.textureCompression = from.textureCompression; to.crunchedCompression = from.crunchedCompression; to.compressionQuality = from.compressionQuality;
                    to.SaveAndReimport();
                }
                if (AssetImporter.GetAtPath(dst) is TextureImporter ti && ti.mipmapEnabled && !ti.streamingMipmaps) { ti.streamingMipmaps = true; ti.SaveAndReimport(); }
                _copiedTex[kv.Key] = AssetDatabase.LoadAssetAtPath<Texture>(dst);
            }
        }

        static void ApplyMaterials(JNode mats, Renderer[] renderers, JNode textures)
        {
            foreach (var m in mats.Items)
            {
                var key = m["key"].AsString;
                var orig = LoadByKey(key) as Material;
                if (orig == null) { Warn("! 머티리얼을 찾지 못함: " + m["name"].AsString); continue; }
                var copy = new Material(orig) { name = orig.name + " (VrcProto)" };
                if (m.Has("shader"))
                {
                    var sh = Shader.Find(m["shader"].AsString);
                    if (sh != null) { int rq = copy.renderQueue; copy.shader = sh; copy.renderQueue = rq; } else Warn("셰이더 없음: " + m["shader"].AsString);
                }
                foreach (var kv in m["floats"].Props) if (copy.HasProperty(kv.Key)) copy.SetFloat(kv.Key, kv.Value.AsFloat);
                foreach (var kv in m["colors"].Props) if (copy.HasProperty(kv.Key)) { var c = kv.Value.ToFloatArray(); copy.SetColor(kv.Key, new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1)); }
                foreach (var kv in m["vectors"].Props) if (copy.HasProperty(kv.Key)) { var c = kv.Value.ToFloatArray(); copy.SetVector(kv.Key, new Vector4(c[0], c[1], c[2], c.Length > 3 ? c[3] : 0)); }
                foreach (var kv in m["textures"].Props)
                {
                    if (!copy.HasProperty(kv.Key)) continue;
                    var tk = kv.Value.AsStringOrNull;
                    Texture tex = tk == null ? null : _copiedTex.TryGetValue(tk, out var ct) ? ct : LoadByKey(tk) as Texture;
                    if (tk != null && tex == null) Warn($"텍스처를 찾지 못함: {m["name"].AsString}.{kv.Key}");
                    copy.SetTexture(kv.Key, tex);
                }
                foreach (var kv in m["texST"].Props) if (copy.HasProperty(kv.Key)) { var c = kv.Value.ToFloatArray(); copy.SetTextureScale(kv.Key, new Vector2(c[0], c[1])); copy.SetTextureOffset(kv.Key, new Vector2(c[2], c[3])); }
                foreach (var kv in m["keywords"].Props) { if (kv.Value.AsBool) copy.EnableKeyword(kv.Key); else copy.DisableKeyword(kv.Key); }
                if (m.Has("renderQueue")) copy.renderQueue = m["renderQueue"].AsInt;
                var path = AssetDatabase.GenerateUniqueAssetPath(_out + "/Materials/" + Safe(orig.name) + ".mat");
                AssetDatabase.CreateAsset(copy, path);
                int used = 0;
                foreach (var r in renderers)
                {
                    var ms = r.sharedMaterials; bool hit = false;
                    for (int i = 0; i < ms.Length; i++) if (ms[i] == orig) { ms[i] = copy; hit = true; used++; }
                    if (hit) r.sharedMaterials = ms;
                }
                Info($"머티리얼 사본: {path} (슬롯 {used}개)");
            }
        }

        /// 메시 편집. 연산의 정점 번호는 IR 메시(툴 화면) 기준이고 Unity 정점 순서는 IR과 다르므로(최적화된 삼각형 목록의 첫 사용 순서),
        /// IR 메시에 연산을 적용한 뒤 그 결과의 정점마다 짝지은 Unity 정점의 속성을 옮겨 담는다 (TransferToUnity).
        /// IR 파일(apply.json "ir")이 없으면 예전처럼 정점 수가 같을 때만 Unity 메시에 그대로 적용한다.
        static void ApplyMeshes(JNode meshes, Renderer[] renderers, string irPath)
        {
            if (meshes.Count == 0) return;
            Protocol.Ir.IrScene ir = null; Protocol.Ir.IrBuffers buf = null;
            if (!string.IsNullOrEmpty(irPath) && File.Exists(irPath))
                try { ir = Protocol.Ir.IrScene.Load(irPath); if (File.Exists(ir.BufferFile)) buf = new Protocol.Ir.IrBuffers(ir.BufferFile); }
                catch (Exception ex) { Warn("IR을 읽지 못함 (" + irPath + "): " + ex.Message); }
            if (buf == null) Warn("IR 메시를 읽지 못해 정점 번호를 Unity 메시에 그대로 써요 (정점 순서가 달라 엉뚱한 정점에 적용될 수 있음)");
            try { ApplyMeshes(meshes, renderers, ir, buf); }
            finally { buf?.Dispose(); }
        }

        static void ApplyMeshes(JNode meshes, Renderer[] renderers, Protocol.Ir.IrScene ir, Protocol.Ir.IrBuffers buf)
        {
            foreach (var e in meshes.Items)
            {
                var orig = LoadByKey(e["key"].AsString) as Mesh;
                if (orig == null) { Warn("! 메시를 찾지 못함: " + e["name"].AsString); continue; }
                var irMesh = buf != null ? ir.FindMesh(e["key"].AsString) : null;
                // 깜빡임·입 맞추기(blinkFix·mouthFix)는 블렌드쉐이프 델타만 바꾼다. TransferToUnity는 짝지은 정점의 델타를 Unity 원본에서 가져오므로
                // 나머지 연산을 옮긴 뒤에 Unity 메시에 바로 적용한다 (정점 번호를 쓰지 않는 연산)
                var blink = e["ops"].Items.Where(op => MeshOps.IsShapeFix(op["op"].AsString)).ToList();
                var ops = JNode.Arr(); foreach (var op in e["ops"].Items) if (!MeshOps.IsShapeFix(op["op"].AsString)) ops.Add(op);
                MeshData edited;
                if (ops.Count == 0) edited = ToData(orig);
                else if (irMesh != null)
                    edited = TransferToUnity(orig.name, ToData(orig), MeshData.Load(irMesh, buf), ops);
                else
                {
                    if (orig.vertexCount != e["vertexCount"].AsInt)
                    {
                        // 정점 번호가 툴과 달라도 고른 정점 없는 weld·decimate는 번호를 쓰지 않으므로 그대로 적용할 수 있다
                        bool indexFree = ops.Items.All(op => op["op"].AsString == MeshOps.Weld && op["sel"].IsNull || op["op"].AsString == MeshOps.Decimate);
                        if (!indexFree) { Warn($"! {orig.name}: 정점 수가 달라 메시 편집을 건너뜀 (Unity {orig.vertexCount}, 툴 {e["vertexCount"].AsInt})"); continue; }
                        Info($"{orig.name}: 정점 수가 툴과 달라요 (Unity {orig.vertexCount}, 툴 {e["vertexCount"].AsInt}) — 정점 합치기·줄이기는 Unity 메시에 그대로 적용");
                    }
                    edited = MeshOps.Apply(ToData(orig), ops);
                }
                foreach (var op in blink)
                {
                    if (MeshOps.IsOff(op)) continue;
                    if (op["op"].AsString == MeshOps.ShapeMove)
                    {
                        // 다문 입 다듬기: 위치로 찾은 정점 수를 남긴다 (Unity 메시와 툴 메시의 위치가 맞는지 확인용)
                        var sh = edited.Shapes.Find(s => s.Name == op["shape"].AsString);
                        var before = sh?.Frames.Count > 0 ? (float[])sh.Frames[sh.Frames.Count - 1].DPos?.Clone() : null;
                        MeshOps.ApplyOne(edited, op);
                        var after = sh?.Frames.Count > 0 ? sh.Frames[sh.Frames.Count - 1].DPos : null;
                        int hit = 0;
                        if (before != null && after != null) for (int v = 0; v * 3 + 2 < after.Length; v++) if (Math.Abs(after[v * 3] - before[v * 3]) + Math.Abs(after[v * 3 + 1] - before[v * 3 + 1]) + Math.Abs(after[v * 3 + 2] - before[v * 3 + 2]) > 1e-9f) hit++;
                        if (hit == 0) Warn($"! {orig.name}: 다문 입 다듬기({op["shape"].AsString})의 정점을 Unity 메시에서 찾지 못했어요");
                        else Info($"{orig.name}: 다문 입 다듬기 ({op["shape"].AsString}, 점 {op["points"].Count / 6}개 → 정점 {hit}개)");
                        continue;
                    }
                    MeshOps.ApplyOne(edited, op);
                    Info(op["op"].AsString == MeshOps.MouthFix ? $"{orig.name}: 입 맞추기 (입 벌리기 {op["targets"].Count}개, 입 편집 {op["sources"].Count}개)"
                                                              : $"{orig.name}: 깜빡임 맞추기 (깜빡임 {op["targets"].Count}개, 눈 편집 {op["sources"].Count}개)");
                }
                var mesh = new Mesh { name = orig.name + " (VrcProto)" };
                Fill(mesh, edited);
                mesh.bounds = orig.bounds;
                mesh.RecalculateBounds();
                var path = AssetDatabase.GenerateUniqueAssetPath(_out + "/Meshes/" + Safe(orig.name) + ".asset");
                AssetDatabase.CreateAsset(mesh, path);
                int used = 0;
                foreach (var r in renderers)
                {
                    if (r is SkinnedMeshRenderer smr && smr.sharedMesh == orig)
                    {
                        var w = new float[orig.blendShapeCount]; for (int i = 0; i < w.Length; i++) w[i] = smr.GetBlendShapeWeight(i);
                        smr.sharedMesh = mesh; for (int i = 0; i < w.Length && i < mesh.blendShapeCount; i++) smr.SetBlendShapeWeight(i, w[i]);
                        used++;
                    }
                    else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh == orig) { mf.sharedMesh = mesh; used++; }
                }
                Info($"메시 편집: {orig.name} 정점 {orig.vertexCount} → {mesh.vertexCount}, 삼각형 {Tris(orig)} → {Tris(mesh)} (렌더러 {used}개)");
            }
        }

        static long Tris(Mesh m) { long t = 0; for (int s = 0; s < m.subMeshCount; s++) if (m.GetTopology(s) == MeshTopology.Triangles) t += m.GetIndexCount(s) / 3; else if (m.GetTopology(s) == MeshTopology.Quads) t += m.GetIndexCount(s) / 2; return t; }

        // ---------------- IR 정점 ↔ Unity 정점 ----------------
        const string TagShape = "__vrcproto_origin__";

        /// <summary>
        /// IR 메시 s에 연산을 적용한 결과(툴 화면과 같은 메시)를 Unity 메시 u의 속성으로 채워 돌려준다.
        /// 결과 정점마다 원래 IR 정점(추적용 블렌드쉐이프 DNrm에 번호를 실어 연산을 통과시킨다)과 짝지은 Unity 정점의 노멀·탄젠트·UV·색·본·블렌드쉐이프를 쓰고,
        /// 위치는 연산이 바꾸지 않았으면 Unity 값, 바꿨으면 결과 값. 짝 없는 IR 정점은 IR 값 그대로.
        /// Unity가 IR보다 더 나눈 정점(한 IR 정점에 Unity 정점 여럿)은 IR처럼 하나가 된다.
        /// </summary>
        static MeshData TransferToUnity(string name, MeshData u, MeshData s, JNode ops)
        {
            var iToU = MatchVertices(u, s, out int strict, out int loose, out int split);

            var src = s.Clone();
            var tag = new float[src.VertexCount * 3];
            for (int i = 0; i < src.VertexCount; i++) tag[i * 3] = i;
            src.Shapes.Add(new MeshData.Shape { Name = TagShape, Frames = { new MeshData.Frame { Weight = 100, DNrm = tag } } });
            var e = MeshOps.Apply(src, ops);
            var tagShape = e.Shapes.First(x => x.Name == TagShape);
            e.Shapes.Remove(tagShape);
            var origin = tagShape.Frames[0].DNrm;

            int n = e.VertexCount;
            var r = new MeshData { VertexCount = n, Pos = new float[n * 3], Bindposes = u.Bindposes ?? e.Bindposes, Subs = e.Subs };
            if (u.Nrm != null || e.Nrm != null) r.Nrm = new float[n * 3];
            if (u.Tan != null || e.Tan != null) r.Tan = new float[n * 4];
            if (u.Col != null || e.Col != null) r.Col = new float[n * 4];
            for (int c = 0; c < 4; c++) if (u.Uv[c] != null || e.Uv[c] != null) { r.UvDims[c] = u.Uv[c] != null ? u.UvDims[c] : e.UvDims[c]; r.Uv[c] = new float[n * r.UvDims[c]]; }
            if (u.BoneW != null || e.BoneW != null) { r.BoneIdx = new int[n * 4]; r.BoneW = new float[n * 4]; }

            // 블렌드쉐이프는 Unity 메시의 이름·프레임 그대로 (짝 없는 정점은 IR의 같은 이름 셰이프 델타)
            var eShapes = new Dictionary<string, MeshData.Shape>();
            foreach (var sh in e.Shapes) eShapes[sh.Name] = sh;
            foreach (var sh in u.Shapes)
            {
                var ns = new MeshData.Shape { Name = sh.Name };
                foreach (var f in sh.Frames) ns.Frames.Add(new MeshData.Frame { Weight = f.Weight, DPos = new float[n * 3], DNrm = f.DNrm != null ? new float[n * 3] : null, DTan = f.DTan != null ? new float[n * 3] : null });
                r.Shapes.Add(ns);
            }
            int missingShapes = e.Shapes.Count(sh => !u.Shapes.Any(x => x.Name == sh.Name));

            void Copy(float[] from, int fi, float[] to, int ti, int dimFrom, int dimTo)
            {
                if (to == null) return;
                for (int k = 0; k < dimTo; k++) to[ti * dimTo + k] = from != null && k < dimFrom ? from[fi * dimFrom + k] : 0;
            }
            int moved = 0, fromIr = 0;
            for (int k = 0; k < n; k++)
            {
                int o = (int)origin[k * 3];
                int ui = o >= 0 && o < iToU.Length ? iToU[o] : -1;
                bool same = o >= 0 && o < s.VertexCount && e.Pos[k * 3] == s.Pos[o * 3] && e.Pos[k * 3 + 1] == s.Pos[o * 3 + 1] && e.Pos[k * 3 + 2] == s.Pos[o * 3 + 2];
                if (!same) moved++;
                if (ui < 0) fromIr++;
                Copy(ui >= 0 && same ? u.Pos : e.Pos, ui >= 0 && same ? ui : k, r.Pos, k, 3, 3);
                var (m, mi) = ui >= 0 ? (u, ui) : (e, k);
                Copy(m.Nrm, mi, r.Nrm, k, 3, 3);
                Copy(m.Tan, mi, r.Tan, k, 4, 4);
                if (r.Col != null) { if (m.Col != null) Copy(m.Col, mi, r.Col, k, 4, 4); else for (int c = 0; c < 4; c++) r.Col[k * 4 + c] = 1; }
                for (int c = 0; c < 4; c++) Copy(m.Uv[c], mi, r.Uv[c], k, m.UvDims[c], r.UvDims[c]);
                if (r.BoneW != null)
                {
                    Copy(m.BoneW, mi, r.BoneW, k, 4, 4);
                    if (m.BoneIdx != null) Array.Copy(m.BoneIdx, mi * 4, r.BoneIdx, k * 4, 4);
                }
                for (int b = 0; b < u.Shapes.Count; b++)
                {
                    var ush = u.Shapes[b]; var rsh = r.Shapes[b];
                    eShapes.TryGetValue(ush.Name, out var esh);
                    for (int f = 0; f < rsh.Frames.Count; f++)
                    {
                        MeshData.Frame from; int fi;
                        if (ui >= 0) { from = ush.Frames[f]; fi = ui; }
                        else if (esh != null && esh.Frames.Count > 0) { from = esh.Frames[Math.Min(f, esh.Frames.Count - 1)]; fi = k; }
                        else continue;
                        Copy(from.DPos, fi, rsh.Frames[f].DPos, k, 3, 3);
                        Copy(from.DNrm, fi, rsh.Frames[f].DNrm, k, 3, 3);
                        Copy(from.DTan, fi, rsh.Frames[f].DTan, k, 3, 3);
                    }
                }
            }
            Info($"{name}: 정점 짝 — IR {s.VertexCount}개 중 {strict}개 같은 값, {loose}개 위치·UV로, {s.VertexCount - strict - loose}개 짝 없음 (Unity {u.VertexCount}개 중 {split}개는 IR 정점 하나에 여럿)"
                + $" → 결과 정점 {n}개 중 Unity 값 {n - fromIr}개, IR 값 {fromIr}개, 위치 바뀜 {moved}개" + (missingShapes > 0 ? $", Unity에 없는 블렌드쉐이프 {missingShapes}개" : ""));
            if (BoneOrderMismatch(u, s, iToU) is int bad && bad > 0) Warn($"{name}: 짝지은 정점 {bad}개의 본 번호가 IR과 달라요 (짝 없는 정점의 본 가중치가 틀릴 수 있음)");
            return r;
        }

        static float Dot3(float[] a, int i, float[] b, int j) => a[i * 3] * b[j * 3] + a[i * 3 + 1] * b[j * 3 + 1] + a[i * 3 + 2] * b[j * 3 + 2];

        static bool Close(float[] a, int i, float[] b, int j, int dim, float tol)
        {
            if (a == null || b == null) return true;
            for (int k = 0; k < dim; k++) if (Math.Abs(a[i * dim + k] - b[j * dim + k]) > tol) return false;
            return true;
        }

        static float Dist2(float[] a, int i, float[] b, int j, int dim)
        {
            float s = 0;
            for (int k = 0; k < dim; k++) { float x = a[i * dim + k] - b[j * dim + k]; s += x * x; }
            return s;
        }

        /// <summary>
        /// IR 정점마다 짝지은 Unity 정점 (없으면 -1). importsim/tests/compare_geo.py match_vertices와 같은 기준:
        /// 1) 위치(크기 비례 오차)·노멀·UV0·탄젠트가 같은 값. 겹친 후보는 위치 차 + 블렌드쉐이프 델타 차가 가장 작은 쪽.
        /// 2) 1에서 짝 없는 IR 정점은 같은 위치·UV0(거리 0.001)·탄젠트 방향 부호가 같은 Unity 정점 중 노멀이 가장 가까운 것 (importsim이 노멀을 다르게 계산한 곳).
        /// split = 이미 짝이 있는 IR 정점에 또 짝지어진 Unity 정점 수 (Unity가 더 나눈 곳).
        /// </summary>
        static int[] MatchVertices(MeshData u, MeshData s, out int strict, out int loose, out int split)
        {
            int nu = u.VertexCount, ns = s.VertexCount;
            float ext = 1e-3f;
            foreach (var x in u.Pos) ext = Math.Max(ext, Math.Abs(x));
            float ptol = Math.Max(2e-5f, ext * 2e-6f), step = ptol * 4;
            var cells = new Dictionary<(int, int, int), List<int>>();
            (int, int, int) Cell(float[] p, int i) => ((int)Math.Floor(p[i * 3] / step), (int)Math.Floor(p[i * 3 + 1] / step), (int)Math.Floor(p[i * 3 + 2] / step));
            for (int j = 0; j < ns; j++) { var c = Cell(s.Pos, j); if (!cells.TryGetValue(c, out var l)) cells[c] = l = new List<int>(); l.Add(j); }
            IEnumerable<int> Near(float[] p, int i)
            {
                var (x, y, z) = Cell(p, i);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (cells.TryGetValue((x + dx, y + dy, z + dz), out var l)) foreach (var j in l) yield return j;
            }

            // 블렌드쉐이프 델타 차 (이름·프레임 번호로 맞춤)
            var shapePairs = new List<(MeshData.Frame a, MeshData.Frame b)>();
            foreach (var sh in u.Shapes)
            {
                var o = s.Shapes.FirstOrDefault(x => x.Name == sh.Name);
                if (o != null) for (int f = 0; f < Math.Min(sh.Frames.Count, o.Frames.Count); f++) if (sh.Frames[f].DPos != null && o.Frames[f].DPos != null) shapePairs.Add((sh.Frames[f], o.Frames[f]));
            }
            float ShapeDist(int i, int j) { float d = 0; foreach (var (a, b) in shapePairs) d += Dist2(a.DPos, i, b.DPos, j, 3); return d; }

            bool useT = u.Tan != null && s.Tan != null;
            var uv0u = u.Uv[0]; var uv0s = s.Uv[0]; int du = u.UvDims[0], ds = s.UvDims[0];
            bool Uv(int i, int j, float tol)
            {
                if (uv0u == null || uv0s == null) return true;
                if (Math.Abs(uv0u[i * du] - uv0s[j * ds]) > tol || Math.Abs(uv0u[i * du + 1] - uv0s[j * ds + 1]) > tol) return false;
                return true;
            }

            var iToU = new int[ns]; for (int j = 0; j < ns; j++) iToU[j] = -1;
            var uToI = new int[nu];
            strict = 0; loose = 0; split = 0;
            var good = new List<int>();
            for (int i = 0; i < nu; i++)
            {
                uToI[i] = -1;
                good.Clear();
                foreach (var j in Near(u.Pos, i))
                {
                    if (!Close(u.Pos, i, s.Pos, j, 3, ptol) || !Close(u.Nrm, i, s.Nrm, j, 3, 2e-3f) || !Uv(i, j, 2e-4f)) continue;
                    if (useT && (u.Tan[i * 4 + 3] != s.Tan[j * 4 + 3] || !CloseXyz(u.Tan, i, s.Tan, j))) continue;
                    good.Add(j);
                }
                if (good.Count == 0) continue;
                int best = -1; float bestCost = float.MaxValue;
                foreach (var j in good)
                {
                    if (iToU[j] >= 0) continue;
                    float cost = good.Count > 1 ? Dist2(u.Pos, i, s.Pos, j, 3) + ShapeDist(i, j) : 0;
                    if (cost < bestCost) { bestCost = cost; best = j; }
                }
                if (best >= 0) { iToU[best] = i; uToI[i] = best; strict++; }
                else { uToI[i] = good[0]; split++; }
            }

            // 2) 노멀이 다르게 계산된 곳: 같은 위치·UV인 Unity 정점 중 노멀이 가장 가까운 것 (짝 없는 Unity 정점을 먼저)
            var ucells = new Dictionary<(int, int, int), List<int>>();
            for (int i = 0; i < nu; i++) { var c = Cell(u.Pos, i); if (!ucells.TryGetValue(c, out var l)) ucells[c] = l = new List<int>(); l.Add(i); }
            for (int j = 0; j < ns; j++)
            {
                if (iToU[j] >= 0) continue;
                var (x, y, z) = Cell(s.Pos, j);
                int best = -1; float bestCost = float.MaxValue;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                {
                    if (!ucells.TryGetValue((x + dx, y + dy, z + dz), out var l)) continue;
                    foreach (var i in l)
                    {
                        if (!Close(u.Pos, i, s.Pos, j, 3, ptol) || !Uv(i, j, 1e-3f)) continue;
                        if (useT && u.Tan[i * 4 + 3] * s.Tan[j * 4 + 3] < 0) continue;
                        float cost = (u.Nrm != null && s.Nrm != null ? 1 - Dot3(u.Nrm, i, s.Nrm, j) : 0) + ShapeDist(i, j) + (uToI[i] >= 0 ? 10 : 0);
                        if (cost < bestCost) { bestCost = cost; best = i; }
                    }
                }
                if (best >= 0) { iToU[j] = best; loose++; }
            }
            return iToU;
        }

        /// <summary>탄젠트 xyz만 비교 (w는 따로 같아야 함).</summary>
        static bool CloseXyz(float[] a, int i, float[] b, int j)
        {
            for (int k = 0; k < 3; k++) if (Math.Abs(a[i * 4 + k] - b[j * 4 + k]) > 2e-3f) return false;
            return true;
        }

        /// <summary>짝지은 정점 중 같은 가중치가 다른 본 번호에 실린 수 (IR과 Unity의 본 순서가 다르면 0보다 큼).</summary>
        static int BoneOrderMismatch(MeshData u, MeshData s, int[] iToU)
        {
            if (u.BoneIdx == null || s.BoneIdx == null || u.BoneW == null || s.BoneW == null) return 0;
            int bad = 0;
            for (int j = 0; j < iToU.Length; j++)
            {
                int i = iToU[j]; if (i < 0) continue;
                for (int a = 0; a < 4; a++)
                {
                    float w = s.BoneW[j * 4 + a]; if (w <= 0.01f) continue;
                    bool ok = false;
                    for (int b = 0; b < 4; b++) if (u.BoneIdx[i * 4 + b] == s.BoneIdx[j * 4 + a] && Math.Abs(u.BoneW[i * 4 + b] - w) < 0.02f) ok = true;
                    if (!ok) { bad++; break; }
                }
            }
            return bad;
        }

        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        static float[] Flat(List<Vector3> v) { var f = new float[v.Count * 3]; for (int i = 0; i < v.Count; i++) { f[i * 3] = v[i].x; f[i * 3 + 1] = v[i].y; f[i * 3 + 2] = v[i].z; } return f; }
        static float[] Flat(List<Vector4> v, int dim) { var f = new float[v.Count * dim]; for (int i = 0; i < v.Count; i++) for (int k = 0; k < dim; k++) f[i * dim + k] = v[i][k]; return f; }

        /// Unity 메시 → MeshData (툴의 IR 메시와 같은 배열 형식)
        static MeshData ToData(Mesh m)
        {
            int n = m.vertexCount;
            var d = new MeshData { VertexCount = n };
            var v3 = new List<Vector3>(); var v4 = new List<Vector4>();
            m.GetVertices(v3); d.Pos = Flat(v3);
            if (m.HasVertexAttribute(VertexAttribute.Normal)) { m.GetNormals(v3); d.Nrm = Flat(v3); }
            if (m.HasVertexAttribute(VertexAttribute.Tangent)) { m.GetTangents(v4); d.Tan = Flat(v4, 4); }
            if (m.HasVertexAttribute(VertexAttribute.Color)) { var c = new List<Color>(); m.GetColors(c); d.Col = new float[n * 4]; for (int i = 0; i < n; i++) { d.Col[i * 4] = c[i].r; d.Col[i * 4 + 1] = c[i].g; d.Col[i * 4 + 2] = c[i].b; d.Col[i * 4 + 3] = c[i].a; } }
            for (int ch = 0; ch < 4; ch++)
            {
                var attr = VertexAttribute.TexCoord0 + ch;
                if (!m.HasVertexAttribute(attr)) continue;
                int dim = m.GetVertexAttributeDimension(attr);
                m.GetUVs(ch, v4); d.Uv[ch] = Flat(v4, dim); d.UvDims[ch] = dim;
            }
            var bw = m.boneWeights;
            if (bw.Length == n)
            {
                d.BoneIdx = new int[n * 4]; d.BoneW = new float[n * 4];
                for (int i = 0; i < n; i++)
                {
                    var w = bw[i];
                    d.BoneIdx[i * 4] = w.boneIndex0; d.BoneIdx[i * 4 + 1] = w.boneIndex1; d.BoneIdx[i * 4 + 2] = w.boneIndex2; d.BoneIdx[i * 4 + 3] = w.boneIndex3;
                    d.BoneW[i * 4] = w.weight0; d.BoneW[i * 4 + 1] = w.weight1; d.BoneW[i * 4 + 2] = w.weight2; d.BoneW[i * 4 + 3] = w.weight3;
                }
            }
            var bp = m.bindposes;
            if (bp.Length > 0) { d.Bindposes = new float[bp.Length * 16]; for (int i = 0; i < bp.Length; i++) for (int k = 0; k < 16; k++) d.Bindposes[i * 16 + k] = bp[i][k]; }
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var topo = m.GetTopology(s);
                d.Subs.Add(new MeshData.Sub { Indices = m.GetIndices(s), Topology = topo == MeshTopology.Quads ? "Quads" : topo == MeshTopology.Lines ? "Lines" : topo == MeshTopology.Points ? "Points" : "Triangles" });
            }
            var dp = new Vector3[n]; var dn = new Vector3[n]; var dt = new Vector3[n];
            for (int b = 0; b < m.blendShapeCount; b++)
            {
                var sh = new MeshData.Shape { Name = m.GetBlendShapeName(b) };
                for (int f = 0; f < m.GetBlendShapeFrameCount(b); f++)
                {
                    m.GetBlendShapeFrameVertices(b, f, dp, dn, dt);
                    sh.Frames.Add(new MeshData.Frame { Weight = m.GetBlendShapeFrameWeight(b, f), DPos = Flat(dp.ToList()), DNrm = Flat(dn.ToList()), DTan = Flat(dt.ToList()) });
                }
                d.Shapes.Add(sh);
            }
            return d;
        }

        static Vector3[] V3(float[] f, int n) { var r = new Vector3[n]; for (int i = 0; i < n; i++) r[i] = new Vector3(f[i * 3], f[i * 3 + 1], f[i * 3 + 2]); return r; }

        /// MeshData → Unity 메시 (렌더러 SceneBuilder.Fill과 같은 규칙)
        static void Fill(Mesh mesh, MeshData d)
        {
            int n = d.VertexCount;
            mesh.indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(V3(d.Pos, n));
            if (d.Nrm != null) mesh.SetNormals(V3(d.Nrm, n));
            if (d.Tan != null) { var t = new Vector4[n]; for (int i = 0; i < n; i++) t[i] = new Vector4(d.Tan[i * 4], d.Tan[i * 4 + 1], d.Tan[i * 4 + 2], d.Tan[i * 4 + 3]); mesh.SetTangents(t); }
            if (d.Col != null) { var c = new Color[n]; for (int i = 0; i < n; i++) c[i] = new Color(d.Col[i * 4], d.Col[i * 4 + 1], d.Col[i * 4 + 2], d.Col[i * 4 + 3]); mesh.SetColors(c); }
            for (int ch = 0; ch < 4; ch++)
            {
                var f = d.Uv[ch]; if (f == null) continue;
                int dim = d.UvDims[ch];
                var uv = new List<Vector4>(n);
                for (int i = 0; i < n; i++) { var v = Vector4.zero; for (int k = 0; k < dim; k++) v[k] = f[i * dim + k]; uv.Add(v); }
                if (dim == 2) mesh.SetUVs(ch, uv.Select(v => (Vector2)v).ToList()); else if (dim == 3) mesh.SetUVs(ch, uv.Select(v => (Vector3)v).ToList()); else mesh.SetUVs(ch, uv);
            }
            if (d.BoneW != null && d.BoneIdx != null)
            {
                var w = new BoneWeight[n];
                for (int i = 0; i < n; i++)
                    w[i] = new BoneWeight
                    {
                        boneIndex0 = d.BoneIdx[i * 4], boneIndex1 = d.BoneIdx[i * 4 + 1], boneIndex2 = d.BoneIdx[i * 4 + 2], boneIndex3 = d.BoneIdx[i * 4 + 3],
                        weight0 = d.BoneW[i * 4], weight1 = d.BoneW[i * 4 + 1], weight2 = d.BoneW[i * 4 + 2], weight3 = d.BoneW[i * 4 + 3],
                    };
                mesh.boneWeights = w;
            }
            if (d.Bindposes != null) { var bp = new Matrix4x4[d.Bindposes.Length / 16]; for (int i = 0; i < bp.Length; i++) { var mm = new Matrix4x4(); for (int k = 0; k < 16; k++) mm[k] = d.Bindposes[i * 16 + k]; bp[i] = mm; } mesh.bindposes = bp; }
            mesh.subMeshCount = d.Subs.Count;
            for (int s = 0; s < d.Subs.Count; s++)
            {
                var topo = d.Subs[s].Topology == "Lines" ? MeshTopology.Lines : d.Subs[s].Topology == "Quads" ? MeshTopology.Quads : d.Subs[s].Topology == "Points" ? MeshTopology.Points : MeshTopology.Triangles;
                mesh.SetIndices(d.Subs[s].Indices, topo, s, false);
            }
            foreach (var bs in d.Shapes)
                foreach (var fr in bs.Frames)
                    mesh.AddBlendShapeFrame(bs.Name, fr.Weight, fr.DPos == null ? new Vector3[n] : V3(fr.DPos, n), fr.DNrm == null ? null : V3(fr.DNrm, n), fr.DTan == null ? null : V3(fr.DTan, n));
            if (d.Nrm == null) mesh.RecalculateNormals();
        }

        /// "Armature/Hips[1]/Spine" (아바타 루트 아래, [k] = 같은 이름 형제 중 k번째)
        public static Transform Find(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var t = root;
            foreach (var seg in path.Split('/'))
            {
                string name = seg; int k = 0;
                int b = seg.LastIndexOf('[');
                if (b > 0 && seg.EndsWith("]") && int.TryParse(seg.Substring(b + 1, seg.Length - b - 2), out var kk)) { name = seg.Substring(0, b); k = kk; }
                Transform next = null; int seen = 0;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name != name) continue;
                    if (seen++ == k) { next = c; break; }
                }
                if (next == null) return null;
                t = next;
            }
            return t;
        }

        public static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        // ---------------- 3) 업로드 창 열기 ----------------
        public static void OpenForUpload()
        {
            if (IsExportProject()) { Warn("! 내보내기 프로젝트에서는 올리기 창을 열지 않아요"); return; }
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var a = Apply();
            var avatar = SceneAvatar();
            if (avatar != null) { Selection.activeGameObject = avatar; EditorGUIUtility.PingObject(avatar); }
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            if (avatar != null) UploadWindow.Open(avatar, a["upload"]);
            else Warn("! 장면에서 아바타(VRCAvatarDescriptor)를 찾지 못했어요");
            Status("opened", true, "Unity를 열었어요. VRChat SDK 창에서 로그인한 뒤 올려 주세요");
        }

        static GameObject SceneAvatar() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()
            .FirstOrDefault(g => g.activeSelf && g.GetComponentsInChildren<Component>(true).Any(c => c != null && c.GetType().Name == "VRCAvatarDescriptor"));

        /// SDK 창의 Build & Publish로 올린 경우에도 ID를 잃지 않게, 장면의 아바타 ID가 기록과 다르면 기록한다
        public static void RememberSceneId(GameObject avatar)
        {
            var id = BlueprintId(avatar);
            if (string.IsNullOrEmpty(id) || id == SavedAvatarId) return;
            RecordUpload(id, avatar.name);
            Info("장면의 아바타 ID를 기록했어요: " + id);
        }

        /// 상반신 썸네일(1200×900)을 찍어 <프로젝트>/VrcProto/thumbnail.png로 저장하고 경로를 돌려준다.
        /// 눈높이는 Avatar Descriptor의 ViewPosition(이름으로 읽음), 없으면 렌더러 범위의 위쪽 1/8.
        public static string CaptureThumbnail(GameObject avatar)
        {
            const int W = 1200, H = 900;
            var root = avatar.transform;
            Vector3 eye;
            var desc = avatar.GetComponents<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "VRCAvatarDescriptor");
            var vp = desc?.GetType().GetField("ViewPosition");
            if (vp != null && vp.GetValue(desc) is Vector3 v && v != Vector3.zero) eye = root.TransformPoint(v);
            else
            {
                var rs = avatar.GetComponentsInChildren<Renderer>(false);
                if (rs.Length == 0) return null;
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                eye = new Vector3(b.center.x, b.max.y - b.size.y / 8, b.center.z);
            }
            float height = Mathf.Max(0.3f, Vector3.Dot(eye - root.position, root.up));
            var target = eye - root.up * height * 0.18f;
            float dist = height * 0.75f;

            var go = new GameObject("VrcProto Thumbnail Camera") { hideFlags = HideFlags.HideAndDontSave };
            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 8);
            var prev = RenderTexture.active;
            try
            {
                var cam = go.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.82f, 0.84f, 0.88f);
                cam.fieldOfView = 30; cam.nearClipPlane = 0.01f; cam.farClipPlane = 100;
                cam.transform.position = target + root.forward * dist;
                cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, root.up);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                var path = Path.Combine(WorkDir, "thumbnail.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                Info("썸네일을 장면에서 찍었어요: " + path);
                return path;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }

    /// <summary>
    /// Unity가 프로젝트를 열고 있을 때의 내보내기: Host가 VrcProto/export-request.json을 두면 (스크립트를 다시 컴파일하지 않아도) 1초 안에 보고 Export를 부른다.
    /// </summary>
    [InitializeOnLoad]
    static class ExportRequest
    {
        static double _next;
        static ExportRequest() { if (!Application.isBatchMode) EditorApplication.update += Poll; }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 1;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var p = Path.Combine(Path.GetDirectoryName(Application.dataPath), "VrcProto", "export-request.json");
            if (!File.Exists(p)) return;
            bool upload;   // 원본 프로젝트에서 올리기 (2026-11-03)
            try { upload = JNode.Parse(File.ReadAllText(p))["upload"].AsBool; File.Delete(p); } catch (Exception) { return; }
            AssetDatabase.Refresh();   // Host가 넣은 원본 파일
            Setup.Export(upload);
        }
    }

    /// <summary>"ALEdit 업로드" 창: SDK 공개 API(VRCSdkControlPanel.TryGetBuilder → BuildAndUpload)를 이름으로 찾아 부른다.</summary>
    public sealed class UploadWindow : EditorWindow
    {
        GameObject _avatar;
        string _name = "", _desc = "", _thumb = "", _status = "";
        [SerializeField] string _key;   // 원본 프로젝트에서 올리기: 아바타별 올린 기록 열쇠 (스크립트를 다시 컴파일해도 남게)
        bool _busy;

        void UseKey() => Setup.UploadKey = string.IsNullOrEmpty(_key) ? null : _key;

        public static void Open(GameObject avatar, JNode upload)
        {
            var w = GetWindow<UploadWindow>(false, "ALEdit 업로드");
            w._avatar = avatar;
            w._key = Setup.UploadKey;
            w._name = upload["name"].AsString;
            w._thumb = upload["thumbnail"].AsString;
            // Host가 찍은 썸네일이 없으면(창 없이 준비한 경우 등) 장면에서 찍어 둔다. 새 아바타는 썸네일이 있어야 올라간다
            if ((string.IsNullOrEmpty(w._thumb) || !File.Exists(w._thumb)) && avatar != null) w.SetThumb(Setup.CaptureThumbnail(avatar));
            w.Show();
        }

        Texture2D _preview;
        string _previewOf;

        void SetThumb(string path) { _thumb = path ?? ""; _previewOf = null; }

        Texture2D Preview()
        {
            if (_previewOf == _thumb) return _preview;
            _previewOf = _thumb;
            if (_preview != null) DestroyImmediate(_preview);
            _preview = null;
            if (!string.IsNullOrEmpty(_thumb) && File.Exists(_thumb))
            {
                _preview = new Texture2D(2, 2);
                if (!_preview.LoadImage(File.ReadAllBytes(_thumb))) { DestroyImmediate(_preview); _preview = null; }
            }
            return _preview;
        }

        void OnDisable() { if (_preview != null) DestroyImmediate(_preview); }

        void OnFocus() { UseKey(); if (_avatar != null) Setup.RememberSceneId(_avatar); }

        [MenuItem("ALEdit/업로드 창")]
        static void Menu() => GetWindow<UploadWindow>(false, "ALEdit 업로드").Show();

        void OnGUI()
        {
            EditorGUILayout.HelpBox("1. VRChat SDK 창(VRChat SDK > Show Control Panel)에서 직접 로그인하세요. 이 도구는 계정 정보를 다루지 않아요.\n2. 아래 \"올리기\"를 누르거나, SDK 창의 Builder 탭에서 Build & Publish를 눌러도 돼요.", MessageType.Info);
            _avatar = (GameObject)EditorGUILayout.ObjectField("아바타", _avatar, typeof(GameObject), true);
            _name = EditorGUILayout.TextField("이름", _name);
            _desc = EditorGUILayout.TextField("설명", _desc);
            using (new EditorGUILayout.HorizontalScope())
            {
                var t = EditorGUILayout.TextField("썸네일 (PNG·JPG)", _thumb);
                if (t != _thumb) SetThumb(t);
                if (GUILayout.Button("찾아보기…", GUILayout.Width(80)))
                {
                    var dir = !string.IsNullOrEmpty(_thumb) ? Path.GetDirectoryName(_thumb) : "";
                    var p = EditorUtility.OpenFilePanelWithFilters("썸네일 고르기", dir, new[] { "이미지", "png,jpg,jpeg" });
                    if (!string.IsNullOrEmpty(p)) SetThumb(p);
                }
                using (new EditorGUI.DisabledScope(_avatar == null))
                    if (GUILayout.Button("장면에서 찍기", GUILayout.Width(100))) SetThumb(Setup.CaptureThumbnail(_avatar));
            }
            var pv = Preview();
            if (pv != null)
            {
                var rect = GUILayoutUtility.GetRect(240, 180, GUILayout.ExpandWidth(false));
                rect.x += EditorGUIUtility.labelWidth;
                GUI.DrawTexture(rect, pv, ScaleMode.ScaleToFit);
                if (pv.width * 3 != pv.height * 4) EditorGUILayout.HelpBox($"이미지가 {pv.width}×{pv.height}예요. VRChat 썸네일은 4:3(1200×900)이 맞아요.", MessageType.Warning);
            }
            else EditorGUILayout.HelpBox("썸네일이 없어요. 새 아바타는 썸네일이 있어야 올라가요.", MessageType.Warning);
            var id = _avatar != null ? Setup.BlueprintId(_avatar) : null;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("올릴 곳", string.IsNullOrEmpty(id) ? "새 아바타" : id + " (덮어쓰기)");
                using (new EditorGUI.DisabledScope(_busy || string.IsNullOrEmpty(id)))
                    if (GUILayout.Button("새 아바타로", GUILayout.Width(100)) && EditorUtility.DisplayDialog("ALEdit", $"{id}를 덮어쓰지 않고 새 아바타로 올릴까요? (지난 기록은 uploads.json에 남아요)", "새 아바타로", "취소"))
                    {
                        UseKey();
                        Setup.SetBlueprintId(_avatar, null);
                        Setup.ForgetAvatarId();
                        EditorSceneManager.SaveScene(_avatar.scene);
                    }
            }
            using (new EditorGUI.DisabledScope(_busy || _avatar == null || pv == null))
                if (GUILayout.Button(_busy ? "올리는 중…" : "올리기 (비공개)", GUILayout.Height(32))) _ = Upload();
            if (_status.Length > 0) EditorGUILayout.HelpBox(_status, MessageType.None);
        }

        async Task Upload()
        {
            _busy = true; _status = "SDK 빌더를 찾는 중…"; Repaint();
            try
            {
                var builderType = Setup.FindType("VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi");
                // VRCSdkControlPanel은 전역 이름공간에 있다 (SDK 3.10 기준. 이름공간이 생기는 경우를 대비해 둘 다 찾음)
                var panelType = Setup.FindType("VRCSdkControlPanel") ?? Setup.FindType("VRC.SDKBase.Editor.VRCSdkControlPanel");
                var avatarType = Setup.FindType("VRC.SDKBase.Editor.Api.VRCAvatar");
                if (builderType == null || panelType == null || avatarType == null)
                {
                    var missing = string.Join(", ", new[] { (builderType, "IVRCSdkAvatarBuilderApi"), (panelType, "VRCSdkControlPanel"), (avatarType, "VRCAvatar") }.Where(x => x.Item1 == null).Select(x => x.Item2));
                    throw new Exception($"VRChat SDK 공개 API를 찾지 못했어요 ({missing}). SDK 창의 Build & Publish로 올려 주세요.");
                }
                var tryGet = panelType.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "TryGetBuilder" && m.IsGenericMethodDefinition).MakeGenericMethod(builderType);
                var args = new object[] { null };
                if (!(bool)tryGet.Invoke(null, args) || args[0] == null) throw new Exception("SDK 창이 열려 있지 않거나 로그인하지 않았어요. VRChat SDK > Show Control Panel에서 로그인한 뒤 다시 눌러 주세요.");
                var builder = args[0];
                var upload = builderType.GetMethods().FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return m.Name == "BuildAndUpload" && p.Length == 4 && p[0].ParameterType == typeof(GameObject) && p[1].ParameterType == avatarType && p[2].ParameterType == typeof(string) && p[3].ParameterType == typeof(CancellationToken);
                });
                if (upload == null) throw new Exception("이 SDK 버전의 BuildAndUpload 형식을 몰라요. SDK 창의 Build & Publish로 올려 주세요.");
                object info = Activator.CreateInstance(avatarType);
                void SetMember(string name, object v)
                {
                    var f = avatarType.GetField(name); if (f != null) { f.SetValue(info, v); return; }
                    var p = avatarType.GetProperty(name); if (p != null && p.CanWrite) p.SetValue(info, v);
                }
                SetMember("Name", _name); SetMember("Description", _desc); SetMember("Tags", new List<string>()); SetMember("ReleaseStatus", "private");
                _status = "빌드하고 올리는 중… (SDK 창에 진행 상황이 보여요)"; Repaint();
                var task = (Task)upload.Invoke(builder, new object[] { _avatar, info, string.IsNullOrEmpty(_thumb) ? null : _thumb, CancellationToken.None });
                await task;
                // 새 아바타면 SDK가 빌드 중에 ID를 받아 PipelineManager에 넣는다
                var id = Setup.BlueprintId(_avatar);
                if (!string.IsNullOrEmpty(id))
                {
                    UseKey();
                    Setup.RecordUpload(id, _name);
                    EditorSceneManager.SaveScene(_avatar.scene);
                }
                _status = "올렸어요" + (string.IsNullOrEmpty(id) ? "" : $" ({id})") + ". VRChat 웹사이트나 게임에서 확인하세요.";
                Setup.Status("uploaded", true, _status, JNode.Obj().Set("avatarId", id));
            }
            catch (Exception e)
            {
                var msg = (e is TargetInvocationException t ? t.InnerException : e)?.Message ?? e.Message;
                _status = "올리지 못했어요: " + msg;
                Setup.Status("uploaded", false, _status);
            }
            finally { _busy = false; Repaint(); }
        }
    }
}
