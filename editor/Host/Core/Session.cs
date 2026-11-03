// 세션 파일: IR 경로 + Changeset + 장면 상태(카메라) + 변형(비교 시트용, 이름 + Changeset). 원본 데이터는 담지 않는다.
// 같은 JSON을 촬영 PNG의 iTXt 청크에도 넣는다 (PngText).
// studio (2026-11-01): 배경·바닥·소품·심도 등 스튜디오 값. 창 없는 촬영이 만든 세션에만 있고, 열면 StudioPanel이 설정에 넣는다. 에디터가 저장하는 세션에는 적지 않는다.
// source (2026-10-27): IR을 만든 재료(IR의 source 그대로 — 원본 패키지 경로·루트·붙인 아이템). IR 캐시를 지웠거나 다른 PC에서 열 때
//   재료로 IR을 다시 만들 수 있게. 원본 데이터가 아니라 경로·설정만이라 작다. 예전 세션에는 없다.
using System.IO;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class Session
    {
        public const string Format = "vrcproto-session";
        public const int Version = 1;
        public const string Extension = ".alesav";
        /// <summary>예전 세션 확장자 (2026-11-03 전). 열기만 하고 새로 쓰지는 않는다.</summary>
        public const string OldExtension = ".vrcproto";

        public static bool IsSessionFile(string path) =>
            path.EndsWith(Extension, System.StringComparison.OrdinalIgnoreCase) || path.EndsWith(OldExtension, System.StringComparison.OrdinalIgnoreCase);

        public static JNode ToJson(Document doc, JNode? camera)
        {
            return JNode.Obj()
                .Set("format", Format).Set("version", Version)
                .Set("ir", doc.IrPath)
                .Set("changeset", doc.ChangesetToJson())
                .Set("camera", camera ?? JNode.Null)
                .Set("variants", doc.VariantsToJson())
                .Set("source", doc.Original.Raw["source"]);
        }

        public static void Save(string path, Document doc, JNode? camera)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(doc, camera).ToJson(indent: true));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            doc.MarkSaved();
        }

        public sealed class Loaded { public string IrPath = ""; public JNode Changeset = JNode.Null; public JNode Camera = JNode.Null; public JNode Variants = JNode.Null; public JNode Source = JNode.Null; public JNode Studio = JNode.Null; }

        public static Loaded Read(string path)
        {
            try { return FromJson(JNode.Parse(File.ReadAllText(path)), Path.GetDirectoryName(Path.GetFullPath(path)) ?? "."); }
            catch (InvalidDataException e) { throw new InvalidDataException(e.Message + ": " + path); }
        }

        /// <summary>세션 JSON 해석 (파일·PNG 공통). 상대 IR 경로는 baseDir 기준.</summary>
        public static Loaded FromJson(JNode j, string baseDir)
        {
            if (j["format"].AsString != Format) throw new InvalidDataException("세션 파일이 아닙니다");
            if (j["version"].AsInt > Version) throw new InvalidDataException("더 새 버전의 세션 파일입니다.");
            var ir = j["ir"].AsString;
            if (!Path.IsPathRooted(ir)) ir = Path.Combine(baseDir, ir);
            return new Loaded { IrPath = ir, Changeset = j["changeset"], Camera = j["camera"], Variants = j["variants"], Source = j["source"], Studio = j["studio"] };
        }
    }
}
