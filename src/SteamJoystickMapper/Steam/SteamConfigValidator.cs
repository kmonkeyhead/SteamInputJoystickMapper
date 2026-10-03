using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;
using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.Steam;

public sealed class ValidationResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;

    public void Merge(ValidationResult other)
    {
        Errors.AddRange(other.Errors);
        Warnings.AddRange(other.Warnings);
    }
}

/// <summary>사양서 26장: 적용 전 검증. 오류가 하나라도 있으면 적용하지 않는다.</summary>
public static class SteamConfigValidator
{
    /// <summary>쓰로틀 특수 처리 경고의 앞부분 (적용 때 이 경고만 골라 지운다).</summary>
    public static string ThrottleWarningMarker => T("쓰로틀은 특수 처리가 필요합니다", "The throttle needs special handling");

    /// <summary>
    /// 프로필 전체 검증: 모든 게임 매핑, 그리고 각 게임 매핑을 자동 생성된 장치 레이아웃 위에서 표현할 수 있는지.
    /// </summary>
    public static ValidationResult ValidateMapper(MapperProfile profile)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(profile.ProfileName)) r.Errors.Add(T("프로필 이름이 없습니다.", "The profile has no name."));
        if (profile.Device.IsEmpty)
        {
            r.Errors.Add(T("프로필에 입력 장치가 지정되지 않았습니다.", "No input device is set for the profile."));
            return r;
        }
        if (profile.Games.Count == 0)
        {
            r.Errors.Add(T("게임 매핑이 없습니다. 게임을 선택해 매핑을 만드세요.", "There are no game mappings. Select a game and create a mapping."));
            return r;
        }

        // 축 반전은 장치 설정(모든 게임 공유)에만 있으므로 게임마다 같아야 한다
        var inverts = profile.Games.SelectMany(g => g.Bindings.Select(b => (Game: g, Binding: b)))
            .Where(x => x.Binding.Source.Kind == PhysicalInputKind.Axis && !x.Binding.Source.IsHalfAxis);
        foreach (var axis in inverts.GroupBy(x => x.Binding.Source.Whole).Where(g => g.Select(x => x.Binding.Invert).Distinct().Count() > 1))
        {
            var inverted = string.Join(", ", axis.Where(x => x.Binding.Invert).Select(x => x.Game.GameName).Distinct());
            r.Errors.Add(T($"{axis.Key.DisplayName}: 축 반전은 모든 게임이 같은 장치 설정을 쓰므로 게임마다 같아야 합니다 (반전: {inverted}).",
                           $"{axis.Key.DisplayName}: axis inversion must be the same in every game, because all games share one device layout (inverted: {inverted})."));
        }

        // 쓰로틀(방향 있는 축 → LT/RT)은 특수 처리: 장치 설정은 모든 게임이 공유하므로 [Steam에 적용] 때 쓰로틀을 쓸 게임
        // 하나를 고른다(나머지 게임의 쓰로틀 매핑은 적용되지 않음). 한 게임 안에서 매핑 1개면 0~100%, 2개(−/+)면 0~50 / 50~100.
        foreach (var game in profile.Games)
        {
            var throttle = SteamLayoutPlanner.ThrottleBindings(game).ToList();
            if (throttle.Count == 0) continue;
            r.Warnings.Add($"[{game.GameName}] " + ThrottleWarningMarker + T(
                ": 장치 설정을 모든 게임이 같이 쓰므로, [Steam에 적용]할 때 쓰로틀을 쓸 게임 하나를 고릅니다 (다른 게임의 쓰로틀 매핑은 그때 적용되지 않음).",
                ": all games share one device layout, so you pick one game to use the throttle when you click [Apply to Steam] (other games' throttle mappings are not applied then)."));
            foreach (var axis in throttle.GroupBy(b => b.Source.Axis!.Value).Where(x => x.Count() >= 2))
            {
                var axisName = PhysicalInput.AxisDisplayName(axis.Key);
                if (axis.Count() > 2 || !axis.Any(b => b.Source.AxisSign < 0) || !axis.Any(b => b.Source.AxisSign > 0))
                {
                    r.Errors.Add(T($"[{game.GameName}] {axisName}: 쓰로틀 매핑 2개는 하나는 0%쪽(−), 하나는 100%쪽(+)이어야 합니다.",
                                   $"[{game.GameName}] {axisName}: with two throttle mappings, one must be the 0% side (−) and the other the 100% side (+)."));
                    continue;
                }
                foreach (var b in axis)
                {
                    var (low, high) = b.TriggerRange();
                    var name = $"[{game.GameName}] {b.Source.DisplayName} → {XboxOutputInfo.DisplayName(b.Target)}";
                    if (b.Source.AxisSign < 0 && high > 50)
                        r.Errors.Add(T($"{name}: 쓰로틀 매핑이 2개라 가운데에서 나뉘므로 − 범위는 0~50% 안이어야 합니다 (지금 {low}~{high}%).",
                                       $"{name}: two throttle mappings split the axis in the middle, so the − range must be within 0~50% (now {low}~{high}%)."));
                    if (b.Source.AxisSign > 0 && low < 50)
                        r.Errors.Add(T($"{name}: 쓰로틀 매핑이 2개라 가운데에서 나뉘므로 + 범위는 50~100% 안이어야 합니다 (지금 {low}~{high}%).",
                                       $"{name}: two throttle mappings split the axis in the middle, so the + range must be within 50~100% (now {low}~{high}%)."));
                }
            }
        }

        // 장치 설정 계획은 게임마다 "그 게임이 쓰로틀 게임으로 골랐을 때"로 검사한다
        // (실제 적용 때 쓰로틀은 고른 게임 하나만 들어감)
        var original = profile;
        foreach (var game in original.Games)
        {
            var label = $"[{game.GameName}] ";
            var v = ValidateProfile(original.GameView(game));
            r.Merge(Prefix(label, v));
            if (!v.IsValid) continue;
            var effective = SteamApplyService.WithThrottleFrom(original, game.AppId);
            var layoutFor = SteamLayoutPlanner.BuildLayout(effective, null);
            var reasons = new List<string>();
            if (SteamLayoutPlanner.TryPlan(layoutFor, effective.GameView(effective.FindGame(game.AppId)!), reasons) == null)
                r.Errors.AddRange(reasons.Distinct().Select(x => label + x + T(" (장치 레이아웃으로 표현할 수 없음)", " (cannot be expressed in the device layout)")));
        }
        return r;
    }

    private static ValidationResult Prefix(string prefix, ValidationResult v)
    {
        var r = new ValidationResult();
        r.Errors.AddRange(v.Errors.Select(e => prefix + e));
        r.Warnings.AddRange(v.Warnings.Select(w => prefix + w));
        return r;
    }

    /// <summary>게임 하나의 매핑 검증.</summary>
    public static ValidationResult ValidateProfile(MappingProfile profile)
    {
        var r = new ValidationResult();
        if (!SteamGameScanner.IsValidAppId(profile.AppId)) r.Errors.Add(T($"AppID가 유효하지 않습니다: '{profile.AppId}'", $"Invalid AppID: '{profile.AppId}'"));
        if (!IsHex4(profile.Device.Vid) || !IsHex4(profile.Device.Pid)) r.Errors.Add(T("장치 VID/PID가 유효하지 않습니다.", "Invalid device VID/PID."));
        if (profile.Bindings.Count == 0) r.Errors.Add(T("매핑이 하나도 없습니다.", "There are no mappings."));

        foreach (var b in profile.Bindings)
        {
            var name = $"{b.Source.DisplayName} → {XboxOutputInfo.DisplayName(b.Target)}";
            var targetKind = XboxOutputInfo.KindOf(b.Target);

            if (b.Target == XboxOutput.Guide)
                r.Errors.Add(T($"{name}: Xbox(Guide) 버튼은 Steam이 Steam 메뉴용으로 예약하고 있어 지원하지 않습니다.",
                               $"{name}: the Xbox (Guide) button is reserved by Steam for the Steam menu and is not supported."));
            var axisToButton = b.Source.Kind == PhysicalInputKind.Axis && targetKind == XboxOutputKind.Button;
            if (axisToButton && !b.Source.IsHalfAxis)
                r.Errors.Add(T($"{name}: 축 → 버튼은 축의 한쪽 방향(−/+)을 지정해야 합니다.",
                               $"{name}: axis → button needs one direction of the axis (−/+)."));
            if (b.Source.IsHalfAxis && targetKind == XboxOutputKind.StickAxis)
                r.Errors.Add(T($"{name}: 축의 한쪽 방향은 버튼이나 트리거 출력에만 연결할 수 있습니다.",
                               $"{name}: one direction of an axis can only go to a button or trigger output."));
            if (b.Source.Kind != PhysicalInputKind.Axis && targetKind == XboxOutputKind.StickAxis)
                r.Errors.Add(T($"{name}: 스틱 축 출력에는 축 입력만 연결할 수 있습니다.",
                               $"{name}: only axis inputs can go to a stick axis output."));
            if (b.ButtonMode != ButtonMode.Normal)
                r.Errors.Add(T($"{name}: 버튼 모드 '{b.ButtonMode}'는 아직 지원하지 않습니다 (Normal만 지원).",
                               $"{name}: button mode '{b.ButtonMode}' is not supported yet (Normal only)."));

            switch (b.Source.Kind)
            {
                case PhysicalInputKind.Axis when b.Source.Axis is not { } axis || !profile.Device.Axes.Contains(axis):
                    r.Errors.Add(T($"{name}: 장치에 해당 축이 없습니다.", $"{name}: the device has no such axis."));
                    break;
                case PhysicalInputKind.Button when b.Source.Button is not { } btn || btn < 0 || (profile.Device.ButtonCount > 0 && btn >= profile.Device.ButtonCount):
                    r.Errors.Add(T($"{name}: 장치에 해당 버튼이 없습니다.", $"{name}: the device has no such button."));
                    break;
                case PhysicalInputKind.Pov when b.Source.Pov is not { } pov || b.Source.Direction == null || pov < 0 || (profile.Device.PovCount > 0 && pov >= profile.Device.PovCount):
                    r.Errors.Add(T($"{name}: 장치에 해당 POV가 없습니다.", $"{name}: the device has no such POV."));
                    break;
            }

            // 쓰로틀 → LT/RT: 감지 범위 (축 %)
            if (SteamLayoutPlanner.IsDirectedAxisToTrigger(b))
            {
                var (low, high) = b.TriggerRange();
                if (low < 0 || high > 100 || low >= high)
                    r.Errors.Add(T($"{name}: 감지 범위는 0~100% 안에서 시작 < 끝이어야 합니다 (지금 {low}~{high}%).",
                                   $"{name}: the detection range must be within 0~100% with start < end (now {low}~{high}%)."));
                // 50% 경계(반으로 나눈 쓰로틀)는 모든 게임을 봐야 알 수 있으므로 ValidateMapper에서 검사
            }
            // 축 → 버튼/트리거: 데드존(트리거 범위 시작)을 넘어야 입력됨
            else if (axisToButton || (b.Source.Kind == PhysicalInputKind.Axis && targetKind == XboxOutputKind.Trigger))
            {
                if (b.DeadZone is < 0 or > 95)
                    r.Errors.Add(T($"{name}: 데드존은 0~95% 범위여야 합니다.", $"{name}: dead zone must be 0~95%."));
            }
            else if (b.DeadZone is < 0 or > 50 || b.OuterDeadZone is < 0 or > 50)
                r.Errors.Add(T($"{name}: 데드존은 0~50% 범위여야 합니다.", $"{name}: dead zone must be 0~50%."));
            if (b.Source.Kind != PhysicalInputKind.Axis && b.Invert)
                r.Warnings.Add(T($"{name}: 반전은 축 입력에만 적용됩니다 (무시됨).", $"{name}: invert only applies to axis inputs (ignored)."));
        }

        // 출력 하나에 입력 여러 개는 버튼/트리거만 (둘 중 아무거나로 입력). 스틱 축은 축 하나만.
        foreach (var g in profile.Bindings.GroupBy(b => b.Target).Where(g => g.Count() > 1 && XboxOutputInfo.KindOf(g.Key) == XboxOutputKind.StickAxis))
            r.Errors.Add(T($"출력 충돌: {XboxOutputInfo.DisplayName(g.Key)}에 입력이 {g.Count()}개 연결되어 있습니다 (스틱 축은 입력 하나만).",
                           $"Output conflict: {g.Count()} inputs are connected to {XboxOutputInfo.DisplayName(g.Key)} (a stick axis takes only one input)."));
        foreach (var g in profile.Bindings.GroupBy(b => b.Target)
                     .Where(g => XboxOutputInfo.KindOf(g.Key) == XboxOutputKind.Trigger && g.Count(b => b.Source.Kind == PhysicalInputKind.Axis) > 1))
            r.Errors.Add(T($"출력 충돌: {XboxOutputInfo.DisplayName(g.Key)}에 아날로그 축이 여러 개입니다 (축은 하나, 버튼은 여러 개 가능).",
                           $"Output conflict: {XboxOutputInfo.DisplayName(g.Key)} has several analog axes (one axis, but several buttons are allowed)."));

        // 축/입력 중복
        foreach (var g in profile.Bindings.GroupBy(b => b.Source).Where(g => g.Count() > 1))
        {
            var targets = string.Join(", ", g.Select(b => XboxOutputInfo.DisplayName(b.Target)));
            r.Warnings.Add(T($"입력 중복: {g.Key.DisplayName}이(가) {targets}에 동시에 연결되어 있습니다.",
                             $"Duplicate input: {g.Key.DisplayName} is connected to {targets} at the same time."));
        }

        // 같은 스틱의 X/Y 데드존이 다르면 큰 값 사용
        foreach (var stick in new[] { (XboxOutput.LeftStickX, XboxOutput.LeftStickY), (XboxOutput.RightStickX, XboxOutput.RightStickY) })
        {
            var x = profile.Bindings.FirstOrDefault(b => b.Target == stick.Item1);
            var y = profile.Bindings.FirstOrDefault(b => b.Target == stick.Item2);
            if (x != null && y != null && (x.DeadZone != y.DeadZone || x.OuterDeadZone != y.OuterDeadZone))
                r.Warnings.Add(XboxOutputInfo.DisplayName(stick.Item1)[..^2] + T(": Steam 데드존은 스틱 단위라 X/Y 중 큰 값이 사용됩니다.",
                                                                                 ": Steam dead zones are per stick, so the larger of X/Y is used."));
        }
        return r;
    }

    public static void ValidateControllerId(ValidationResult r, string controllerId)
    {
        if (!SteamInputConfigFinder.ControllerIdPattern.IsMatch(controllerId))
            r.Errors.Add(T($"Controller ID가 유효하지 않습니다: {controllerId}", $"Invalid controller ID: {controllerId}"));
    }

    public static void ValidatePerGame(ValidationResult r, string label, string text) =>
        CheckVdf(r, label, text, root =>
        {
            var m = root.Get("controller_mappings");
            if (m == null) return Missing("controller_mappings");
            if (!m.GetAll("group").Any()) return Missing("group");
            if (!m.GetAll("preset").Any()) return Missing("preset");
            return null;
        });

    private static string Missing(string key) => T($"{key} 없음", $"{key} missing");

    public static void ValidateConfigset(ValidationResult r, string text) =>
        CheckVdf(r, "configset", text, root => root.Get("controller_config") == null ? Missing("controller_config") : null);

    public static void ValidateGlobal(ValidationResult r, string text, SdlMapping? layout)
    {
        CheckVdf(r, "config.vdf", text, root => root.Children!.Count == 0 ? T("비어 있음", "empty") : null);
        if (layout != null && SdlMapping.TryParse(layout.ToString()) == null)
            r.Errors.Add(T("생성된 장치 레이아웃(SDL 매핑) 형식이 올바르지 않습니다.", "The generated device layout (SDL mapping) is malformed."));
    }

    public static ValidationResult ValidateGenerated(string controllerId, string perGameText, string configsetText, string? globalText, SdlMapping? layout)
    {
        var r = new ValidationResult();
        ValidateControllerId(r, controllerId);
        ValidatePerGame(r, T("게임별 설정", "per-game config"), perGameText);
        ValidateConfigset(r, configsetText);
        if (globalText != null) ValidateGlobal(r, globalText, layout);
        return r;
    }
    private static void CheckVdf(ValidationResult r, string label, string text, Func<VdfNode, string?> structureCheck)
    {
        try
        {
            var root = VdfParser.Parse(text);
            var problem = structureCheck(root);
            if (problem != null) r.Errors.Add($"{label}: {problem}");
        }
        catch (VdfParseException ex)
        {
            r.Errors.Add($"{label}: " + T("VDF 구문 오류", "VDF syntax error") + $" - {ex.Message}");
        }
    }

    private static bool IsHex4(string s) => s.Length is > 0 and <= 4 && s.All(Uri.IsHexDigit);
}
