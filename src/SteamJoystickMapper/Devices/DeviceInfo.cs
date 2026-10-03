using SteamJoystickMapper.Mapping;

using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.Devices;

public sealed class DeviceInfo
{
    public required string Name { get; init; }
    public required Guid InstanceGuid { get; init; }
    public required Guid ProductGuid { get; init; }
    public required ushort Vid { get; init; }
    public required ushort Pid { get; init; }
    public string InterfacePath { get; init; } = "";
    public required IReadOnlyList<JoyAxis> Axes { get; init; }
    public int ButtonCount { get; init; }
    public int PovCount { get; init; }
    /// <summary>같은 VID/PID 장치가 여러 개일 때 1부터 매기는 번호.</summary>
    public int DuplicateIndex { get; set; }

    public string VidHex => Vid.ToString("X4");
    public string PidHex => Pid.ToString("X4");

    public DeviceIdentity ToIdentity() => new()
    {
        Name = Name,
        Vid = VidHex,
        Pid = PidHex,
        InstanceId = InstanceGuid.ToString(),
        Axes = Axes.ToList(),
        ButtonCount = ButtonCount,
        PovCount = PovCount,
    };

    public string Display => DuplicateIndex > 0 ? $"{Name} #{DuplicateIndex}  [{VidHex}:{PidHex}]" : $"{Name}  [{VidHex}:{PidHex}]";

    public string Details =>
        $"Vendor ID: {VidHex}   Product ID: {PidHex}\n" +
        $"Instance ID: {InstanceGuid}\n" +
        $"Axes: {string.Join(", ", Axes.Select(PhysicalInput.AxisDisplayName))}\n" +
        $"Buttons: {(ButtonCount > 0 ? $"1 ~ {ButtonCount}" : T("없음", "none"))}   POV: {PovCount}";

    public override string ToString() => Display;
}

/// <summary>한 번 폴링한 입력 상태. 축은 -1..1 정규화.</summary>
public sealed class InputSnapshot
{
    public Dictionary<JoyAxis, double> Axes { get; } = new();
    public bool[] Buttons { get; init; } = Array.Empty<bool>();
    /// <summary>POV 각도 (1/100도), 중립 = -1.</summary>
    public int[] Povs { get; init; } = Array.Empty<int>();

    public static PovDirection? PovToDirection(int value)
    {
        if (value < 0 || value > 36000) return null;
        var angle = value / 100.0;
        return angle switch
        {
            >= 315 or < 45 => PovDirection.Up,
            < 135 => PovDirection.Right,
            < 225 => PovDirection.Down,
            _ => PovDirection.Left,
        };
    }
}
