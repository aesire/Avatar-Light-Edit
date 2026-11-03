// 패널들이 공유하는 편집 상태: 현재 Document, 선택, 렌더러 연결.
using System;
using System.Collections.Generic;
using VrcProto.Host.Core;
using VrcProto.Protocol;

namespace VrcProto.Host.Shared
{
    public sealed class EditorContext
    {
        public Document? Doc { get; private set; }
        /// <summary>현재 Document의 메시 편집 (정점·삼각형 수, 정점 합치기). Doc과 같이 바뀐다.</summary>
        public MeshEdits? Meshes { get; private set; }
        public RenderHost? Render { get; set; }
        public string? SelectedRenderer { get; private set; }
        public string? SelectedMaterial { get; private set; }

        /// <summary>렌더러가 보고한 환경 Provider 스키마 (ready 메시지). 연결 전에는 내장 기본값.</summary>
        public JNode EnvironmentSchema { get; set; } = DefaultEnvironmentSchema();
        public List<string> AvailableShaders { get; } = new List<string>();

        public event Action? DocumentOpened;
        public event Action? SelectionChanged;
        public event Action? RendererInfoChanged;
        /// <summary>렌더러에서 온 모든 메시지 (UI 스레드). 촬영 패널의 captureDone 등.</summary>
        public event Action<JNode>? RendererMessage;
        public void RaiseRendererMessage(JNode m) => RendererMessage?.Invoke(m);

        /// <summary>상태 표시줄 문구.</summary>
        public event Action<string>? StatusRequested;
        public void Status(string s) => StatusRequested?.Invoke(s);

        /// <summary>사진 파일을 저장했다 (스튜디오·VR 카메라, 2026-10-27): 상태 표시줄에 열기·폴더·복사 단추를 띄운다.</summary>
        public event Action<string>? FileSaved;
        public void Saved(string path) => FileSaved?.Invoke(path);

        /// <summary>현재 카메라 상태 (렌더러의 cameraChanged, 세션 저장용).</summary>
        public JNode Camera { get; set; } = JNode.Null;

        /// <summary>렌더러가 sceneLoaded로 알려 준 월드 범위 { min, max } (꺼 둔 렌더러 포함). 연결 전·장면 없음이면 Null.</summary>
        public JNode SceneBounds { get; set; } = JNode.Null;

        /// <summary>마지막 SetDocument에서 DocumentOpened·SelectionChanged 구독자마다 걸린 시간 (구독한 클래스 이름, ms). 열기 시간 재기용</summary>
        public List<(string Who, double Ms)> OpenTimings { get; } = new List<(string, double)>();
        /// <summary>마지막 RaiseRendererInfoChanged에서 구독자마다 걸린 시간</summary>
        public List<(string Who, double Ms)> InfoTimings { get; } = new List<(string, double)>();

        /// <param name="renderer">처음 고를 렌더러 (2026-10-24): 페이지들이 DocumentOpened에서 이 선택으로 한 번만 그린다.
        /// 예전엔 빈 선택으로 그린 뒤 메시 고르기가 첫 렌더러를 골라 머티리얼 페이지를 또 그렸고, 블렌드쉐이프 페이지를 처음 열 때 몸 메시로 바뀌며 또 그렸다.</param>
        /// <param name="meshes">미리 만든 메시 편집 (원본 메시를 백그라운드에서 미리 읽기 시작한 것). 없으면 새로.</param>
        public void SetDocument(Document? doc, string? renderer = null, MeshEdits? meshes = null)
        {
            Doc = doc;
            Meshes = doc != null ? meshes ?? new MeshEdits(doc) : null;
            SceneBounds = JNode.Null;
            var r = renderer != null ? doc?.Original.FindRenderer(renderer) : null;
            SelectedRenderer = r?.Id; SelectedMaterial = r?.Materials.Count > 0 ? r.Materials[0] : null;
            if (r != null) Render?.Send(Msg.Make(Msg.SetSelection).Set("renderer", SelectedRenderer).Set("mat", SelectedMaterial));
            OpenTimings.Clear();
            _opening = true;
            try
            {
                InvokeTimed(DocumentOpened, OpenTimings, "");
                InvokeTimed(SelectionChanged, OpenTimings, "선택 ");
            }
            finally { _opening = false; }
        }
        bool _opening;

        /// <summary>구독자를 하나씩 부르며 시간을 잰다 (람다는 그 람다를 쓴 클래스 이름으로)</summary>
        static void InvokeTimed(Action? ev, List<(string, double)> into, string prefix)
        {
            if (ev == null) return;
            foreach (var h in ev.GetInvocationList())
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ((Action)h)();
                var t = h.Target?.GetType() ?? h.Method.DeclaringType;
                while (t != null && t.IsNested && t.Name.StartsWith("<")) t = t.DeclaringType;
                into.Add((prefix + (t?.Name ?? "?"), sw.Elapsed.TotalMilliseconds));
            }
        }

        public void Select(string? renderer, string? material)
        {
            if (renderer == SelectedRenderer && material == SelectedMaterial) return;
            SelectedRenderer = renderer; SelectedMaterial = material;
            if (_opening) InvokeTimed(SelectionChanged, OpenTimings, "선택 ");   // 열 때 RendererPicker가 고르는 몸 메시 (그 안에서 걸린 시간을 나눠 본다)
            else SelectionChanged?.Invoke();
            Render?.Send(Msg.Make(Msg.SetSelection).Set("renderer", renderer).Set("mat", material));
        }

        /// <summary>작업 창에 페이지가 보였다 (페이지는 늘 붙어 있고 보이기만 바뀌므로 AttachedToVisualTree 대신 이것으로 안다)</summary>
        public event Action<string>? PageShown;
        public void RaisePageShown(string page) => PageShown?.Invoke(page);

        public void RaiseRendererInfoChanged() { InfoTimings.Clear(); InvokeTimed(RendererInfoChanged, InfoTimings, ""); }

        /// <summary>시간 목록 한 줄: 합계 · 오래 걸린 것부터 (1 ms 미만은 뺀다)</summary>
        public static string TimingLine(List<(string Who, double Ms)> list)
        {
            double sum = 0; foreach (var x in list) sum += x.Ms;
            var parts = new List<string>();
            foreach (var x in System.Linq.Enumerable.OrderByDescending(list, x => x.Ms)) if (x.Ms >= 1) parts.Add($"{x.Who} {x.Ms:0}");
            return $"{sum:0} ms" + (parts.Count > 0 ? " — " + string.Join(" · ", parts) : "");
        }

        public static JNode DefaultEnvironmentSchema() => JNode.Parse(@"[
          { ""id"": ""UnityLighting"", ""version"": 1, ""label"": ""조명"",
            ""params"": [
              { ""name"": ""preset"", ""label"": ""프리셋"", ""type"": ""enum"", ""options"": [""bright"", ""indoor"", ""dark"", ""none""], ""optionLabels"": [""밝은 월드"", ""실내"", ""어두운 월드"", ""라이트 없음""], ""default"": ""bright"" },
              { ""name"": ""intensity"", ""label"": ""주광 세기 배율"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1 },
              { ""name"": ""yaw"", ""label"": ""주광 회전(°)"", ""type"": ""float"", ""min"": -180, ""max"": 180, ""default"": 0 },
              { ""name"": ""ambient"", ""label"": ""환경광 배율"", ""type"": ""float"", ""min"": 0, ""max"": 3, ""default"": 1 }
            ] }
        ]");
    }
}
