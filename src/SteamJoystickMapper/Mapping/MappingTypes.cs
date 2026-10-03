using System.Text.Json.Serialization;

namespace SteamJoystickMapper.Mapping;

/// <summary>DirectInput/HID 축. 선언 순서 = HID Usage 순서(X 0x30 … Dial 0x37) = SDL 축 인덱스 순서.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JoyAxis { X, Y, Z, Rx, Ry, Rz, Slider0, Slider1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PovDirection { Up, Right, Down, Left }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PhysicalInputKind { Axis, Button, Pov }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum XboxOutput
{
    LeftStickX, LeftStickY, RightStickX, RightStickY,
    LT, RT,
    A, B, X, Y,
    LB, RB,
    LS, RS,
    View, Menu, Guide,
    DPadUp, DPadDown, DPadLeft, DPadRight,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ButtonMode { Normal, Toggle, Hold, Turbo }

public enum XboxOutputKind { StickAxis, Trigger, Button }

public static class XboxOutputInfo
{
    public static XboxOutputKind KindOf(XboxOutput o) => o switch
    {
        XboxOutput.LeftStickX or XboxOutput.LeftStickY or XboxOutput.RightStickX or XboxOutput.RightStickY => XboxOutputKind.StickAxis,
        XboxOutput.LT or XboxOutput.RT => XboxOutputKind.Trigger,
        _ => XboxOutputKind.Button,
    };

    public static bool IsAxisLike(XboxOutput o) => KindOf(o) != XboxOutputKind.Button;

    public static string DisplayName(XboxOutput o) => o switch
    {
        XboxOutput.LeftStickX => "Left Stick X",
        XboxOutput.LeftStickY => "Left Stick Y",
        XboxOutput.RightStickX => "Right Stick X",
        XboxOutput.RightStickY => "Right Stick Y",
        XboxOutput.LS => "Left Stick Click",
        XboxOutput.RS => "Right Stick Click",
        XboxOutput.DPadUp => "D-Pad Up",
        XboxOutput.DPadDown => "D-Pad Down",
        XboxOutput.DPadLeft => "D-Pad Left",
        XboxOutput.DPadRight => "D-Pad Right",
        XboxOutput.Guide => "Xbox",
        _ => o.ToString(),
    };
}

/// <summary>조이스틱의 물리 입력 하나 (축, 버튼, POV 방향).</summary>
public sealed record PhysicalInput
{
    public PhysicalInputKind Kind { get; init; }
    public JoyAxis? Axis { get; init; }
    /// <summary>0부터 시작하는 버튼 인덱스 (표시는 +1).</summary>
    public int? Button { get; init; }
    public int? Pov { get; init; }
    public PovDirection? Direction { get; init; }
    /// <summary>축의 한쪽 방향만 버튼으로 쓸 때: -1(왼쪽/위) 또는 +1(오른쪽/아래). 축 전체면 null.</summary>
    public int? AxisSign { get; init; }

    public static PhysicalInput FromAxis(JoyAxis axis) => new() { Kind = PhysicalInputKind.Axis, Axis = axis };
    public static PhysicalInput FromAxisHalf(JoyAxis axis, int sign) =>
        new() { Kind = PhysicalInputKind.Axis, Axis = axis, AxisSign = sign < 0 ? -1 : 1 };

    [JsonIgnore] public bool IsHalfAxis => Kind == PhysicalInputKind.Axis && AxisSign != null;
    /// <summary>방향을 뗀 물리 입력 (축 전체). 축이 아니면 자기 자신.</summary>
    [JsonIgnore] public PhysicalInput Whole => IsHalfAxis ? this with { AxisSign = null } : this;
    public static PhysicalInput FromButton(int index) => new() { Kind = PhysicalInputKind.Button, Button = index };
    public static PhysicalInput FromPov(int pov, PovDirection dir) => new() { Kind = PhysicalInputKind.Pov, Pov = pov, Direction = dir };

    [JsonIgnore] public string DisplayName => Kind switch
    {
        PhysicalInputKind.Axis => AxisDisplayName(Axis!.Value) + AxisSign switch { < 0 => " −", > 0 => " +", _ => "" },
        PhysicalInputKind.Button => $"Button {Button + 1}",
        _ => $"POV {Pov} {Direction}",
    };

    public static string AxisDisplayName(JoyAxis axis) => axis switch
    {
        JoyAxis.X => "Stick X",
        JoyAxis.Y => "Stick Y",
        JoyAxis.Rz => "Twist (Rz)",
        JoyAxis.Slider0 => "Slider (Throttle)",
        JoyAxis.Slider1 => "Slider 2",
        _ => axis.ToString(),
    };

    public override string ToString() => DisplayName;
}
