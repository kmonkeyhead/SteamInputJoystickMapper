using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;

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
    /// <summary>
    /// 프로필 전체 검증: 모든 게임 매핑, 그리고 각 게임 매핑을 자동 생성된 장치 레이아웃 위에서 표현할 수 있는지.
    /// </summary>
    public static ValidationResult ValidateMapper(MapperProfile profile)
    {
        var r = new ValidationResult();
        if (string.IsNullOrWhiteSpace(profile.ProfileName)) r.Errors.Add("프로필 이름이 없습니다.");
        if (profile.Device.IsEmpty)
        {
            r.Errors.Add("프로필에 입력 장치가 지정되지 않았습니다.");
            return r;
        }
        if (profile.Games.Count == 0)
        {
            r.Errors.Add("게임 매핑이 없습니다. 게임을 선택해 매핑을 만드세요.");
            return r;
        }

        // 축 반전은 장치 설정(모든 게임 공유)에만 있으므로 게임마다 같아야 한다
        var inverts = profile.Games.SelectMany(g => g.Bindings.Select(b => (Game: g, Binding: b)))
            .Where(x => x.Binding.Source.Kind == PhysicalInputKind.Axis && !x.Binding.Source.IsHalfAxis);
        foreach (var axis in inverts.GroupBy(x => x.Binding.Source.Whole).Where(g => g.Select(x => x.Binding.Invert).Distinct().Count() > 1))
            r.Errors.Add($"{axis.Key.DisplayName}: 축 반전은 모든 게임이 같은 장치 설정을 쓰므로 게임마다 같아야 합니다 " +
                         $"(반전: {string.Join(", ", axis.Where(x => x.Binding.Invert).Select(x => x.Game.GameName).Distinct())}).");

        var layout = SteamLayoutPlanner.BuildLayout(profile, null);
        foreach (var game in profile.Games)
        {
            var label = $"[{game.GameName}] ";
            var view = profile.GameView(game);
            var v = ValidateProfile(view);
            r.Merge(Prefix(label, v));
            if (!v.IsValid) continue;
            var reasons = new List<string>();
            if (SteamLayoutPlanner.TryPlan(layout, view, reasons) == null)
                r.Errors.AddRange(reasons.Distinct().Select(x => label + x + " (장치 레이아웃으로 표현할 수 없음)"));
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
        if (!SteamGameScanner.IsValidAppId(profile.AppId)) r.Errors.Add($"AppID가 유효하지 않습니다: '{profile.AppId}'");
        if (!IsHex4(profile.Device.Vid) || !IsHex4(profile.Device.Pid)) r.Errors.Add("장치 VID/PID가 유효하지 않습니다.");
        if (profile.Bindings.Count == 0) r.Errors.Add("매핑이 하나도 없습니다.");

        foreach (var b in profile.Bindings)
        {
            var name = $"{b.Source.DisplayName} → {XboxOutputInfo.DisplayName(b.Target)}";
            var targetKind = XboxOutputInfo.KindOf(b.Target);

            if (b.Target == XboxOutput.Guide)
                r.Errors.Add($"{name}: Xbox(Guide) 버튼은 Steam이 Steam 메뉴용으로 예약하고 있어 지원하지 않습니다.");
            var axisToButton = b.Source.Kind == PhysicalInputKind.Axis && targetKind == XboxOutputKind.Button;
            if (axisToButton && !b.Source.IsHalfAxis)
                r.Errors.Add($"{name}: 축 → 버튼은 축의 한쪽 방향(−/+)을 지정해야 합니다.");
            if (b.Source.IsHalfAxis && targetKind == XboxOutputKind.StickAxis)
                r.Errors.Add($"{name}: 축의 한쪽 방향은 버튼이나 트리거 출력에만 연결할 수 있습니다.");
            if (b.Source.Kind != PhysicalInputKind.Axis && targetKind == XboxOutputKind.StickAxis)
                r.Errors.Add($"{name}: 스틱 축 출력에는 축 입력만 연결할 수 있습니다.");
            if (b.ButtonMode != ButtonMode.Normal)
                r.Errors.Add($"{name}: 버튼 모드 '{b.ButtonMode}'는 아직 지원하지 않습니다 (Normal만 지원).");

            switch (b.Source.Kind)
            {
                case PhysicalInputKind.Axis when b.Source.Axis is not { } axis || !profile.Device.Axes.Contains(axis):
                    r.Errors.Add($"{name}: 장치에 해당 축이 없습니다.");
                    break;
                case PhysicalInputKind.Button when b.Source.Button is not { } btn || btn < 0 || (profile.Device.ButtonCount > 0 && btn >= profile.Device.ButtonCount):
                    r.Errors.Add($"{name}: 장치에 해당 버튼이 없습니다.");
                    break;
                case PhysicalInputKind.Pov when b.Source.Pov is not { } pov || b.Source.Direction == null || pov < 0 || (profile.Device.PovCount > 0 && pov >= profile.Device.PovCount):
                    r.Errors.Add($"{name}: 장치에 해당 POV가 없습니다.");
                    break;
            }

            // 쓰로틀 → LT/RT: 감지 범위 (축 %)
            if (SteamLayoutPlanner.IsDirectedAxisToTrigger(b))
            {
                var (low, high) = b.TriggerRange();
                if (low < 0 || high > 100 || low >= high)
                    r.Errors.Add($"{name}: 감지 범위는 0~100% 안에서 시작 < 끝이어야 합니다 (지금 {low}~{high}%).");
                // 쓰로틀은 가운데(50%)에서 반으로 나뉜다 (Steam은 한 축을 반쪽 +/− 로만 두 트리거에 나눠 쓴다)
                else if (b.Source.AxisSign < 0 && low >= 50)
                    r.Errors.Add($"{name}: − 방향(0%에서 최대) 범위는 50%보다 아래여야 합니다 (지금 {low}~{high}%).");
                else if (b.Source.AxisSign > 0 && high <= 50)
                    r.Errors.Add($"{name}: + 방향(100%에서 최대) 범위는 50%보다 위여야 합니다 (지금 {low}~{high}%).");
                else if (b.Source.AxisSign < 0 && high > 50)
                    r.Warnings.Add($"{name}: − 방향 범위는 50%까지만 적용됩니다 ({low}~{high}% → {low}~50%).");
                else if (b.Source.AxisSign > 0 && low < 50)
                    r.Warnings.Add($"{name}: + 방향 범위는 50%부터만 적용됩니다 ({low}~{high}% → 50~{high}%).");
            }
            // 축 → 버튼/트리거: 데드존(트리거 범위 시작)을 넘어야 입력됨
            else if (axisToButton || (b.Source.Kind == PhysicalInputKind.Axis && targetKind == XboxOutputKind.Trigger))
            {
                if (b.DeadZone is < 0 or > 95)
                    r.Errors.Add($"{name}: 데드존은 0~95% 범위여야 합니다.");
            }
            else if (b.DeadZone is < 0 or > 50 || b.OuterDeadZone is < 0 or > 50)
                r.Errors.Add($"{name}: 데드존은 0~50% 범위여야 합니다.");
            if (b.Source.Kind != PhysicalInputKind.Axis && b.Invert)
                r.Warnings.Add($"{name}: 반전은 축 입력에만 적용됩니다 (무시됨).");
        }

        // 출력 하나에 입력 여러 개는 버튼/트리거만 (둘 중 아무거나로 입력). 스틱 축은 축 하나만.
        foreach (var g in profile.Bindings.GroupBy(b => b.Target).Where(g => g.Count() > 1 && XboxOutputInfo.KindOf(g.Key) == XboxOutputKind.StickAxis))
            r.Errors.Add($"출력 충돌: {XboxOutputInfo.DisplayName(g.Key)}에 입력이 {g.Count()}개 연결되어 있습니다 (스틱 축은 입력 하나만).");
        foreach (var g in profile.Bindings.GroupBy(b => b.Target)
                     .Where(g => XboxOutputInfo.KindOf(g.Key) == XboxOutputKind.Trigger && g.Count(b => b.Source.Kind == PhysicalInputKind.Axis) > 1))
            r.Errors.Add($"출력 충돌: {XboxOutputInfo.DisplayName(g.Key)}에 아날로그 축이 여러 개입니다 (축은 하나, 버튼은 여러 개 가능).");

        // 축/입력 중복
        foreach (var g in profile.Bindings.GroupBy(b => b.Source).Where(g => g.Count() > 1))
            r.Warnings.Add($"입력 중복: {g.Key.DisplayName}이(가) {string.Join(", ", g.Select(b => XboxOutputInfo.DisplayName(b.Target)))}에 동시에 연결되어 있습니다.");

        // 같은 스틱의 X/Y 데드존이 다르면 큰 값 사용
        foreach (var stick in new[] { (XboxOutput.LeftStickX, XboxOutput.LeftStickY), (XboxOutput.RightStickX, XboxOutput.RightStickY) })
        {
            var x = profile.Bindings.FirstOrDefault(b => b.Target == stick.Item1);
            var y = profile.Bindings.FirstOrDefault(b => b.Target == stick.Item2);
            if (x != null && y != null && (x.DeadZone != y.DeadZone || x.OuterDeadZone != y.OuterDeadZone))
                r.Warnings.Add($"{XboxOutputInfo.DisplayName(stick.Item1)[..^2]}: Steam 데드존은 스틱 단위라 X/Y 중 큰 값이 사용됩니다.");
        }
        return r;
    }

    public static void ValidateControllerId(ValidationResult r, string controllerId)
    {
        if (!SteamInputConfigFinder.ControllerIdPattern.IsMatch(controllerId))
            r.Errors.Add($"Controller ID가 유효하지 않습니다: {controllerId}");
    }

    public static void ValidatePerGame(ValidationResult r, string label, string text) =>
        CheckVdf(r, label, text, root =>
        {
            var m = root.Get("controller_mappings");
            if (m == null) return "controller_mappings 없음";
            if (!m.GetAll("group").Any()) return "group 없음";
            if (!m.GetAll("preset").Any()) return "preset 없음";
            return null;
        });

    public static void ValidateConfigset(ValidationResult r, string text) =>
        CheckVdf(r, "configset", text, root => root.Get("controller_config") == null ? "controller_config 없음" : null);

    public static void ValidateGlobal(ValidationResult r, string text, SdlMapping? layout)
    {
        CheckVdf(r, "config.vdf", text, root => root.Children!.Count == 0 ? "비어 있음" : null);
        if (layout != null && SdlMapping.TryParse(layout.ToString()) == null)
            r.Errors.Add("생성된 장치 레이아웃(SDL 매핑) 형식이 올바르지 않습니다.");
    }

    public static ValidationResult ValidateGenerated(string controllerId, string perGameText, string configsetText, string? globalText, SdlMapping? layout)
    {
        var r = new ValidationResult();
        ValidateControllerId(r, controllerId);
        ValidatePerGame(r, "게임별 설정", perGameText);
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
            r.Errors.Add($"{label}: VDF 구문 오류 - {ex.Message}");
        }
    }

    private static bool IsHex4(string s) => s.Length is > 0 and <= 4 && s.All(Uri.IsHexDigit);
}
