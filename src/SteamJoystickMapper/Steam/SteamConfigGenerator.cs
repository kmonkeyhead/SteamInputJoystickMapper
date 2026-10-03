using System.IO;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Steam;

/// <summary>
/// 내부 프로필 → Steam VDF. 가능하면 Steam이 만든 기존 설정(또는 Steam 설치 폴더의 템플릿)을
/// 파싱해 필요한 바인딩만 수정한다. 새 VDF 구조를 임의로 만들지 않는다.
/// </summary>
public static class SteamConfigGenerator
{
    public const string TemplateFileName = "controller_generic_gamepad_joystick.vdf";

    /// <summary>앱이 만든 게임별 파일 표시 (description 앞부분).</summary>
    public const string ManagedMarker = "Monkeyhead Mapper";
    private static readonly string[] ManagedMarkers = { ManagedMarker, "Steam Input Joystick Mapper" };

    /// <summary>이 앱이 만든 게임별 설정 파일인지.</summary>
    public static bool IsManaged(VdfNode root)
    {
        var description = root.Get("controller_mappings")?.GetValue("description") ?? "";
        return ManagedMarkers.Any(m => description.StartsWith(m, StringComparison.Ordinal));
    }

    /// <summary>관리 대상 Steam 소스 → 그룹 모드.</summary>
    private static readonly (string Source, string Mode)[] ManagedSources =
    {
        ("button_diamond", "four_buttons"),
        ("dpad", "dpad"),
        ("switch", "switches"),
        ("joystick", "joystick_move"),
        ("right_joystick", "joystick_move"),
        ("left_trigger", "trigger"),
        ("right_trigger", "trigger"),
    };

    public sealed record BaseDocument(VdfNode Root, string Origin);

    /// <summary>기준 문서 선택: 기존 게임별 설정 → Steam 템플릿 → 최소 내장 템플릿.</summary>
    public static BaseDocument LoadBase(string? existingPerGamePath, string? steamPath)
    {
        if (existingPerGamePath != null && File.Exists(existingPerGamePath))
        {
            try
            {
                var root = VdfParser.ParseFile(existingPerGamePath);
                if (root.Get("controller_mappings") != null) return new BaseDocument(root, "기존 Steam 설정");
            }
            catch (VdfParseException) { /* 템플릿으로 폴백 */ }
        }
        if (steamPath != null)
        {
            var template = Path.Combine(steamPath, "controller_base", "templates", TemplateFileName);
            if (File.Exists(template))
            {
                try
                {
                    var root = VdfParser.ParseFile(template);
                    if (root.Get("controller_mappings") != null) return new BaseDocument(root, "Steam 템플릿");
                }
                catch (VdfParseException) { }
            }
        }
        return new BaseDocument(MinimalTemplate(), "내장 최소 템플릿");
    }

    public static VdfNode Generate(VdfNode baseRoot, MappingProfile profile, PerGamePlan plan, List<string> log)
    {
        var root = baseRoot.DeepClone();
        var m = root.Get("controller_mappings") ?? throw new InvalidOperationException("controller_mappings 노드가 없습니다.");

        m.SetValue("title", profile.ProfileName);
        m.SetValue("description", $"{ManagedMarker} - {profile.GameName} - {profile.Device.Name}");
        m.Remove("localization"); // 템플릿의 번역된 제목("게임패드")이 우리 제목을 덮지 않도록
        if (m.Get("controller_type") == null) m.SetValue("controller_type", "controller_generic");

        var preset = m.GetAll("preset").FirstOrDefault();
        if (preset == null)
        {
            preset = VdfNode.CreateObject("preset");
            preset.SetValue("id", "0");
            preset.SetValue("name", "Default");
            m.Add(preset);
        }
        var gsb = preset.GetOrAddObject("group_source_bindings");

        foreach (var (source, defaultMode) in ManagedSources)
        {
            var mode = defaultMode == "joystick_move" ? plan.StickMode(source) : defaultMode;
            var group = EnsureGroup(m, gsb, source, mode, log);
            var used = plan.IsSourceUsed(source);
            SetSourceActive(gsb, group.GetValue("id")!, source, used);

            RewriteInputs(group, plan.ButtonBindings.Where(kv => kv.Key.Source == source)
                .ToDictionary(kv => kv.Key.Input, kv => kv.Value));

            if (mode == "joystick_move") ApplyStickSettings(group, source, plan);
            if (mode == "dpad" && source != "dpad") ApplyStickDpadSettings(group, source, plan);
            if (mode == "trigger")
            {
                var settings = group.GetOrAddObject("settings");
                settings.SetValue("output_trigger", plan.TriggerOutputs.GetValueOrDefault(source).ToString());
                // 트리거 범위 시작 = deadzone_inner_radius, 끝 = deadzone_outer_radius (0~32767).
                // 2026-10-03 사용자가 Steam에서 바꾼 값이 저장된 파일에서 확인.
                var hasRange = plan.TriggerRanges.TryGetValue(source, out var range);
                if (hasRange && range.Start > 0) settings.SetValue("deadzone_inner_radius", range.Start.ToString());
                else settings.Remove("deadzone_inner_radius");
                if (hasRange && range.End is { } end) settings.SetValue("deadzone_outer_radius", end.ToString());
                else settings.Remove("deadzone_outer_radius");
            }
        }
        return root;
    }

