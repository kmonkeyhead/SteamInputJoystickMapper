using System.Globalization;
using System.Text;

namespace SteamJoystickMapper.Steam.Sdl;

/// <summary>
/// SDL 게임패드 매핑 한 줄: "GUID,이름,a:b0,leftx:a0,...,platform:Windows,".
/// Steam은 일반(DirectInput/HID) 장치의 "장치 레이아웃"(물리 입력 → 게임패드 요소)을
/// config/config.vdf 의 "SDL_GamepadBind" 값에 이 형식으로 저장한다.
/// </summary>
public sealed class SdlMapping
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>순서를 보존하는 (요소, 소스) 목록. platform 등 기타 필드도 포함.</summary>
    public List<KeyValuePair<string, string>> Fields { get; } = new();

    public static string GuidFor(ushort vid, ushort pid, ushort version = 0)
    {
        // SDL GUID: bus(LE16) crc(LE16) vid(LE16) 0000 pid(LE16) 0000 version(LE16) driver(2바이트)
        static string Le(ushort v) => (v & 0xFF).ToString("x2") + (v >> 8).ToString("x2");
        return "0300" + "0000" + Le(vid) + "0000" + Le(pid) + "0000" + Le(version) + "0000";
    }

    public (ushort Vid, ushort Pid)? TryGetVidPid()
    {
        if (Guid.Length != 32) return null;
        static ushort? ReadLe(string s, int at) =>
            ushort.TryParse(s.Substring(at + 2, 2) + s.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
        var vid = ReadLe(Guid, 8);
        var pid = ReadLe(Guid, 16);
        return vid.HasValue && pid.HasValue ? (vid.Value, pid.Value) : null;
    }

    public bool Matches(ushort vid, ushort pid) => TryGetVidPid() is var (v, p) && v == vid && p == pid;

    public string? Get(string element) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, element, StringComparison.OrdinalIgnoreCase)).Value;

    public void Set(string element, string source)
    {
        var index = Fields.FindIndex(f => string.Equals(f.Key, element, StringComparison.OrdinalIgnoreCase));
        var pair = new KeyValuePair<string, string>(element, source);
        if (index >= 0) Fields[index] = pair;
        else
        {
            var platform = Fields.FindIndex(f => f.Key == "platform");
            if (platform >= 0) Fields.Insert(platform, pair);
            else Fields.Add(pair);
        }
    }

    /// <summary>게임패드 요소 매핑만 (platform, crc, hint 등 메타 필드 제외).</summary>
    public IEnumerable<KeyValuePair<string, string>> ElementFields =>
        Fields.Where(f => SdlElements.IsElement(f.Key));

    public static SdlMapping? TryParse(string line)
    {
        var parts = line.Trim().Split(',');
        if (parts.Length < 3 || parts[0].Length != 32 || !parts[0].All(Uri.IsHexDigit)) return null;
        var m = new SdlMapping { Guid = parts[0].ToLowerInvariant(), Name = parts[1] };
        foreach (var part in parts.Skip(2))
        {
            if (part.Length == 0) continue;
            var colon = part.IndexOf(':');
            if (colon <= 0) return null;
            m.Fields.Add(new(part[..colon], part[(colon + 1)..]));
        }
        return m;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Guid).Append(',').Append(Name.Replace(",", " ")).Append(',');
        foreach (var f in Fields) sb.Append(f.Key).Append(':').Append(f.Value).Append(',');
        return sb.ToString();
    }
}

/// <summary>SDL_GamepadBind 전체 값. 파싱할 수 없는 줄(주석, URL 등)도 그대로 보존한다.</summary>
public sealed class SdlMappingDatabase
{
    private readonly List<object> _lines = new(); // string(원문) 또는 SdlMapping
    private string _newline = "\n";

    public static SdlMappingDatabase Parse(string? value)
    {
        var db = new SdlMappingDatabase();
        if (string.IsNullOrEmpty(value)) return db;
        if (value.Contains("\r\n")) db._newline = "\r\n";
        foreach (var raw in value.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            db._lines.Add((object?)SdlMapping.TryParse(line) ?? line);
        }
        return db;
    }

    public IEnumerable<SdlMapping> Mappings => _lines.OfType<SdlMapping>();

    public SdlMapping? Find(ushort vid, ushort pid) => Mappings.LastOrDefault(m => m.Matches(vid, pid));

    /// <summary>같은 VID/PID의 매핑을 교체하거나(모두) 없으면 추가한다.</summary>
    public void Upsert(SdlMapping mapping, ushort vid, ushort pid)
    {
        var first = _lines.FindIndex(l => l is SdlMapping m && m.Matches(vid, pid));
        _lines.RemoveAll(l => l is SdlMapping m && m.Matches(vid, pid));
        if (first >= 0) _lines.Insert(Math.Min(first, _lines.Count), mapping);
        else
        {
            // 끝의 빈 줄 앞에 추가
            var insertAt = _lines.Count;
            while (insertAt > 0 && _lines[insertAt - 1] is string s && s.Length == 0) insertAt--;
            _lines.Insert(insertAt, mapping);
        }
    }

    /// <summary>같은 VID/PID의 매핑을 모두 지운다. 지운 것이 있으면 true.</summary>
    public bool Remove(ushort vid, ushort pid) => _lines.RemoveAll(l => l is SdlMapping m && m.Matches(vid, pid)) > 0;

    public override string ToString() => string.Join(_newline, _lines.Select(l => l.ToString()));
}
