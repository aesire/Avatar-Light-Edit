// 스튜디오 세팅 프리셋 파일 (2026-11-01): <이름>.studio.json = { format, version, name, studio: { 스튜디오 값 } }. 스튜디오 페이지 값을 파일로 저장하고 불러온다.
// 값은 세션의 studio와 같은 모양 (StudioPanel.ApplyStudio). 이미지·소품은 경로만 적는다 — 다른 PC로 옮기면 파일을 따로 옮겨야 한다.
// 기본 폴더는 설정 폴더의 studio-presets. 파일을 직접 주고받아도 된다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcProto.Protocol;

namespace VrcProto.Host.Core
{
    public static class StudioPreset
    {
        public const string Format = "vrcproto-studio-preset";
        public const int Version = 1;
        public const string Extension = ".studio.json";

        public static bool IsPresetFile(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>파일 이름에서 프리셋 이름 ("노을.studio.json" → "노을").</summary>
        public static string NameOf(string path)
        {
            var n = Path.GetFileName(path);
            return IsPresetFile(n) ? n.Substring(0, n.Length - Extension.Length) : Path.GetFileNameWithoutExtension(n);
        }

        /// <summary>확장자를 맞춘 경로 ("a" · "a.json" → "a.studio.json").</summary>
        public static string WithExtension(string path)
        {
            if (IsPresetFile(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileName(path);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5);
            return Path.Combine(dir, name + Extension);
        }

        public static void Save(string path, JNode studio)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JNode.Obj().Set("format", Format).Set("version", Version).Set("name", NameOf(path)).Set("studio", studio).ToJson(indent: true));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        /// <summary>프리셋의 스튜디오 값. 프리셋이 아니거나 더 새 버전이면 InvalidDataException.</summary>
        public static JNode Load(string path)
        {
            JNode j;
            try { j = JNode.Parse(File.ReadAllText(path)); }
            catch (Exception e) when (e is FormatException || e is IndexOutOfRangeException) { throw new InvalidDataException("스튜디오 프리셋 파일을 읽지 못했습니다: " + e.Message); }
            if (j["format"].AsString != Format) throw new InvalidDataException("스튜디오 프리셋 파일이 아닙니다: " + Path.GetFileName(path));
            if (j["version"].AsInt > Version) throw new InvalidDataException("더 새 버전의 프리셋입니다. 프로그램을 업데이트해 주세요: " + Path.GetFileName(path));
            if (j["studio"].Kind != JKind.Object) throw new InvalidDataException("프리셋에 스튜디오 값이 없습니다: " + Path.GetFileName(path));
            return j["studio"];
        }

        /// <summary>폴더의 프리셋 (이름 순). 폴더가 없으면 빈 목록.</summary>
        public static List<(string Name, string Path)> List(string folder)
        {
            if (!Directory.Exists(folder)) return new List<(string, string)>();
            return Directory.GetFiles(folder, "*" + Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => (NameOf(p), p)).ToList();
        }

        /// <summary>프리셋이 가리키는 파일 중 없는 것 (배경 이미지·소품). 불러온 뒤 알려 준다.</summary>
        public static List<string> MissingFiles(JNode studio)
        {
            var list = new List<string>();
            var bg = studio["background"];
            if (bg["mode"].AsString is "image" or "panorama" && !File.Exists(bg["path"].AsString)) list.Add(bg["path"].AsString);
            foreach (var p in studio["props"].Items) if (!File.Exists(p["path"].AsString)) list.Add(p["path"].AsString);
            return list;
        }
    }
}