    private static VdfNode EnsureGroup(VdfNode m, VdfNode gsb, string source, string mode, List<string> log)
    {
        // 같은 소스에 대한 모드시프트 레이어는 결정적 결과를 위해 제거
        foreach (var entry in gsb.Children!.ToList())
        {
            var tokens = (entry.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0 && tokens[0] == source && tokens.Contains("modeshift"))
            {
                gsb.Remove(entry);
                log.Add($"'{source}'의 모드 시프트 레이어 제거");
            }
        }

        var existing = gsb.Children!.FirstOrDefault(e => (e.Value ?? "").Split(' ')[0] == source);
        if (existing != null)
        {
            var group = m.GetAll("group").FirstOrDefault(g => g.GetValue("id") == existing.Key);
            if (group != null && group.GetValue("mode") == mode) return group;
            gsb.Remove(existing);
            // 다른 곳에서 참조하지 않는 이전 그룹은 지워 모드를 오갈 때 쌓이지 않게 한다
            if (group != null && gsb.Children!.All(e => e.Key != existing.Key)) m.Remove(group);
            log.Add($"'{source}' 그룹 모드가 '{group?.GetValue("mode") ?? "없음"}'이라 '{mode}' 그룹으로 교체");
        }

        var nextId = m.GetAll("group").Select(g => int.TryParse(g.GetValue("id"), out var i) ? i : -1).DefaultIfEmpty(-1).Max() + 1;
        var created = VdfNode.CreateObject("group");
        created.SetValue("id", nextId.ToString());
        created.SetValue("mode", mode);
        created.SetValue("description", "");
        created.GetOrAddObject("inputs");
        var insertAt = m.Children!.FindIndex(c => c.Key == "preset");
        if (insertAt < 0) m.Add(created); else m.Children.Insert(insertAt, created);
        gsb.Add(VdfNode.CreateValue(nextId.ToString(), $"{source} active"));
        return created;
    }

    private static void SetSourceActive(VdfNode gsb, string groupId, string source, bool active)
    {
        var state = active ? "active" : "inactive";
        gsb.SetValue(groupId, $"{source} {state}");
    }

