using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Sdl;

namespace SteamJoystickMapper.Steam;

public enum StickSide { Left, Right }

/// <summary>게임별 Steam Input 설정(VDF)에 써야 할 내용. Steam "소스" 이름 기준.</summary>
public sealed class PerGamePlan
{
    /// <summary>(소스 그룹, 입력) → xinput 버튼 이름. 예: ("button_diamond","button_a") → "B".</summary>
    public Dictionary<(string Source, string Input), string> ButtonBindings { get; } = new();
    /// <summary>"joystick"/"right_joystick" → 출력 스틱.</summary>
    public Dictionary<string, StickSide> StickOutputs { get; } = new();
    /// <summary>"left_trigger"/"right_trigger" → output_trigger (0 없음, 1 LT, 2 RT).</summary>
    public Dictionary<string, int> TriggerOutputs { get; } = new();
    /// <summary>트리거 소스별 Steam 트리거 범위 시작/끝 (0~32767). 끝이 null이면 Steam 기본값.</summary>
    public Dictionary<string, (int Start, int? End)> TriggerRanges { get; } = new();
    /// <summary>스틱 소스별 데드존 (%): (안쪽, 바깥쪽).</summary>
    public Dictionary<string, (int Inner, int Outer)> StickDeadZones { get; } = new();
    /// <summary>
    /// 방향 패드 모드로 쓸 스틱 소스 → 데드존 (%). 축 → 버튼(트위스트 −/+ → LB/RB 등)에 사용.
    /// 버튼 자체는 ButtonBindings의 (소스, dpad_west/east/north/south)에 들어간다.
    /// </summary>
    public Dictionary<string, int> DpadStickDeadZones { get; } = new();

    /// <summary>스틱 소스의 그룹 모드.</summary>
    public string StickMode(string source) => DpadStickDeadZones.ContainsKey(source) ? "dpad" : "joystick_move";

    public bool IsSourceUsed(string source) =>
        StickOutputs.ContainsKey(source) || TriggerOutputs.GetValueOrDefault(source) != 0 ||
        ButtonBindings.Keys.Any(k => k.Source == source);
}

/// <summary>
/// Steam 일반 컨트롤러는 2단 구조다:
///   (1) 장치 레이아웃 [장치당 1개, 모든 게임 공유, config.vdf SDL_GamepadBind]: 물리 입력 → 게임패드 요소
///   (2) 게임별 설정 [appid 폴더 VDF]: 게임패드 요소(소스) → Xbox 출력
/// (1)은 모든 게임 매핑에 쓰인 입력으로 자동 생성하고(BuildLayout), 각 게임 매핑은 (2)에서 각 자리의 출력을 바꿔 표현한다.
/// 게임별 파일이 없는 게임은 Steam 기본 템플릿이 (1)의 자리를 그대로 통과시킨다.
/// </summary>
public static class SteamLayoutPlanner
{
    public const int SteamAxisMax = 32767;

    /// <summary>두 레이아웃의 요소 매핑이 같은지 (순서/메타 필드 무시).</summary>
    public static bool SameLayout(SdlMapping a, SdlMapping b)
    {
        static HashSet<string> Set(SdlMapping m) =>
            m.ElementFields.Select(f => $"{f.Key.ToLowerInvariant()}:{f.Value}").ToHashSet();
        return Set(a).SetEquals(Set(b));
    }

    /// <summary>매핑 하나만으로 만든 장치 레이아웃 (테스트/단일 게임용).</summary>
    public static SdlMapping BuildNaturalLayout(MappingProfile profile, string? guid) =>
        BuildLayout(new MapperProfile
        {
            Device = profile.Device,
            Games = { new GameMapping { AppId = profile.AppId, GameName = profile.GameName, Bindings = profile.Bindings } },
        }, guid);

