using System.Collections.Generic;
using System.Globalization;

namespace ImportSim
{
    /// <summary>
    /// 직렬화된 Unity 객체 하나. Props 는 Unity YAML 구조 그대로:
    /// 값은 string / Map(Dictionary) / List. 참조는 {fileID, guid?, type?} Map.
    /// </summary>
    public sealed class SimObject
    {
        public long Id;
        public int ClassId;
        public string Type = "";
        public Map Props = new Map();

        public SimObject Clone(System.Func<long, long> remapLocal) =>
            new SimObject { Id = remapLocal(Id), ClassId = ClassId, Type = Type, Props = (Map)Yaml.DeepCopy(Props, remapLocal)! };
    }

    public sealed class Map : Dictionary<string, object?> { }

    /// <summary>한 에셋 파일(모델 또는 프리팹)을 Unity가 로드한 결과. Objects 키는 그 파일 안의 fileID.</summary>
    public sealed class SimFile
    {
        public string Guid = "", Path = "";
        public Dictionary<long, SimObject> Objects = new Dictionary<long, SimObject>();
        public List<string> Warnings = new List<string>();
        /// Resolver.Compose 로 넣은 객체 fileID -> 넣은 프리팹 guid (의상)
        public Dictionary<long, string> AddedFrom = new Dictionary<long, string>();
        /// 프리팹 인스턴스의 루트 GameObject fileID -> 원본 프리팹 guid (중첩 포함, 기믹 찾기용 IrGimmicks.cs)
        public Dictionary<long, string> PrefabRoots = new Dictionary<long, string>();
        /// 모델(FBX)일 때: Mesh fileID -> 블렌드쉐이프 이름 (Unity 순서)
        public Dictionary<long, List<string>> MeshBlendShapes = new Dictionary<long, List<string>>();
        /// 모델(FBX)이고 geometry 로 만들었을 때: Mesh fileID -> 메시 데이터
        public Dictionary<long, SimMesh> Meshes = new Dictionary<long, SimMesh>();
        /// 모델(FBX)일 때: 내장 Material fileID -> FBX 머티리얼 이름
        public Dictionary<long, string> MaterialNames = new Dictionary<long, string>();

        public SimObject? Get(long id) => Objects.TryGetValue(id, out var o) ? o : null;

        /// <summary>부모가 없는 Transform 의 GameObject 전부 (씬은 여러 개)</summary>
        public IEnumerable<SimObject> Roots
        {
            get
            {
                foreach (var o in Objects.Values)
                    if ((o.ClassId == 4 || o.ClassId == 224) && Ref.Local(o.Props, "m_Father") == 0 && Get(Ref.Local(o.Props, "m_GameObject")) is SimObject go)
                        yield return go;
            }
        }

        /// <summary>루트 GameObject (부모가 없는 Transform 의 GameObject)</summary>
        public SimObject? Root
        {
            get
            {
                foreach (var o in Objects.Values)
                    if (o.ClassId == 4 && Ref.Local(o.Props, "m_Father") == 0)
                        return Get(Ref.Local(o.Props, "m_GameObject"));
                return null;
            }
        }
    }

    public static class Ref
    {
        public const string BuiltinExtraGuid = "0000000000000000f000000000000000";
        public const string BuiltinDefaultGuid = "0000000000000000e000000000000000";
        public static bool IsBuiltin(string? guid) => guid == BuiltinExtraGuid || guid == BuiltinDefaultGuid;

        public static Map Make(long fileId, string? guid = null, int type = 0)
        {
            var m = new Map { ["fileID"] = fileId.ToString(CultureInfo.InvariantCulture) };
            if (guid != null) { m["guid"] = guid; m["type"] = type.ToString(CultureInfo.InvariantCulture); }
            return m;
        }

        public static long FileId(object? v) =>
            v is Map m && m.TryGetValue("fileID", out var f) && f is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

        public static string? Guid(object? v) => v is Map m && m.TryGetValue("guid", out var g) ? g as string : null;

        public static bool IsRef(object? v) => v is Map m && m.ContainsKey("fileID");

        /// 같은 파일 안을 가리키는 참조의 fileID (guid 있으면 0)
        public static long Local(Map props, string key) =>
            props.TryGetValue(key, out var v) && Guid(v) == null ? FileId(v) : 0;

        public static string Format(object? v)
        {
            var g = Guid(v);
            return g == null ? FileId(v).ToString(CultureInfo.InvariantCulture) : $"{g}:{FileId(v)}";
        }
    }
}