    /// <summary>그룹의 입력을 계획된 바인딩으로 교체한다. 기존 입력의 활성자 설정(햅틱 등)은 유지.</summary>
    private static void RewriteInputs(VdfNode group, Dictionary<string, string> bindings)
    {
        var inputs = group.GetOrAddObject("inputs");
        var old = inputs.Children!.ToList();
        inputs.ClearChildren();
        // 기존 입력 순서를 유지해 Steam 원본과의 차이를 최소화
        int OrderOf(string name)
        {
            var i = old.FindIndex(o => string.Equals(o.Key, name, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        foreach (var (inputName, xinput) in bindings.OrderBy(b => OrderOf(b.Key)).ThenBy(b => b.Key, StringComparer.Ordinal))
        {
            var prev = old.FirstOrDefault(i => string.Equals(i.Key, inputName, StringComparison.OrdinalIgnoreCase));
            var input = VdfNode.CreateObject(inputName);
            var activators = input.GetOrAddObject("activators");
            var fullPress = activators.GetOrAddObject("Full_Press");
            fullPress.GetOrAddObject("bindings").SetValue("binding", $"xinput_button {xinput}");
            var prevSettings = prev?.Get("activators")?.Get("Full_Press")?.Get("settings");
            if (prevSettings != null) fullPress.Add(prevSettings.DeepClone());
            input.GetOrAddObject("disabled_activators");
            inputs.Add(input);
        }
    }

    private static void ApplyStickSettings(VdfNode group, string source, PerGamePlan plan)
    {
        var settings = group.GetOrAddObject("settings");
        var natural = source == "joystick" ? StickSide.Left : StickSide.Right;
        if (plan.StickOutputs.TryGetValue(source, out var side) && side != natural)
            settings.SetValue("output_joystick", "1"); // 확인된 값: 1 = 오른쪽 스틱 (왼쪽 소스에서만 사용)
        else
            settings.Remove("output_joystick");

        var (inner, outer) = plan.StickDeadZones.GetValueOrDefault(source);
        if (inner > 0) settings.SetValue("deadzone_inner_radius", SteamLayoutPlanner.InnerDeadZoneUnits(inner).ToString());
        else settings.Remove("deadzone_inner_radius");
        if (outer > 0) settings.SetValue("deadzone_outer_radius", SteamLayoutPlanner.OuterDeadZoneUnits(outer).ToString());
        else settings.Remove("deadzone_outer_radius");

        if (settings.Children!.Count == 0) group.Remove(settings);
    }

    /// <summary>
    /// 스틱을 방향 패드 모드로 쓸 때의 설정. 키는 Steam 기본 설정(controller_base)의 조이스틱 방향 패드 그룹에서 확인:
    /// "deadzone" (0~32767, 이 값을 넘어야 방향 입력), "requires_click" "0".
    /// </summary>
    private static void ApplyStickDpadSettings(VdfNode group, string source, PerGamePlan plan)
    {
        var settings = group.GetOrAddObject("settings");
        settings.SetValue("requires_click", "0");
        var dz = plan.DpadStickDeadZones.GetValueOrDefault(source);
        if (dz > 0) settings.SetValue("deadzone", SteamLayoutPlanner.InnerDeadZoneUnits(dz).ToString());
        else settings.Remove("deadzone");
    }

    /// <summary>configset 파일에서 해당 게임이 사용자 설정(autosave)을 쓰도록 지정.</summary>
    public static VdfNode UpdateConfigset(VdfNode? existing, string appId)
    {
        var root = existing?.DeepClone() ?? VdfNode.CreateObject("");
        var cc = root.GetOrAddObject("controller_config");
        var entry = cc.GetOrAddObject(appId);
        entry.ClearChildren();
        entry.SetValue("autosave", "1");
        return root;
    }

    /// <summary>configset에서 게임 항목을 제거한다 (Steam 기본 템플릿으로 돌아감).</summary>
    public static void RemoveFromConfigset(VdfNode configsetRoot, string appId)
    {
        var cc = configsetRoot.Get("controller_config");
        cc?.Remove(appId);
    }

    /// <summary>config.vdf 의 SDL_GamepadBind 에서 장치 레이아웃을 교체/추가.</summary>
    public static VdfNode UpdateGlobalConfig(VdfNode configRoot, SdlMapping layout, DeviceIdentity device)
    {
        var root = configRoot.DeepClone();
        var store = root.Get("InstallConfigStore") ?? root.Children!.FirstOrDefault(c => c.IsObject)
            ?? throw new InvalidOperationException("config.vdf 루트 노드를 찾을 수 없습니다.");
        var db = SdlMappingDatabase.Parse(store.GetValue("SDL_GamepadBind"));
        db.Upsert(layout, device.VidValue, device.PidValue);
        store.SetValue("SDL_GamepadBind", db.ToString());
        return root;
    }

    /// <summary>config.vdf 의 SDL_GamepadBind 에서 이 장치의 레이아웃을 지운다 (Steam이 일반 컨트롤러가 아닌 조이스틱으로 봄).</summary>
    public static VdfNode RemoveDeviceLayout(VdfNode configRoot, DeviceIdentity device)
    {
        var root = configRoot.DeepClone();
        var store = root.Get("InstallConfigStore") ?? root.Children!.FirstOrDefault(c => c.IsObject)
            ?? throw new InvalidOperationException("config.vdf 루트 노드를 찾을 수 없습니다.");
        var db = SdlMappingDatabase.Parse(store.GetValue("SDL_GamepadBind"));
        if (db.Remove(device.VidValue, device.PidValue)) store.SetValue("SDL_GamepadBind", db.ToString());
        return root;
    }

    public static SdlMapping? ReadDeviceLayout(VdfNode configRoot, DeviceIdentity device)
    {
        var store = configRoot.Get("InstallConfigStore") ?? configRoot.Children!.FirstOrDefault(c => c.IsObject);
        return SdlMappingDatabase.Parse(store?.GetValue("SDL_GamepadBind")).Find(device.VidValue, device.PidValue);
    }

    /// <summary>Steam 템플릿을 찾지 못했을 때 쓰는 최소 구조 (Steam 생성 파일과 같은 키만 사용).</summary>
    public static VdfNode MinimalTemplate()
    {
        var root = VdfNode.CreateObject("");
        var m = root.GetOrAddObject("controller_mappings");
        m.SetValue("version", "3");
        m.SetValue("revision", "1");
        m.SetValue("title", "Gamepad");
        m.SetValue("description", "");
        m.SetValue("creator", "");
        m.SetValue("progenitor", $"template://{TemplateFileName}");
        m.SetValue("url", "");
        m.SetValue("export_type", "unknown");
        m.SetValue("controller_type", "controller_generic");
        m.SetValue("major_revision", "0");
        m.SetValue("minor_revision", "0");
        m.SetValue("Timestamp", "0");
        var preset = VdfNode.CreateObject("preset");
        preset.SetValue("id", "0");
        preset.SetValue("name", "Default");
        preset.GetOrAddObject("group_source_bindings");
        m.Add(preset);
        var settings = m.GetOrAddObject("settings");
        settings.SetValue("left_trackpad_mode", "0");
        settings.SetValue("right_trackpad_mode", "0");
        return root;
    }
}