    // 자연스러운 자리가 이미 찼을 때 쓸 빈 자리 (덜 쓰이는 자리부터)
    private static readonly string[] FreeStickElements = { "rightx", "righty", "leftx", "lefty" };
    private static readonly string[] FreeTriggerElements = { "lefttrigger", "righttrigger" };
    private static readonly string[] FreeButtonElements =
    {
        "leftstick", "rightstick", "back", "start", "leftshoulder", "rightshoulder",
        "x", "y", "a", "b", "dpup", "dpdown", "dpleft", "dpright",
    };

    /// <summary>
    /// 장치 레이아웃(config.vdf, 장치당 1개)을 모든 게임 매핑에서 자동으로 만든다.
    /// 게임 매핑에 쓰인 물리 입력을 하나도 빠짐없이 레이아웃 자리에 올린다:
    ///   1) 처음 쓰인 출력의 자연스러운 자리(버튼1 → A면 A 자리)가 비어 있으면 그 자리
    ///   2) 아니면 같은 종류의 빈 자리 (축 → 버튼은 스틱 빈 자리)
    /// 축 한쪽 방향 → 트리거(쓰로틀 아래쪽 → LT, 위쪽 → RT)는 반쪽 축 그대로 트리거 자리에 둔다 (예: "lefttrigger:-a3").
    /// 게임별 설정이 각 자리를 게임마다 원하는 출력으로 다시 연결한다. 축 반전은 레이아웃에만 있으므로 처음 쓰인 값을 따른다.
    /// </summary>
    public static SdlMapping BuildLayout(MapperProfile profile, string? guid)
    {
        var device = profile.Device;
        var layout = new SdlMapping
        {
            Guid = guid ?? SdlMapping.GuidFor(device.VidValue, device.PidValue),
            Name = string.IsNullOrWhiteSpace(device.Name) ? "Joystick" : device.Name,
        };
        var placed = new HashSet<string>();
        var pending = new List<MappingBinding>();

        // 쓰로틀 → LT/RT (장치 설정은 공유라, 적용할 때 쓰로틀을 쓸 게임 하나만 남긴 프로필로 만든다 — SteamApplyService):
        //  - 그 게임의 쓰로틀 매핑 1개: 축 전체. + 는 "a3"(확인됨), − 는 뒤집어서 "a3~". 범위 0~100%.
        //  - 2개(−/+): Steam 자체 항목("lefttrigger:+a2,righttrigger:-a2")과 같이 반쪽 "-a3"/"+a3". 범위 0~50 / 50~100.
        //    사용자 PC에서 확인: "lefttrigger:a3"와 함께 쓴 "righttrigger:a3~"/"-a3"는 움직이지 않음.
        var split = SplitAxes(profile);
        string? LayoutSource(MappingBinding b, bool invert) =>
            IsDirectedAxisToTrigger(b)
                ? split.Contains(b.Source.Axis!.Value)
                    ? (b.Source.AxisSign < 0 ? "-" : "+") + SdlElements.SourceFor(device, b.Source.Whole, invert: false)
                    : SdlElements.SourceFor(device, b.Source.Whole, invert: b.Source.AxisSign < 0)
                : SdlElements.SourceFor(device, b.Source.Whole, invert);
        // 같은 축이라도 트리거용(방향별)과 스틱/버튼용은 서로 다른 자리에 둔다
        string? Key(MappingBinding b) => IsDirectedAxisToTrigger(b) ? "T:" + LayoutSource(b, false) : LayoutSource(b, false);

        void Place(string element, MappingBinding b, string key)
        {
            var invert = b.Invert && b.Source.Kind == PhysicalInputKind.Axis && !b.Source.IsHalfAxis;
            layout.Set(element, LayoutSource(b, invert)!);
            placed.Add(key);
        }

        foreach (var b in profile.Games.SelectMany(g => g.Bindings))
        {
            var key = Key(b);
            if (key == null || placed.Contains(key)) continue;
            var natural = b.Source.IsHalfAxis && !IsDirectedAxisToTrigger(b) ? null : SdlElements.NaturalElement(b.Target);
            if (natural != null && layout.Get(natural) == null && Fits(b.Source, natural)) Place(natural, b, key);
            else pending.Add(b);
        }
        foreach (var b in pending)
        {
            var key = Key(b)!;
            if (placed.Contains(key)) continue;
            var candidates = b.Source.Kind == PhysicalInputKind.Axis
                ? (XboxOutputInfo.KindOf(b.Target) == XboxOutputKind.Trigger
                    ? (IsDirectedAxisToTrigger(b) ? FreeTriggerElements : FreeTriggerElements.Concat(FreeStickElements))
                    : FreeStickElements)
                : FreeButtonElements;
            var element = candidates.FirstOrDefault(e => layout.Get(e) == null);
            if (element != null) Place(element, b, key); // 자리가 없으면 TryPlan이 이유와 함께 거부한다
        }
        layout.Fields.Add(new("platform", "Windows"));
        return layout;
    }

