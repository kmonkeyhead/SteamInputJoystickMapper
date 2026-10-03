using System.Globalization;
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
    /// 일반 컨트롤러 등록 후 기본 출력 대상. 기존 장치별 선택 정보는 FindDeviceTargets로 찾아 함께 적용한다.
    /// </summary>
    public static SteamInputTarget Resolve(string configDir, DeviceIdentity device, string appId) =>
        new() { ConfigDir = configDir, ControllerId = GenericControllerId, AppId = appId };

    /// <summary>
    /// 이 제품의 기존 장치별 설정 대상. configset 또는 게임 파일에 실제로 존재하는 VID-PID-접미사 ID만 쓴다.
    /// VID/PID는 숫자로 비교해 앞의 0과 대소문자가 달라도 같은 제품으로 찾고, 다른 제품은 제외한다.
    /// </summary>
    public static IReadOnlyList<SteamInputTarget> FindDeviceTargets(string configDir, DeviceIdentity device, string appId)
    {
        if (!ushort.TryParse(device.Vid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid) ||
            !ushort.TryParse(device.Pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
            return Array.Empty<SteamInputTarget>();

        var configsetIds = Directory.Exists(configDir)
            ? Directory.EnumerateFiles(configDir, "configset_*.vdf")
                .Select(f => Path.GetFileNameWithoutExtension(f)["configset_".Length..])
            : Enumerable.Empty<string>();
        var gameIds = ExistingGameConfigs(configDir, appId).Select(Path.GetFileNameWithoutExtension);
        bool MatchesDevice(string? id)
        {
            if (id == null || !ControllerIdPattern.IsMatch(id)) return false;
            var parts = id.Split('-');
            return parts.Length == 3 &&
                   ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v == vid &&
                   ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p) && p == pid;
        }

        return configsetIds.Concat(gameIds).Where(MatchesDevice)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => new SteamInputTarget { ConfigDir = configDir, ControllerId = id!, AppId = appId }).ToList();
    }

    public static ConfigSelection ReadSelection(SteamInputTarget target)
    {
        if (!File.Exists(target.ConfigsetPath)) return new ConfigSelection("none", null);
        try
        {
            return ReadSelection(VdfParser.ParseFile(target.ConfigsetPath), target.AppId);
        }
        catch (Exception ex) when (ex is VdfParseException or IOException)
        {
            AppLog.Warn($"configset 분석 실패: {ex.Message}");
            return new ConfigSelection("error", ex.Message);
        }
    }

    public static ConfigSelection ReadSelection(VdfNode root, string appId)
    {
        var entry = root.Get("controller_config")?.Get(appId);
        var first = entry?.Children?.FirstOrDefault(c => !c.IsObject);
        return first == null ? new ConfigSelection("none", null) : new ConfigSelection(first.Key.ToLowerInvariant(), first.Value);
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
