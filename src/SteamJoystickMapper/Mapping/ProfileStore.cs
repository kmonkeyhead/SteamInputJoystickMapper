using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamJoystickMapper.Mapping;

/// <summary>프로필은 하나뿐이다: profile.json.</summary>
public sealed class ProfileStore(string path)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    /// <summary>프로필을 읽는다. 없거나 읽을 수 없으면 기본 프로필을 만든다.</summary>
    public MapperProfile LoadOrCreate()
    {
        if (File.Exists(Path))
        {
            try
            {
                var loaded = Read(Path);
                // v2의 전역 매핑은 더 이상 쓰지 않는다: 다음 저장에서 사라지므로 원본을 남겨 둔다
                var legacy = Path + ".v2-global.json";
                if (!File.Exists(legacy) && File.ReadAllText(Path).Contains("\"global\""))
                {
                    File.Copy(Path, legacy);
                    Logging.AppLog.Info($"이전 형식 프로필(전역 매핑 포함) 보관: {System.IO.Path.GetFileName(legacy)}");
                }
                return loaded;
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                var broken = Path + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(Path, broken, overwrite: true);
                Logging.AppLog.Warn($"프로필을 읽을 수 없어 기본 프로필로 시작합니다 (원본 보관: {System.IO.Path.GetFileName(broken)}): {ex.Message}");
            }
        }
        var profile = MapperProfile.CreateDefault(null);
        Save(profile);
        return profile;
    }

    public static MapperProfile Read(string path)
    {
        var p = JsonSerializer.Deserialize<MapperProfile>(File.ReadAllText(path), JsonOptions)
                ?? throw new JsonException("빈 프로필");
        if (p.FormatVersion < 2) throw new NotSupportedException("이전 형식의 프로필입니다.");
        p.FormatVersion = 3; // v2의 "global"은 더 이상 쓰지 않음 (무시)
        return p;
    }

    public void Save(MapperProfile profile)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        profile.ModifiedAt = DateTime.Now;
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(profile, JsonOptions));
        File.Move(tmp, Path, overwrite: true);
    }

    public static void Export(MapperProfile profile, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
}
