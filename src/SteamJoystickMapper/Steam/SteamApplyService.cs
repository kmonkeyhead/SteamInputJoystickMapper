using System.IO;
using System.Text;
using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Steam;

/// <summary>게임 하나에 대한 적용 내용.</summary>
public sealed class GameApplyItem
{
    public required string AppId { get; init; }
    public required string GameName { get; init; }
    public required SteamInputTarget Target { get; init; }
    public ConfigSelection PreviousSelection { get; init; } = new("none", null);
    public bool CloudConfig { get; init; }
}

/// <summary>프로필 전체를 Steam에 동기화하기 위한 계획. 사용자 확인 후 Execute로 실제 파일에 쓴다.</summary>
public sealed class ApplyPlan
{
    public required MapperProfile Profile { get; init; }
    public required ValidationResult Validation { get; init; }
    public string? ControllerId { get; init; }
    public SdlMapping? Layout { get; init; }
    public bool LayoutChanged { get; init; }
    public List<GameApplyItem> Games { get; } = new();
    /// <summary>게임 매핑이 삭제되어 앱이 만든 파일을 정리할 게임.</summary>
    public List<(string AppId, string Path)> Released { get; } = new();
    /// <summary>Steam Input을 '사용'으로 바꿀 게임 (게임 매핑이 있는데 사용이 아닌 게임).</summary>
    public List<(string GameName, SteamInputMode Previous)> SteamInputEnabled { get; } = new();

    /// <summary>경로 → 새 내용 (현재 내용과 다른 것만).</summary>
    public Dictionary<string, string> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Notes { get; } = new();

    public bool HasChanges => Writes.Count > 0 || Released.Count > 0;
    public bool CanApply => Validation.IsValid && HasChanges;
}