    /// <summary>방향(−/+)이 있는 축 → LT/RT: 감지 범위(TriggerRange)를 게임별 트리거 범위 시작/끝으로 표현한다.</summary>
    public static bool IsDirectedAxisToTrigger(MappingBinding b) =>
        b.Source.IsHalfAxis && XboxOutputInfo.KindOf(b.Target) == XboxOutputKind.Trigger;

    /// <summary>게임 하나의 쓰로틀(방향 있는 축 → LT/RT) 매핑.</summary>
    public static IEnumerable<MappingBinding> ThrottleBindings(GameMapping game) => game.Bindings.Where(IsDirectedAxisToTrigger);

    /// <summary>반으로 나눠 쓰는 축: 한 게임에서 같은 축을 쓰로틀 매핑 2개(−/+)에 쓴 축.</summary>
    public static HashSet<JoyAxis> SplitAxes(MapperProfile profile) =>
        profile.Games
            .SelectMany(g => ThrottleBindings(g).GroupBy(b => b.Source.Axis!.Value).Where(x => x.Count() >= 2))
            .Select(x => x.Key).ToHashSet();

    /// <summary>
    /// 감지 범위(축 %) → Steam 트리거 범위 시작/끝 (0~32767).
    /// 단일(축 전체): + 는 p% 그대로, − 는 뒤집힌 축이라 (100 - p)%.
    /// split(반쪽 축): 쓰로틀 50%에서 0. −: p% → (50 - p) * 2 %, +: p% → (p - 50) * 2 %, 50% 넘는 부분은 잘림.
    /// </summary>
    public static (int Start, int End) TriggerRangeUnits(MappingBinding b, bool split)
    {
        var (low, high) = b.TriggerRange();
        var (start, end) = !split
            ? (b.Source.AxisSign < 0 ? (100 - high, 100 - low) : (low, high))
            : b.Source.AxisSign < 0
                ? ((50 - Math.Min(high, 50)) * 2, (50 - Math.Min(low, 50)) * 2)
                : ((Math.Max(low, 50) - 50) * 2, (Math.Max(high, 50) - 50) * 2);
        // 범위가 축 끝까지 닿으면 Steam 기본값(32000)을 쓴다: 축이 정확히 끝까지 가지 않아도 최대가 되도록
        return (InnerDeadZoneUnits(start), end >= 100 ? SteamTriggerRangeEndDefault : InnerDeadZoneUnits(end));
    }

    /// <summary>Steam "트리거 범위 끝" 기본값 (Steam 설정 화면에서 확인).</summary>
    public const int SteamTriggerRangeEndDefault = 32000;

