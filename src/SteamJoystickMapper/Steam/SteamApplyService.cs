using System.IO;
using System.Text;
using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;
using static SteamJoystickMapper.Localization.Loc;

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

    /// <summary>
    /// 쓰로틀(방향 있는 축 → LT/RT)은 장치 설정을 공유하므로 한 게임 것만 쓴다: throttleAppId 게임의 쓰로틀 매핑만 남기고
    /// 나머지 게임의 쓰로틀 매핑은 뺀다. null이면 모든 쓰로틀 매핑을 뺀다 (쓰로틀 사용 안 함). 쓰로틀만 있던 게임은 빠진다.
    /// </summary>
    public static MapperProfile WithThrottleFrom(MapperProfile profile, string? throttleAppId) => new()
    {
        FormatVersion = profile.FormatVersion,
        ProfileName = profile.ProfileName,
        Device = profile.Device,
        ModifiedAt = profile.ModifiedAt,
        Games = profile.Games
            .Select(g => g.AppId == throttleAppId ? g : new GameMapping
            {
                AppId = g.AppId, GameName = g.GameName,
                Bindings = g.Bindings.Where(b => !SteamLayoutPlanner.IsDirectedAxisToTrigger(b)).ToList(),
            })
            .Where(g => g.Bindings.Count > 0)
            .ToList(),
    };

    /// <summary>쓰로틀 매핑이 있는 게임들 (적용 때 고를 목록).</summary>
    public static List<GameMapping> ThrottleGames(MapperProfile profile) =>
        profile.Games.Where(g => SteamLayoutPlanner.ThrottleBindings(g).Any()).ToList();

    /// <param name="throttleAppId">쓰로틀을 쓸 게임 (null = 쓰로틀 사용 안 함). 다른 게임의 쓰로틀 매핑은 적용하지 않는다.</param>
    public ApplyPlan Prepare(MapperProfile original, string? throttleAppId = null)
    {
        var profile = WithThrottleFrom(original, throttleAppId);
        var validation = SteamConfigValidator.ValidateMapper(profile);
        validation.Warnings.RemoveAll(w => w.Contains(SteamConfigValidator.ThrottleWarningMarker)); // 적용 단계에서는 이미 골랐음
        var configDir = env.ControllerConfigDir;
        if (configDir == null) validation.Errors.Add(T("Steam 사용자를 찾을 수 없어 설정 폴더를 결정할 수 없습니다.", "No Steam user found, so the config folder cannot be determined."));
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
                validation.Errors.Add(T("config.vdf 분석 실패 - 변경하지 않습니다: ", "Failed to parse config.vdf - leaving it unchanged: ") + ex.Message);
            }
        }
        var layout = SteamLayoutPlanner.BuildLayout(profile, existingLayout?.Guid);
        var layoutChanged = existingLayout == null || !SteamLayoutPlanner.SameLayout(existingLayout, layout);
        if (layoutChanged && globalRoot == null && validation.IsValid)
            validation.Errors.Add(T("장치 레이아웃을 저장할 config.vdf를 찾을 수 없습니다. Steam을 한 번 실행한 뒤 다시 시도하세요.",
                                    "config.vdf for the device layout was not found. Run Steam once and try again."));

        var plan = new ApplyPlan
        {
            Profile = profile, Validation = validation, ControllerId = controllerTarget.ControllerId,
            Layout = layout, LayoutChanged = layoutChanged,
        };
        var throttleGame = original.FindGame(throttleAppId ?? "");
        var ignored = ThrottleGames(original).Where(g => g.AppId != throttleAppId).Select(g => g.GameName).ToList();
        if (throttleGame != null) plan.Notes.Add(T($"쓰로틀: {throttleGame.GameName}의 쓰로틀 설정을 사용합니다 — ",
                                                   $"Throttle: using the throttle mapping of {throttleGame.GameName} — ")
                                                 + SteamLayoutPlanner.ThrottleSummary(throttleGame));
        if (ignored.Count > 0) plan.Notes.Add(T($"쓰로틀: 다음 게임의 쓰로틀 매핑은 적용하지 않습니다 — {string.Join(", ", ignored)}",
                                                $"Throttle: these games' throttle mappings are not applied — {string.Join(", ", ignored)}"));
        if (layoutChanged && globalRoot != null)
        {
            plan.Writes[env.GlobalConfigPath] = VdfWriter.Write(SteamConfigGenerator.UpdateGlobalConfig(globalRoot, layout, device));
            plan.Notes.Add(existingLayout == null
                ? T("장치 설정: 이 조이스틱을 Steam 일반 컨트롤러로 등록합니다 (게임 매핑에 쓰인 입력으로 자동 생성).",
                    "Device layout: registers this joystick as a Steam generic controller (built from the inputs used in game mappings).")
                : T("장치 설정: 게임 매핑에 쓰인 입력에 맞게 갱신합니다.",
                    "Device layout: updated to match the inputs used in game mappings."));
        }

        // ---- (2) 게임별 ----
        VdfNode? configsetRoot = null;
        if (File.Exists(controllerTarget.ConfigsetPath))
        {
            try { configsetRoot = VdfParser.ParseFile(controllerTarget.ConfigsetPath); }
            catch (VdfParseException ex) { validation.Errors.Add(T("configset 분석 실패 - 변경하지 않습니다: ", "Failed to parse configset - leaving it unchanged: ") + ex.Message); }
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
            var doc = SteamConfigGenerator.Generate(baseDoc.Root, view, perGamePlan, genLog);
            var text = VdfWriter.Write(doc);
            AddWriteIfChanged(plan, target.PerGamePath, text);
            // Steam "내 레이아웃"에는 게임 폴더의 파일마다 항목이 하나씩 보인다. 이전 버전이 다른 파일 이름으로 만든
            // 같은 이름(title)의 레이아웃이 남아 있으면 같은 내용으로 덮어써, 어느 항목을 골라도 같은 매핑이 되게 한다.
            var title = doc.Get("controller_mappings")?.GetValue("title");
            foreach (var same in SameNameLayouts(target.PerGamePath, title))
            {
                AddWriteIfChanged(plan, same, text);
                plan.Notes.Add($"[{game.GameName}] " + T("같은 이름의 레이아웃도 덮어씀: ", "Also overwrites the layout with the same name: ") + Path.GetFileName(same));
            }
            configset = SteamConfigGenerator.UpdateConfigset(configset, game.AppId);

            var selection = SteamInputConfigFinder.ReadSelection(target);
            plan.Games.Add(new GameApplyItem
            {
                AppId = game.AppId, GameName = game.GameName, Target = target, PreviousSelection = selection,
                CloudConfig = env.User != null && SteamInputConfigFinder.HasCloudConfig(env.SteamPath, env.User.AccountId, game.AppId),
            });
            if (!File.Exists(target.PerGamePath) || !IsManagedFile(target.PerGamePath))
                plan.Notes.Add($"[{game.GameName}] " + T("기준 문서: ", "Base document: ") + baseDoc.Origin);
            if (selection.Kind == "workshop")
                plan.Notes.Add(T($"[{game.GameName}] 현재 {selection}을 사용 중 → 이 앱의 게임 매핑으로 바뀝니다.",
                                 $"[{game.GameName}] currently uses {selection} → it will be replaced by this app's game mapping."));
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
            plan.Notes.Add(T($"[{appId}] 게임 매핑이 없어 앱이 만든 설정을 정리합니다 (파일은 백업 폴더로 이동)",
                             $"[{appId}] has no game mapping, so the config this app made is removed (file moved to the backup folder)"));
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
                    validation.Errors.Add(T("Steam 사용자 설정(localconfig.vdf)이 없어 Steam Input을 켤 수 없습니다. Steam에 한 번 로그인한 뒤 다시 시도하세요.",
                                            "Steam user settings (localconfig.vdf) not found, so Steam Input cannot be enabled. Log in to Steam once and try again."));
                }
                else if (toEnable.Count > 0)
                {
                    plan.Writes[localConfig] = SteamInputSetting.EnableFor(text!, toEnable.Select(g => g.AppId));
                    foreach (var g in toEnable)
                    {
                        var previous = SteamInputSetting.Read(root!, g.AppId);
                        plan.SteamInputEnabled.Add((g.GameName, previous));
                        plan.Notes.Add($"[{g.GameName}] " + T("게임 속성 Steam Input: ", "Game properties Steam Input: ") +
                                       $"{SteamInputSetting.DisplayName(previous)} → {SteamInputSetting.DisplayName(SteamInputMode.On)}");
                    }
                }
            }
            catch (Exception ex) when (ex is VdfParseException or IOException or InvalidDataException)
            {
                validation.Errors.Add(T("localconfig.vdf 분석 실패 - Steam Input을 바꾸지 않습니다: ", "Failed to parse localconfig.vdf - Steam Input is left unchanged: ") + ex.Message);
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
                SteamConfigValidator.ValidatePerGame(validation, Path.GetFileName(Path.GetDirectoryName(path)!) + T(" 게임별 설정", " per-game config"), text);
        }
        if (existingLayout == null)
            plan.Notes.Add(T("팁: Steam 설정 > 컨트롤러에서 이 장치의 레이아웃을 한 번 정의해 두면 Steam이 쓰는 장치 GUID를 그대로 재사용합니다.",
                             "Tip: define this device's layout once in Steam Settings > Controller and the device GUID Steam uses will be reused."));
        return plan;
    }

    /// <summary>
    /// Steam에서 비활성화: config.vdf에서 이 장치의 레이아웃을 지워 Steam이 일반 컨트롤러가 아닌 조이스틱으로 보게 한다.
    /// 게임별 설정 파일은 그대로 둔다 (레이아웃이 없으면 쓰이지 않으며, 다시 적용하면 그대로 이어서 쓴다).
    /// </summary>
    public ApplyPlan PrepareDisable(MapperProfile profile)
    {
        var validation = new ValidationResult();
        if (profile.Device.IsEmpty) validation.Errors.Add(T("프로필에 입력 장치가 지정되지 않았습니다.", "No input device is set for the profile."));
        var plan = new ApplyPlan { Profile = profile, Validation = validation };
        if (!validation.IsValid || !File.Exists(env.GlobalConfigPath)) return plan;
        try
        {
            var root = VdfParser.ParseFile(env.GlobalConfigPath);
            if (SteamConfigGenerator.ReadDeviceLayout(root, profile.Device) == null) return plan; // 이미 비활성
            var text = VdfWriter.Write(SteamConfigGenerator.RemoveDeviceLayout(root, profile.Device));
            plan.Writes[env.GlobalConfigPath] = text;
            SteamConfigValidator.ValidateGlobal(validation, text, null);
            plan.Notes.Add(T("장치 설정: 이 조이스틱의 Steam 일반 컨트롤러 등록을 지웁니다 (Steam에서는 조이스틱으로 보임).",
                             "Device layout: removes this joystick's Steam generic controller registration (Steam will see it as a joystick)."));
        }
        catch (VdfParseException ex)
        {
            validation.Errors.Add(T("config.vdf 분석 실패 - 변경하지 않습니다: ", "Failed to parse config.vdf - leaving it unchanged: ") + ex.Message);
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

    /// <summary>같은 게임 폴더에서 이 앱이 만든, 제목이 같은 다른 레이아웃 파일 (configset/preferences 제외).</summary>
    public static IEnumerable<string> SameNameLayouts(string perGamePath, string? title)
    {
        var dir = Path.GetDirectoryName(perGamePath);
        if (string.IsNullOrEmpty(title) || dir == null || !Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.vdf"))
        {
            if (string.Equals(file, perGamePath, StringComparison.OrdinalIgnoreCase)) continue;
            VdfNode? root;
            try { root = VdfParser.ParseFile(file); }
            catch (Exception ex) when (ex is VdfParseException or IOException) { continue; }
            if (!SteamConfigGenerator.IsManaged(root)) continue;
            if (root.Get("controller_mappings")?.GetValue("title") == title) yield return file;
        }
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
        if (!plan.CanApply) throw new InvalidOperationException(T("검증을 통과하지 못한 설정은 적용할 수 없습니다.", "A config that failed validation cannot be applied."));
        if (_isSteamRunning()) throw new InvalidOperationException(T("Steam이 실행 중입니다. Steam을 종료한 후 적용하세요.", "Steam is running. Exit Steam and apply again."));
        var touched = plan.Writes.Keys.Concat(plan.Released.Select(r => r.Path)).ToList();
        foreach (var path in touched) EnsureSafePath(path);

        var backup = backups.Create(new BackupManifest
        {
            AppId = string.Join(",", plan.Games.Select(g => g.AppId).Concat(plan.Released.Select(r => r.AppId))),
            GameName = plan.Games.Count == 0 ? T("장치 설정", "Device layout") : string.Join(", ", plan.Games.Select(g => g.GameName)),
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
                if (written != text) throw new IOException(T("쓰기 검증 실패: ", "Write verification failed: ") + Path.GetFileName(path));
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
            AppLog.Info($"Config applied (layout {(plan.LayoutChanged ? "changed" : "kept")}, games {plan.Games.Count}, released {plan.Released.Count})");
            return backup;
        }
        catch (Exception ex)
        {
            AppLog.Error(T("적용 실패, 원본 복원 중: ", "Apply failed, restoring originals: ") + ex.Message);
            try
            {
                backups.Restore(backup);
                AppLog.Info(T("원본 설정 복원 완료", "Originals restored"));
            }
            catch (Exception restoreEx)
            {
                AppLog.Error(T("자동 복원 실패! 백업 폴더에서 수동 복원이 필요합니다: ", "Automatic restore failed! Restore manually from the backup folder: ") +
                             $"{backup.Folder} ({restoreEx.Message})");
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
            throw new InvalidOperationException(T("허용되지 않은 경로에 쓰기 시도: ", "Attempted write to a disallowed path: ") + full);
        if (!full.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(T("VDF가 아닌 파일 쓰기 시도: ", "Attempted write to a non-VDF file: ") + full);
    }
}