public sealed class SteamApplyService(SteamEnvironment env, BackupManager backups, Func<bool>? isSteamRunning = null)
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    private readonly Func<bool> _isSteamRunning = isSteamRunning ?? SteamProcess.IsRunning;

    private string? LocalConfigPath => env.LocalConfigPath;

    public ApplyPlan Prepare(MapperProfile profile)
    {
        var validation = SteamConfigValidator.ValidateMapper(profile);
        var configDir = env.ControllerConfigDir;
        if (configDir == null) validation.Errors.Add("Steam 사용자를 찾을 수 없어 설정 폴더를 결정할 수 없습니다.");
        if (!validation.IsValid || configDir == null) return new ApplyPlan { Profile = profile, Validation = validation };

        var device = profile.Device;
        var controllerTarget = SteamInputConfigFinder.Resolve(configDir, device, "0");

        // ---- (1) 장치 레이아웃 (게임 매핑에서 자동 생성) ----
        VdfNode? globalRoot = null;
        SdlMapping? existingLayout = null;
        if (File.Exists(env.GlobalConfigPath))
        {
            try
            {
                globalRoot = VdfParser.ParseFile(env.GlobalConfigPath);
                existingLayout = SteamConfigGenerator.ReadDeviceLayout(globalRoot, device);
            }
            catch (VdfParseException ex)
            {
                validation.Errors.Add($"config.vdf 분석 실패 - 변경하지 않습니다: {ex.Message}");
            }
        }
        var layout = SteamLayoutPlanner.BuildLayout(profile, existingLayout?.Guid);
        var layoutChanged = existingLayout == null || !SteamLayoutPlanner.SameLayout(existingLayout, layout);
        if (layoutChanged && globalRoot == null && validation.IsValid)
            validation.Errors.Add("장치 레이아웃을 저장할 config.vdf를 찾을 수 없습니다. Steam을 한 번 실행한 뒤 다시 시도하세요.");

        var plan = new ApplyPlan
        {
            Profile = profile, Validation = validation, ControllerId = controllerTarget.ControllerId,
            Layout = layout, LayoutChanged = layoutChanged,
        };
        if (layoutChanged && globalRoot != null)
        {
            plan.Writes[env.GlobalConfigPath] = VdfWriter.Write(SteamConfigGenerator.UpdateGlobalConfig(globalRoot, layout, device));
            plan.Notes.Add(existingLayout == null
                ? "장치 설정: 이 조이스틱을 Steam 일반 컨트롤러로 등록합니다 (게임 매핑에 쓰인 입력으로 자동 생성)."
                : "장치 설정: 게임 매핑에 쓰인 입력에 맞게 갱신합니다.");
        }

        // ---- (2) 게임별 ----
        VdfNode? configsetRoot = null;
        if (File.Exists(controllerTarget.ConfigsetPath))
        {
            try { configsetRoot = VdfParser.ParseFile(controllerTarget.ConfigsetPath); }
            catch (VdfParseException ex) { validation.Errors.Add($"configset 분석 실패 - 변경하지 않습니다: {ex.Message}"); }
        }
        var configset = configsetRoot?.DeepClone() ?? VdfNode.CreateObject("");
        configset.GetOrAddObject("controller_config");

        foreach (var game in profile.Games)
        {
            var view = profile.GameView(game);
            var target = SteamInputConfigFinder.Resolve(configDir, device, game.AppId);
            var reasons = new List<string>();
            var perGamePlan = SteamLayoutPlanner.TryPlan(layout, view, reasons);
            if (perGamePlan == null)
            {
                validation.Errors.AddRange(reasons.Select(r => $"[{game.GameName}] {r}"));
                continue;
            }

            var genLog = new List<string>();
            var baseDoc = SteamConfigGenerator.LoadBase(target.PerGamePath, env.SteamPath);
            var text = VdfWriter.Write(SteamConfigGenerator.Generate(baseDoc.Root, view, perGamePlan, genLog));
            AddWriteIfChanged(plan, target.PerGamePath, text);
            configset = SteamConfigGenerator.UpdateConfigset(configset, game.AppId);

            var selection = SteamInputConfigFinder.ReadSelection(target);
            plan.Games.Add(new GameApplyItem
            {
                AppId = game.AppId, GameName = game.GameName, Target = target, PreviousSelection = selection,
                CloudConfig = env.User != null && SteamInputConfigFinder.HasCloudConfig(env.SteamPath, env.User.AccountId, game.AppId),
            });
            if (!File.Exists(target.PerGamePath) || !IsManagedFile(target.PerGamePath))
                plan.Notes.Add($"[{game.GameName}] 기준 문서: {baseDoc.Origin}");
            if (selection.Kind == "workshop")
                plan.Notes.Add($"[{game.GameName}] 현재 {selection}을 사용 중 → 이 앱의 게임 매핑으로 바뀝니다.");
            plan.Notes.AddRange(genLog.Select(l => $"[{game.GameName}] {l}"));
        }

        // ---- (3) 게임 매핑이 삭제된 게임: 앱이 만든 파일만 정리 ----
        foreach (var (appId, path) in FindManagedGames(configDir, controllerTarget.ControllerId))
        {
            if (profile.FindGame(appId) != null) continue;
            // configset이 가리키지 않으면 이미 정리된 것이다. Steam이 재시작하며 클라우드에서
            // 같은 파일을 되살리는 경우가 있어, 파일만 보고 판단하면 매번 '정리할 변경'이 생긴다.
            if (configsetRoot?.Find("controller_config", appId) == null) continue;
            plan.Released.Add((appId, path));
            SteamConfigGenerator.RemoveFromConfigset(configset, appId);
            plan.Notes.Add($"[{appId}] 게임 매핑이 없어 앱이 만든 설정을 정리합니다 (파일은 백업 폴더로 이동)");
        }

        if (plan.Games.Count > 0 || plan.Released.Count > 0)
        {
            AddWriteIfChanged(plan, controllerTarget.ConfigsetPath, VdfWriter.Write(configset));
        }

        // ---- (4) 게임 매핑이 있는 게임: 게임 속성의 Steam Input을 '사용'으로 ----
        // 게임 매핑을 지운 게임은 되돌리지 않는다.
        var localConfig = LocalConfigPath;
        if (localConfig != null && plan.Games.Count > 0)
        {
            try
            {
                var text = File.Exists(localConfig) ? File.ReadAllText(localConfig, Utf8NoBom) : null;
                var root = text == null ? null : VdfParser.Parse(text);
                var toEnable = plan.Games.Where(g => root == null || SteamInputSetting.Read(root, g.AppId) != SteamInputMode.On).ToList();
                if (toEnable.Count > 0 && text == null)
                {
                    validation.Errors.Add("Steam 사용자 설정(localconfig.vdf)이 없어 Steam Input을 켤 수 없습니다. Steam에 한 번 로그인한 뒤 다시 시도하세요.");
                }
                else if (toEnable.Count > 0)
                {
                    plan.Writes[localConfig] = SteamInputSetting.EnableFor(text!, toEnable.Select(g => g.AppId));
                    foreach (var g in toEnable)
                    {
                        var previous = SteamInputSetting.Read(root!, g.AppId);
                        plan.SteamInputEnabled.Add((g.GameName, previous));
                        plan.Notes.Add($"[{g.GameName}] 게임 속성 Steam Input: {SteamInputSetting.DisplayName(previous)} → 사용");
                    }
                }
            }
            catch (Exception ex) when (ex is VdfParseException or IOException or InvalidDataException)
            {
                validation.Errors.Add($"localconfig.vdf 분석 실패 - Steam Input을 바꾸지 않습니다: {ex.Message}");
            }
        }

        SteamConfigValidator.ValidateControllerId(validation, controllerTarget.ControllerId);
        foreach (var (path, text) in plan.Writes)
        {
            if (string.Equals(path, env.GlobalConfigPath, StringComparison.OrdinalIgnoreCase))
                SteamConfigValidator.ValidateGlobal(validation, text, layout);
            else if (string.Equals(path, controllerTarget.ConfigsetPath, StringComparison.OrdinalIgnoreCase))
                SteamConfigValidator.ValidateConfigset(validation, text);
            else if (string.Equals(path, localConfig, StringComparison.OrdinalIgnoreCase))
                continue; // SteamInputSetting.EnableFor에서 의도한 값만 바뀌었는지 검증함
            else
                SteamConfigValidator.ValidatePerGame(validation, Path.GetFileName(Path.GetDirectoryName(path)!) + " 게임별 설정", text);
        }
        if (existingLayout == null)
            plan.Notes.Add("팁: Steam 설정 > 컨트롤러에서 이 장치의 레이아웃을 한 번 정의해 두면 Steam이 쓰는 장치 GUID를 그대로 재사용합니다.");
        return plan;
    }

    /// <summary>
    /// Steam에서 비활성화: config.vdf에서 이 장치의 레이아웃을 지워 Steam이 일반 컨트롤러가 아닌 조이스틱으로 보게 한다.
    /// 게임별 설정 파일은 그대로 둔다 (레이아웃이 없으면 쓰이지 않으며, 다시 적용하면 그대로 이어서 쓴다).
    /// </summary>
    public ApplyPlan PrepareDisable(MapperProfile profile)
    {
        var validation = new ValidationResult();
        if (profile.Device.IsEmpty) validation.Errors.Add("프로필에 입력 장치가 지정되지 않았습니다.");
        var plan = new ApplyPlan { Profile = profile, Validation = validation };
        if (!validation.IsValid || !File.Exists(env.GlobalConfigPath)) return plan;
        try
        {
            var root = VdfParser.ParseFile(env.GlobalConfigPath);
            if (SteamConfigGenerator.ReadDeviceLayout(root, profile.Device) == null) return plan; // 이미 비활성
            var text = VdfWriter.Write(SteamConfigGenerator.RemoveDeviceLayout(root, profile.Device));
            plan.Writes[env.GlobalConfigPath] = text;
            SteamConfigValidator.ValidateGlobal(validation, text, null);
            plan.Notes.Add("장치 설정: 이 조이스틱의 Steam 일반 컨트롤러 등록을 지웁니다 (Steam에서는 조이스틱으로 보임).");
        }
        catch (VdfParseException ex)
        {
            validation.Errors.Add($"config.vdf 분석 실패 - 변경하지 않습니다: {ex.Message}");
        }
        return plan;
    }

    /// <summary>이 장치가 Steam에 일반 컨트롤러로 등록되어 있는지 (config.vdf에 레이아웃이 있는지).</summary>
    public bool IsDeviceRegistered(DeviceIdentity device)
    {
        try
        {
            return !device.IsEmpty && File.Exists(env.GlobalConfigPath) &&
                   SteamConfigGenerator.ReadDeviceLayout(VdfParser.ParseFile(env.GlobalConfigPath), device) != null;
        }
        catch (Exception ex) when (ex is VdfParseException or IOException)
        {
            return false;
        }
    }

    private static void AddWriteIfChanged(ApplyPlan plan, string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path, Utf8NoBom) == text) return;
        plan.Writes[path] = text;
    }

    private static bool IsManagedFile(string path)
    {
        try { return SteamConfigGenerator.IsManaged(VdfParser.ParseFile(path)); }
        catch (Exception ex) when (ex is VdfParseException or IOException) { return false; }
    }

    /// <summary>이 앱이 만든 게임별 파일들 (description 표시로 식별).</summary>
    public static IEnumerable<(string AppId, string Path)> FindManagedGames(string configDir, string controllerId)
    {
        if (!Directory.Exists(configDir)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(configDir))
        {
            var appId = Path.GetFileName(dir);
            var file = Path.Combine(dir, $"{controllerId}.vdf");
            if (SteamGameScanner.IsValidAppId(appId) && File.Exists(file) && IsManagedFile(file))
                yield return (appId, file);
        }
    }

    /// <summary>검증 → 백업 → 쓰기 → 재검증. 실패 시 백업으로 자동 복원.</summary>
    public BackupManifest Execute(ApplyPlan plan)
    {
        if (!plan.CanApply) throw new InvalidOperationException("검증을 통과하지 못한 설정은 적용할 수 없습니다.");
        if (_isSteamRunning()) throw new InvalidOperationException("Steam이 실행 중입니다. Steam을 종료한 후 적용하세요.");
        var touched = plan.Writes.Keys.Concat(plan.Released.Select(r => r.Path)).ToList();
        foreach (var path in touched) EnsureSafePath(path);

        var backup = backups.Create(new BackupManifest
        {
            AppId = string.Join(",", plan.Games.Select(g => g.AppId).Concat(plan.Released.Select(r => r.AppId))),
            GameName = plan.Games.Count == 0 ? "장치 설정" : string.Join(", ", plan.Games.Select(g => g.GameName)),
            DeviceName = plan.Profile.Device.Name,
            ControllerId = plan.ControllerId ?? "",
            ProfileName = plan.Profile.ProfileName,
        }, touched);

        try
        {
            AppLog.Info("Config generated");
            foreach (var (path, text) in plan.Writes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".sjm.tmp";
                File.WriteAllText(tmp, text, Utf8NoBom);
                File.Move(tmp, path, overwrite: true);
                var written = File.ReadAllText(path, Utf8NoBom);
                if (written != text) throw new IOException($"쓰기 검증 실패: {Path.GetFileName(path)}");
                VdfParser.Parse(written);
            }
            // 삭제하지 않고 백업 폴더로 이동 (원본 파일 직접 삭제 금지)
            var releasedDir = Path.Combine(backup.Folder, "released");
            foreach (var (appId, path) in plan.Released)
            {
                Directory.CreateDirectory(releasedDir);
                File.Move(path, Path.Combine(releasedDir, $"{appId}_{Path.GetFileName(path)}"), overwrite: true);
            }
            AppLog.Info("Validation OK");
            AppLog.Info($"Config applied (장치 설정{(plan.LayoutChanged ? " 변경" : " 유지")}, 게임 {plan.Games.Count}개, 정리 {plan.Released.Count}개)");
            return backup;
        }
        catch (Exception ex)
        {
            AppLog.Error($"적용 실패, 원본 복원 중: {ex.Message}");
            try
            {
                backups.Restore(backup);
                AppLog.Info("원본 설정 복원 완료");
            }
            catch (Exception restoreEx)
            {
                AppLog.Error($"자동 복원 실패! 백업 폴더에서 수동 복원이 필요합니다: {backup.Folder} ({restoreEx.Message})");
            }
            throw;
        }
    }

    /// <summary>사양서 27장: Steam 사용자 설정 외의 파일(Steam 실행 파일, 게임 파일 등)은 절대 쓰지 않는다.</summary>
    private void EnsureSafePath(string path)
    {
        var full = Path.GetFullPath(path);
        var configDir = env.ControllerConfigDir;
        var isControllerConfig = configDir != null &&
            full.StartsWith(Path.GetFullPath(configDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var isGlobalConfig = string.Equals(full, Path.GetFullPath(env.GlobalConfigPath), StringComparison.OrdinalIgnoreCase);
        var isLocalConfig = LocalConfigPath != null && string.Equals(full, Path.GetFullPath(LocalConfigPath), StringComparison.OrdinalIgnoreCase);
        if (!isControllerConfig && !isGlobalConfig && !isLocalConfig)
            throw new InvalidOperationException($"허용되지 않은 경로에 쓰기 시도: {full}");
        if (!full.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"VDF가 아닌 파일 쓰기 시도: {full}");
    }
}