    /// <summary>
    /// 축은 스틱/트리거 자리에만, 버튼/POV는 버튼형 자리에만. 트리거 자리는 아날로그 축(쓰로틀 등)용으로 남겨 두고,
    /// 버튼 → LT/RT는 다른 버튼 자리에 두고 게임별 설정에서 "xinput_button TRIGGER_LEFT/RIGHT"로 누른다.
    /// </summary>
    private static bool Fits(PhysicalInput source, string element) => source.Kind == PhysicalInputKind.Axis
        ? FreeStickElements.Contains(element) || FreeTriggerElements.Contains(element)
        : ButtonSlot(element) != null && !FreeTriggerElements.Contains(element);

    // ---- 요소 ↔ 게임별 소스 ----

    /// <summary>버튼형으로 사용할 수 있는 요소 → (소스 그룹, 입력).</summary>
    public static (string Source, string Input)? ButtonSlot(string element) => element.ToLowerInvariant() switch
    {
        "a" => ("button_diamond", "button_a"),
        "b" => ("button_diamond", "button_b"),
        "x" => ("button_diamond", "button_x"),
        "y" => ("button_diamond", "button_y"),
        "dpup" => ("dpad", "dpad_north"),
        "dpdown" => ("dpad", "dpad_south"),
        "dpleft" => ("dpad", "dpad_west"),
        "dpright" => ("dpad", "dpad_east"),
        "start" => ("switch", "button_escape"),
        "back" => ("switch", "button_menu"),
        "leftshoulder" => ("switch", "left_bumper"),
        "rightshoulder" => ("switch", "right_bumper"),
        "leftstick" => ("joystick", "click"),
        "rightstick" => ("right_joystick", "click"),
        "lefttrigger" => ("left_trigger", "click"),
        "righttrigger" => ("right_trigger", "click"),
        _ => null,
    };

    private static (string Source, char Component)? StickAxis(string element) => element.ToLowerInvariant() switch
    {
        "leftx" => ("joystick", 'x'),
        "lefty" => ("joystick", 'y'),
        "rightx" => ("right_joystick", 'x'),
        "righty" => ("right_joystick", 'y'),
        _ => null,
    };

    private static string? TriggerSource(string element) => element.ToLowerInvariant() switch
    {
        "lefttrigger" => "left_trigger",
        "righttrigger" => "right_trigger",
        _ => null,
    };

    /// <summary>Xbox 버튼 출력 → Steam 바인딩 문자열의 xinput 이름 (Steam 생성 파일에서 확인된 이름만).</summary>
    public static string? XInputName(XboxOutput o) => o switch
    {
        XboxOutput.A => "A",
        XboxOutput.B => "B",
        XboxOutput.X => "X",
        XboxOutput.Y => "Y",
        XboxOutput.LB => "shoulder_left",
        XboxOutput.RB => "shoulder_right",
        XboxOutput.LS => "JOYSTICK_LEFT",
        XboxOutput.RS => "JOYSTICK_RIGHT",
        XboxOutput.View => "select",
        XboxOutput.Menu => "start",
        XboxOutput.DPadUp => "dpad_up",
        XboxOutput.DPadDown => "dpad_down",
        XboxOutput.DPadLeft => "dpad_left",
        XboxOutput.DPadRight => "dpad_right",
        XboxOutput.LT => "TRIGGER_LEFT",
        XboxOutput.RT => "TRIGGER_RIGHT",
        _ => null,
    };

