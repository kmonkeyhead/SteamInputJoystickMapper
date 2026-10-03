using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Vdf;

using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.Steam;

/// <summary>게임 + 장치에 대한 Steam Input 설정 파일 위치.</summary>
public sealed class SteamInputTarget
{
    public required string ConfigDir { get; init; }
    /// <summary>Steam 컨트롤러 ID. 일반 컨트롤러로 등록된 장치는 "controller_generic".</summary>
    public required string ControllerId { get; init; }
    public required string AppId { get; init; }

    public string ConfigsetPath => Path.Combine(ConfigDir, $"configset_{ControllerId}.vdf");
    public string PerGamePath => Path.Combine(ConfigDir, AppId, $"{ControllerId}.vdf");
}

/// <summary>configset 파일에서 게임에 선택된 설정의 종류.</summary>
public sealed record ConfigSelection(string Kind, string? Value)
{
    public override string ToString() => Kind switch
    {
        "autosave" => T("사용자 설정(autosave)", "user config (autosave)"),
        "workshop" => T($"커뮤니티/클라우드 설정 (workshop {Value})", $"community/cloud config (workshop {Value})"),
        "template" => T($"템플릿 ({Value})", $"template ({Value})"),
        "none" => T("설정 없음", "no config"),
        _ => $"{Kind} {Value}",
    };
}

public static class SteamInputConfigFinder
{
    public static readonly Regex ControllerIdPattern = new(@"^([0-9a-f]+-[0-9a-f]+-[0-9a-z]+|controller_generic)$", RegexOptions.IgnoreCase);

    public static string ControllerConfigDir(string steamPath, uint accountId) =>
        Path.Combine(steamPath, "steamapps", "common", "Steam Controller Configs", accountId.ToString(), "config");

    public static string GlobalConfigPath(string steamPath) => Path.Combine(steamPath, "config", "config.vdf");

    public static string CloudControllerDir(string steamPath, uint accountId) =>
        Path.Combine(steamPath, "userdata", accountId.ToString(), "241100", "remote", "controller_config");

    public const string GenericControllerId = "controller_generic";

    /// <summary>
    /// 게임별 설정 파일의 컨트롤러 ID. 앱이 장치 설정(SDL 레이아웃)으로 일반 컨트롤러 등록을 하면 Steam은
    /// 이 장치의 게임별 설정을 controller_generic 이름으로 읽고 쓴다 (2026-10-03 사용자 PC에서 확인:
    /// Steam 설정 화면에서 바꾼 값이 3029750\controller_generic.vdf / configset_controller_generic.vdf에 저장됨).
    /// </summary>
    public static SteamInputTarget Resolve(string configDir, DeviceIdentity device, string appId) =>
        new() { ConfigDir = configDir, ControllerId = GenericControllerId, AppId = appId };

    public static ConfigSelection ReadSelection(SteamInputTarget target)
    {
        if (!File.Exists(target.ConfigsetPath)) return new ConfigSelection("none", null);
        try
        {
            var entry = VdfParser.ParseFile(target.ConfigsetPath).Get("controller_config")?.Get(target.AppId);
            var first = entry?.Children?.FirstOrDefault(c => !c.IsObject);
            return first == null ? new ConfigSelection("none", null) : new ConfigSelection(first.Key.ToLowerInvariant(), first.Value);
        }
        catch (Exception ex) when (ex is VdfParseException or IOException)
        {
            AppLog.Warn($"configset 분석 실패: {ex.Message}");
            return new ConfigSelection("error", ex.Message);
        }
    }

    /// <summary>해당 게임 폴더에 이미 있는 장치 설정 파일 (다른 컨트롤러 종류 포함).</summary>
    public static IReadOnlyList<string> ExistingGameConfigs(string configDir, string appId)
    {
        var dir = Path.Combine(configDir, appId);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.vdf") : Array.Empty<string>();
    }

    public static bool HasConfigFor(string configDir, DeviceIdentity? device, string appId)
    {
        if (device == null) return ExistingGameConfigs(configDir, appId).Count > 0;
        var target = Resolve(configDir, device, appId);
        return File.Exists(target.PerGamePath) || ReadSelection(target).Kind != "none";
    }

    public static bool HasCloudConfig(string steamPath, uint accountId, string appId) =>
        Directory.Exists(Path.Combine(CloudControllerDir(steamPath, accountId), appId));

    /// <summary>사양서 16장: 기존 Steam 컨트롤러 설정 분석.</summary>
    public static string Analyze(VdfNode root, string controllerId, string appId)
    {
        var sb = new StringBuilder();
        var m = root.Get("controller_mappings");
        if (m == null) return T("controller_mappings 노드가 없습니다 (알 수 없는 포맷).", "No controller_mappings node (unknown format).");
        sb.AppendLine($"Controller Type : {m.GetValue("controller_type") ?? "?"}");
        sb.AppendLine($"Controller ID   : {controllerId}");
        sb.AppendLine($"Game AppID      : {appId}");
        sb.AppendLine($"Version         : {m.GetValue("version") ?? "?"}  (progenitor: {m.GetValue("progenitor") ?? "-"})");

        var preset = m.GetAll("preset").FirstOrDefault();
        var sources = preset?.Get("group_source_bindings")?.Children ?? new List<VdfNode>();
        foreach (var group in m.GetAll("group"))
        {
            var id = group.GetValue("id");
            var src = sources.FirstOrDefault(s => s.Key == id)?.Value ?? T("(미사용)", "(unused)");
            sb.AppendLine($"Group {id,-3} {group.GetValue("mode"),-14} ← {src}");
            foreach (var input in group.Get("inputs")?.Children ?? new List<VdfNode>())
            {
                foreach (var activator in input.Get("activators")?.Children ?? new List<VdfNode>())
                {
                    var bindings = activator.Get("bindings")?.GetAll("binding").Select(b => b.Value) ?? Enumerable.Empty<string?>();
                    sb.AppendLine($"    {input.Key,-18} [{activator.Key}] {string.Join(" | ", bindings)}");
                }
            }
            var settings = group.Get("settings")?.Children;
            if (settings is { Count: > 0 })
                sb.AppendLine("    settings: " + string.Join(", ", settings.Where(s => !s.IsObject).Select(s => $"{s.Key}={s.Value}")));
        }
        return sb.ToString();
    }
}
