using System.Text.Json.Serialization;

namespace SteamJoystickMapper.Mapping;

/// <summary>
/// 앱의 유일한 프로필: 장치 + 게임별 매핑. Steam이 필요로 하는 장치 레이아웃(config.vdf)은
/// 게임 매핑들로부터 앱이 자동으로 만든다 (SteamLayoutPlanner.BuildLayout). Steam VDF는 이 프로필의 출력 대상이다.
/// </summary>
public sealed class MapperProfile
{
    public const string DefaultName = "Monkeyhead Mapper - default";

    /// <summary>3: 전역 매핑 제거 (이전 형식의 "global"은 읽을 때 무시됨).</summary>
    public int FormatVersion { get; set; } = 3;
    public string ProfileName { get; set; } = DefaultName;
    public DeviceIdentity Device { get; set; } = new();
    public List<GameMapping> Games { get; set; } = new();
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public static MapperProfile CreateDefault(DeviceIdentity? device) => new() { Device = device ?? new DeviceIdentity() };

    public GameMapping? FindGame(string appId) => Games.FirstOrDefault(g => g.AppId == appId);

    /// <summary>게임 매핑을 계획/검증용 보기로.</summary>
    public MappingProfile GameView(GameMapping game) => new()
    {
        ProfileName = ProfileName,
        AppId = game.AppId,
        GameName = game.GameName,
        Device = Device,
        Bindings = game.Bindings.Select(b => b.Clone()).ToList(),
    };
}

public sealed class GameMapping
{
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public List<MappingBinding> Bindings { get; set; } = new();
}

/// <summary>
/// 게임 하나에 대한 매핑 보기. 플래너/생성기/검증기의 입력.
/// </summary>
public sealed class MappingProfile
{
    public string ProfileName { get; set; } = "";
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public DeviceIdentity Device { get; set; } = new();
    public List<MappingBinding> Bindings { get; set; } = new();
}

/// <summary>장치 식별 정보. 장치명만으로 식별하지 않고 VID/PID/Instance ID를 사용한다.</summary>
public sealed record DeviceIdentity
{
    public string Name { get; init; } = "";
    /// <summary>4자리 16진수 (예: "044F").</summary>
    public string Vid { get; init; } = "";
    public string Pid { get; init; } = "";
    /// <summary>DirectInput Instance GUID. 같은 제품이 여러 개 연결된 경우 구분용.</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>장치에 존재하는 축 목록 (HID Usage 순). SDL 축 인덱스 계산에 필요.</summary>
    public List<JoyAxis> Axes { get; init; } = new();
    public int ButtonCount { get; init; }
    public int PovCount { get; init; }

    [JsonIgnore] public bool IsEmpty => string.IsNullOrEmpty(Vid) || string.IsNullOrEmpty(Pid);
    [JsonIgnore] public ushort VidValue => Convert.ToUInt16(Vid, 16);
    [JsonIgnore] public ushort PidValue => Convert.ToUInt16(Pid, 16);

    public bool SameProduct(DeviceIdentity other) =>
        string.Equals(Vid, other.Vid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Pid, other.Pid, StringComparison.OrdinalIgnoreCase);
}

public sealed class MappingBinding
{
    public XboxOutput Target { get; set; }
    public PhysicalInput Source { get; set; } = PhysicalInput.FromButton(0);

    // 축 옵션 (MVP: Invert, DeadZone, OuterDeadZone)
    public bool Invert { get; set; }
    /// <summary>안쪽 데드존 (%). 축 → 버튼이면 이 값을 넘어야 눌림.</summary>
    public int DeadZone { get; set; }
    /// <summary>바깥쪽 데드존 (%).</summary>
    public int OuterDeadZone { get; set; }

    /// <summary>
    /// 축 → 트리거의 감지 범위 (축 % 0~100, 쓰로틀 표시와 같음). 방향(Source.AxisSign)이 −면 RangeLow에서 최대·RangeHigh에서 0,
    /// +면 RangeLow에서 0·RangeHigh에서 최대. 없으면 이전 방식(가운데 기준 DeadZone)에서 계산.
    /// </summary>
    public int? RangeLow { get; set; }
    public int? RangeHigh { get; set; }

    /// <summary>축 → 트리거 감지 범위 (%). 이전 형식(반쪽 축 + 데드존)은 가운데 50%를 기준으로 바꾼다.</summary>
    public (int Low, int High) TriggerRange()
    {
        if (RangeLow is { } lo && RangeHigh is { } hi) return (lo, hi);
        var edge = (int)Math.Round(50 * Math.Clamp(DeadZone, 0, 100) / 100.0);
        return Source.AxisSign switch
        {
            < 0 => (0, 50 - edge),
            > 0 => (50 + edge, 100),
            _ => (Math.Clamp(DeadZone, 0, 100), 100),
        };
    }

    // 버튼 옵션 (MVP: Normal만 지원)
    public ButtonMode ButtonMode { get; set; } = ButtonMode.Normal;

    public MappingBinding Clone() => (MappingBinding)MemberwiseClone();
}