    /// <summary>주어진 장치 레이아웃으로 프로필을 게임별 설정만으로 표현해 본다. 불가능하면 null.</summary>
    public static PerGamePlan? TryPlan(SdlMapping layout, MappingProfile profile, List<string> reasons)
    {
        var device = profile.Device;
        var plan = new PerGamePlan();
        var usedElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 물리 소스 키(a0, b3, h0.1) → 레이아웃 요소 목록
        var reverse = new Dictionary<string, List<(string Element, SdlElements.ParsedSource Src)>>();
        foreach (var field in layout.ElementFields)
        {
            if (SdlElements.Parse(field.Value) is not { } parsed) continue;
            if (!reverse.TryGetValue(parsed.BaseKey, out var list)) reverse[parsed.BaseKey] = list = new();
            list.Add((field.Key, parsed));
        }

        foreach (var b in profile.Bindings)
        {
            var key = SdlElements.SourceFor(device, b.Source, invert: false);
            if (key == null) { reasons.Add($"{b.Source.DisplayName}: 장치에 없는 입력"); return null; }
            var candidates = reverse.GetValueOrDefault(key) ?? new();
            var kind = XboxOutputInfo.KindOf(b.Target);
            var ok = false;

            foreach (var (element, src) in candidates)
            {
                if (kind == XboxOutputKind.StickAxis)
                {
                    if (src.Type != 'a' || src.HalfSign != null || src.Inverted != b.Invert) continue;
                    if (StickAxis(element) is not { } stick) continue;
                    var (stickSource, component) = stick;
                    var targetSide = b.Target is XboxOutput.LeftStickX or XboxOutput.LeftStickY ? StickSide.Left : StickSide.Right;
                    var targetComponent = b.Target is XboxOutput.LeftStickX or XboxOutput.RightStickX ? 'x' : 'y';
                    if (component != targetComponent) continue;
                    // 오른쪽 스틱 소스 → 왼쪽 스틱 출력은 Steam 설정 값이 확인되지 않아 지원하지 않는다.
                    if (stickSource == "right_joystick" && targetSide == StickSide.Left) continue;
                    if (plan.StickOutputs.TryGetValue(stickSource, out var existingSide) && existingSide != targetSide) continue;
                    if (plan.DpadStickDeadZones.ContainsKey(stickSource))
                    {
                        reasons.Add($"{b.Source.DisplayName}: 같은 스틱 자리를 버튼(방향 패드)으로 쓰고 있어 스틱 출력에 함께 쓸 수 없습니다");
                        return null;
                    }
                    plan.StickOutputs[stickSource] = targetSide;
                    var dz = plan.StickDeadZones.GetValueOrDefault(stickSource);
                    plan.StickDeadZones[stickSource] = (Math.Max(dz.Inner, b.DeadZone), Math.Max(dz.Outer, b.OuterDeadZone));
                    usedElements.Add(element);
                    ok = true;
                    break;
                }

                if (kind == XboxOutputKind.Trigger && src.Type == 'a')
                {
                    var directed = b.Source.IsHalfAxis;
                    // 쓰로틀(방향 있음): 나눈 축이면 같은 방향 반쪽 자리("-aN"/"+aN"), 단일이면 축 전체 자리(− 는 뒤집힌 자리).
                    // 방향 없는 축 → 트리거: 축 전체 자리, 매핑의 반전 값.
                    if (directed && src.HalfSign != null && src.HalfSign != (b.Source.AxisSign < 0 ? '-' : '+')) continue;
                    if (!directed && src.HalfSign != null) continue;
                    if (src.HalfSign == null && src.Inverted != (directed ? b.Source.AxisSign < 0 : b.Invert)) continue;
                    if (TriggerSource(element) is not { } trig) continue;
                    var output = b.Target == XboxOutput.LT ? 1 : 2;
                    if (plan.TriggerOutputs.TryGetValue(trig, out var existingOut) && existingOut != 0 && existingOut != output) continue;
                    plan.TriggerOutputs[trig] = output;
                    // Steam 기본 트리거 설정과 같이 "끝까지 당기기"도 같은 트리거로: 끝까지 당기면 확실히 최대
                    plan.ButtonBindings.TryAdd((trig, "click"), output == 1 ? "TRIGGER_LEFT" : "TRIGGER_RIGHT");
                    if (directed) plan.TriggerRanges[trig] = TriggerRangeUnits(b, split: src.HalfSign != null);
                    else if (b.DeadZone > 0) plan.TriggerRanges[trig] = (InnerDeadZoneUnits(b.DeadZone), null);
                    usedElements.Add(element);
                    ok = true;
                    break;
                }

                // 축 한쪽 방향 → 버튼: 장치 레이아웃에서 스틱 축 자리에 있는 축을, 게임별 설정에서 그 스틱을 방향 패드 모드로 바꿔 표현
                if (kind == XboxOutputKind.Button && b.Source.IsHalfAxis)
                {
                    if (src.Type != 'a' || src.HalfSign != null || StickAxis(element) is not { } dstick) continue;
                    var xinputName = XInputName(b.Target);
                    if (xinputName == null) continue;
                    if (plan.StickOutputs.ContainsKey(dstick.Source))
                    {
                        reasons.Add($"{b.Source.DisplayName}: 같은 스틱 자리({element})를 스틱 출력으로도 쓰고 있어 버튼으로 바꿀 수 없습니다");
                        return null;
                    }
                    // 장치 레이아웃에서 반전된 축이면 Steam이 보는 방향도 반대
                    var steamSign = b.Source.AxisSign!.Value * (src.Inverted ? -1 : 1);
                    var dpadInput = dstick.Component == 'x'
                        ? (steamSign < 0 ? "dpad_west" : "dpad_east")
                        : (steamSign < 0 ? "dpad_north" : "dpad_south"); // SDL Y는 아래가 +
                    var dslot = (dstick.Source, dpadInput);
                    if (plan.ButtonBindings.TryGetValue(dslot, out var prevX) && prevX != xinputName) continue;
                    plan.ButtonBindings[dslot] = xinputName;
                    plan.DpadStickDeadZones[dstick.Source] = Math.Max(plan.DpadStickDeadZones.GetValueOrDefault(dstick.Source), b.DeadZone);
                    usedElements.Add(element);
                    ok = true;
                    break;
                }

                // 버튼형: 버튼/POV 소스 → 버튼(또는 디지털 트리거) 출력
                if (src.Type is 'b' or 'h' && src.HalfSign == null && ButtonSlot(element) is { } slot)
                {
                    var xinput = XInputName(b.Target);
                    if (xinput == null) continue;
                    if (plan.ButtonBindings.TryGetValue(slot, out var existingX) && existingX != xinput) continue;
                    plan.ButtonBindings[slot] = xinput;
                    if (TriggerSource(element) is { } trigSrc) plan.TriggerOutputs.TryAdd(trigSrc, 0);
                    usedElements.Add(element);
                    ok = true;
                    break;
                }
            }

            if (!ok)
            {
                reasons.Add(candidates.Count == 0
                    ? $"{b.Source.DisplayName}: 장치 설정에 넣을 빈 자리가 없습니다 (모든 게임을 합쳐 쓰는 입력이 너무 많음)"
                    : $"{b.Source.DisplayName} → {XboxOutputInfo.DisplayName(b.Target)}: 현재 레이아웃({string.Join(", ", candidates.Select(c => c.Element))})으로 표현 불가");
                return null;
            }
        }

        // 사용 중인 스틱 그룹에 프로필에 없는 축이 섞여 있으면 의도치 않은 입력이 나간다.
        foreach (var field in layout.ElementFields)
        {
            if (usedElements.Contains(field.Key)) continue;
            if (StickAxis(field.Key) is { } stick && plan.StickOutputs.ContainsKey(stick.Source))
            {
                reasons.Add($"같은 스틱 자리({field.Key})의 다른 축도 함께 출력되므로 그 축도 같은 스틱에 매핑해야 합니다");
                return null;
            }
        }

        return plan;
    }

    public static int InnerDeadZoneUnits(int percent) => Math.Clamp(percent, 0, 100) * SteamAxisMax / 100;
    public static int OuterDeadZoneUnits(int percent) => SteamAxisMax - Math.Clamp(percent, 0, 100) * SteamAxisMax / 100;
}
