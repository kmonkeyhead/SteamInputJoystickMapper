using SteamJoystickMapper.Mapping;

namespace SteamJoystickMapper.Steam.Sdl;

/// <summary>
/// SDL 게임패드 요소 이름과, 물리 입력 → SDL 소스 문자열(a0, b3, h0.1 ...) 변환.
/// </summary>
public static class SdlElements
{
    public static readonly string[] AllElements =
    {
        "a", "b", "x", "y", "back", "guide", "start", "leftstick", "rightstick", "leftshoulder", "rightshoulder",
        "dpup", "dpdown", "dpleft", "dpright", "misc1", "paddle1", "paddle2", "paddle3", "paddle4", "touchpad",
        "leftx", "lefty", "rightx", "righty", "lefttrigger", "righttrigger",
    };

    public static bool IsElement(string key) => AllElements.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Xbox 출력의 "자연스러운" SDL 요소. Guide는 Steam이 예약하므로 지원하지 않는다.</summary>
    public static string? NaturalElement(XboxOutput output) => output switch
    {
        XboxOutput.LeftStickX => "leftx",
        XboxOutput.LeftStickY => "lefty",
        XboxOutput.RightStickX => "rightx",
        XboxOutput.RightStickY => "righty",
        XboxOutput.LT => "lefttrigger",
        XboxOutput.RT => "righttrigger",
        XboxOutput.A => "a",
        XboxOutput.B => "b",
        XboxOutput.X => "x",
        XboxOutput.Y => "y",
        XboxOutput.LB => "leftshoulder",
        XboxOutput.RB => "rightshoulder",
        XboxOutput.LS => "leftstick",
        XboxOutput.RS => "rightstick",
        XboxOutput.View => "back",
        XboxOutput.Menu => "start",
        XboxOutput.DPadUp => "dpup",
        XboxOutput.DPadDown => "dpdown",
        XboxOutput.DPadLeft => "dpleft",
        XboxOutput.DPadRight => "dpright",
        _ => null,
    };

    /// <summary>
    /// 장치 축 목록에서 SDL 축 인덱스를 계산한다.
    /// SDL(RawInput/HIDAPI/DirectInput 백엔드 모두)은 존재하는 축을 HID Usage 순서로 번호 매긴다.
    /// </summary>
    public static int? AxisIndex(DeviceIdentity device, JoyAxis axis)
    {
        var ordered = device.Axes.Distinct().OrderBy(a => (int)a).ToList();
        var index = ordered.IndexOf(axis);
        return index >= 0 ? index : null;
    }

    public static int PovMask(PovDirection d) => d switch
    {
        PovDirection.Up => 1,
        PovDirection.Right => 2,
        PovDirection.Down => 4,
        _ => 8,
    };

    /// <summary>물리 입력을 SDL 소스 문자열로. 축 반전은 "~" 접미사.</summary>
    public static string? SourceFor(DeviceIdentity device, PhysicalInput input, bool invert)
    {
        switch (input.Kind)
        {
            case PhysicalInputKind.Axis:
                var idx = AxisIndex(device, input.Axis!.Value);
                return idx == null ? null : $"a{idx}" + (invert ? "~" : "");
            case PhysicalInputKind.Button:
                return $"b{input.Button}";
            case PhysicalInputKind.Pov:
                return $"h{input.Pov}.{PovMask(input.Direction!.Value)}";
            default:
                return null;
        }
    }

    /// <summary>SDL 소스 문자열 분석 결과.</summary>
    public readonly record struct ParsedSource(char Type, int Index, int HatMask, char? HalfSign, bool Inverted)
    {
        public string BaseKey => Type == 'h' ? $"h{Index}.{HatMask}" : $"{Type}{Index}";
    }

    public static ParsedSource? Parse(string source)
    {
        var s = source.Trim();
        char? half = null;
        var inverted = false;
        if (s.StartsWith('+') || s.StartsWith('-')) { half = s[0]; s = s[1..]; }
        if (s.EndsWith('~')) { inverted = true; s = s[..^1]; }
        if (s.Length < 2) return null;
        var type = s[0];
        if (type is 'a' or 'b')
            return int.TryParse(s[1..], out var i) ? new ParsedSource(type, i, 0, half, inverted) : null;
        if (type == 'h')
        {
            var dot = s.IndexOf('.');
            if (dot < 0 || !int.TryParse(s[1..dot], out var hat) || !int.TryParse(s[(dot + 1)..], out var mask)) return null;
            return new ParsedSource('h', hat, mask, half, inverted);
        }
        return null;
    }
}
