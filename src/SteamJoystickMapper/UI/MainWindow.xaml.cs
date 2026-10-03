using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Config;
using SteamJoystickMapper.Devices;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.UI;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ProfileStore _store = new(AppPaths.ProfileFile);
    private readonly BackupManager _backups = new(AppPaths.Backup);
    private MapperProfile _profile;
    private SteamEnvironment? _env;
    private List<SteamGame> _allGames = new();
    private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
    private bool _busy;
    /// <summary>이번 실행에서 마지막으로 매핑을 편집한 게임 (쓰로틀 선택의 기본값으로 먼저 보여 줌).</summary>
    private string? _lastEditedAppId;

    public MainWindow()
    {
        InitializeComponent();
        _profile = _store.LoadOrCreate();
        AppLog.LineAdded += line => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(line);
            LogList.ScrollIntoView(line);
        });
        Loaded += (_, _) => Initialize();
        Closing += (_, _) => _settings.Save();
    }

    private SteamGame? SelectedTarget => GameCombo.SelectedItem as SteamGame;

    /// <summary>상태 표시용: 마지막으로 고른 쓰로틀 게임 (아직 쓰로틀 매핑이 있을 때만).</summary>
    private string? CurrentThrottleAppId =>
        SteamApplyService.ThrottleGames(_profile).Any(g => g.AppId == _settings.LastThrottleAppId) ? _settings.LastThrottleAppId : null;
    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private void Initialize()
    {
        // 장치 선택 등으로 RefreshAll이 먼저 돌면 LastAppId가 덮어써지므로 먼저 읽어 둔다.
        var lastAppId = _settings.LastAppId ?? "";
        _env = SteamEnvironment.Detect();
        SteamInfoText.Text = _env == null
            ? "Steam을 찾을 수 없습니다. Steam 설치 후 다시 실행하세요."
            : $"Steam: {_env.SteamPath}    사용자: {(_env.User?.ToString() ?? "없음")}";

        LoadGames();
        RefreshDevices();
        // 목록이 채워진 뒤에 선택해야 반영된다.
        GameCombo.ItemsSource = _allGames;
        GameCombo.SelectedItem = _allGames.FirstOrDefault(g => g.AppId == lastAppId) ?? _allGames.FirstOrDefault();
        RefreshAll();
    }

    private void SaveProfile()
    {
        _store.Save(_profile);
        RefreshAll();
    }

    private void RefreshAll()
    {
        foreach (var g in _allGames) g.HasMapping = _profile.FindGame(g.AppId) != null;
        ApplyGameFilter();
        RefreshProfileSummary();
        RefreshTarget();
        RefreshSteamStatus();
    }

    // ---------------- 프로필 ----------------

    private void RefreshProfileSummary()
    {
        ProfileNameText.Text = _profile.ProfileName;
        var device = _profile.Device.IsEmpty ? "장치 미지정" : $"{_profile.Device.Name} [{_profile.Device.Vid}:{_profile.Device.Pid}]";
        var games = _profile.Games.Count == 0 ? "없음" : string.Join(", ", _profile.Games.Select(g => g.GameName));
        ProfileSummaryText.Text = $"장치: {device}    게임 매핑: {games}";
    }

    private void BackupProfileFile()
    {
        if (!File.Exists(_store.Path)) return;
        var copy = Path.Combine(AppPaths.Root, $"profile-backup-{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.Copy(_store.Path, copy, overwrite: true);
        AppLog.Info($"이전 프로필 보관: {Path.GetFileName(copy)}");
    }

    private void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(this, "프로필 모두 초기화",
                $"모든 게임 매핑을 지우고 '{MapperProfile.DefaultName}' 빈 프로필로 되돌립니다.",
                "• 이전 프로필 파일은 profile-backup-*.json 으로 보관됩니다.\n" +
                "• Steam 설정은 지금 바뀌지 않습니다.\n" +
                "  - 다음에 [Steam에 적용]하면 이 앱이 만든 게임별 설정이 정리됩니다.\n" +
                "  - 적용 전 상태로 되돌리려면 [원본 설정 복원]을 사용하세요.",
                "초기화")) return;
        BackupProfileFile();
        _profile = MapperProfile.CreateDefault(SelectedDevice?.ToIdentity());
        _store.Save(_profile);
        AppLog.Info("프로필 초기화");
        RefreshAll();
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "프로필 JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var imported = ProfileStore.Read(dlg.FileName);
            if (MessageBox.Show(this, "현재 프로필을 가져온 프로필로 바꿉니다. 현재 프로필은 보관됩니다.\n계속할까요?", "가져오기",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            BackupProfileFile();
            _profile = imported;
            SaveProfile();
            AppLog.Info("프로필 가져오기 완료");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"프로필을 읽을 수 없습니다: {ex.Message}", "가져오기", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "프로필 JSON (*.json)|*.json", FileName = _profile.ProfileName + ".json" };
        if (dlg.ShowDialog(this) != true) return;
        ProfileStore.Export(_profile, dlg.FileName);
        AppLog.Info("프로필 내보내기 완료");
    }

    // ---------------- 게임 목록 ----------------

    private void LoadGames()
    {
        // 설치된 게임만. 단, 게임 매핑이 있는 미설치 게임은 매핑을 지울 수 있도록 남긴다.
        var games = _env == null ? new List<SteamGame>() : SteamGameScanner.Scan(_env.SteamPath).ToList();
        foreach (var pg in _profile.Games.Where(pg => games.All(g => g.AppId != pg.AppId)))
            games.Add(new SteamGame { AppId = pg.AppId, Name = pg.GameName, Installed = false });
        AppLog.Info($"Steam 게임 {games.Count}개 검색됨");
        // 매핑이 있는 게임을 위로
        _allGames = games.OrderByDescending(g => _profile.FindGame(g.AppId) != null)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void ApplyGameFilter()
    {
        var q = GameSearchBox.Text.Trim();
        var selected = SelectedTarget;
        var filtered = q.Length == 0 ? _allGames
            : _allGames.Where(g => g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || g.AppId.Contains(q)).ToList();
        GameCombo.ItemsSource = null;
        GameCombo.ItemsSource = filtered;
        if (selected != null && filtered.Contains(selected)) GameCombo.SelectedItem = selected;
        else GameCombo.SelectedIndex = filtered.Count > 0 ? 0 : -1;
    }

    private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GameSearchHint.Visibility = GameSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyGameFilter();
        // 검색 중이면 첫 번째 일치 게임으로
        if (GameSearchBox.Text.Trim().Length > 0 && GameCombo.Items.Count > 0 && SelectedTarget is { } t &&
            !t.Name.Contains(GameSearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) && !t.AppId.Contains(GameSearchBox.Text.Trim()))
            GameCombo.SelectedIndex = 0;
    }

    private void GameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedTarget != null && _settings.LastAppId != SelectedTarget.AppId)
        {
            _settings.LastAppId = SelectedTarget.AppId;
            _settings.Save();
        }
        RefreshTarget();
        RefreshSteamStatus();
    }

    // ---------------- 장치 ----------------

    private void RefreshDevices()
    {
        var previous = SelectedDevice?.InstanceGuid.ToString() ?? _settings.LastDeviceInstanceId ?? _profile.Device.InstanceId;
        _devices = DeviceScanner.Scan();
        DeviceCombo.ItemsSource = _devices;
        DeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.InstanceGuid.ToString() == previous)
                                   ?? _devices.FirstOrDefault(d => !_profile.Device.IsEmpty && d.ToIdentity().SameProduct(_profile.Device))
                                   ?? _devices.FirstOrDefault();
        if (_devices.Count == 0)
        {
            DeviceDetailsText.Text = "연결된 조이스틱이 없습니다.";
            AppLog.Warn("연결된 조이스틱/HID 게임 장치가 없습니다.");
        }
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var d = SelectedDevice;
        DeviceDetailsText.Text = d?.Details ?? "";
        if (d == null) return;
        _settings.LastDeviceInstanceId = d.InstanceGuid.ToString();
        if (d.DuplicateIndex > 0)
            DeviceDetailsText.Text += "\n※ 같은 제품이 여러 개 연결됨: Steam 장치 레이아웃은 VID/PID 단위로 공유됩니다.";

        if (_profile.Device.IsEmpty)
        {
            _profile.Device = d.ToIdentity();
            SaveProfile();
        }
        else if (!d.ToIdentity().SameProduct(_profile.Device))
        {
            DeviceDetailsText.Text += $"\n※ 프로필 장치({_profile.Device.Name})와 다른 장치입니다. 매핑 편집 시 장치를 바꿀지 묻습니다.";
        }
    }

    /// <summary>편집 전에 프로필 장치를 확인/갱신. 실시간 감지에 쓸 장치를 반환 (없으면 null).</summary>
    private DeviceInfo? PrepareDeviceForEdit()
    {
        var d = SelectedDevice;
        if (d == null) return null;
        var identity = d.ToIdentity();
        if (_profile.Device.IsEmpty || identity.SameProduct(_profile.Device))
        {
            _profile.Device = identity; // 축 목록 등 최신 정보로 갱신
            return d;
        }
        var answer = MessageBox.Show(this,
            $"프로필 장치는 {_profile.Device.Name} [{_profile.Device.Vid}:{_profile.Device.Pid}]입니다.\n" +
            $"선택한 {d.Name}(으)로 바꿀까요?\n\n매핑은 유지되지만, 새 장치에 없는 입력은 검증 오류가 납니다.\n[아니요]를 누르면 장치 감지 없이 편집합니다.",
            "장치 변경", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) throw new OperationCanceledException();
        if (answer != MessageBoxResult.Yes) return null;
        _profile.Device = identity;
        return d;
    }

    // ---------------- 매핑 대상 ----------------

    private void RefreshTarget()
    {
        var t = SelectedTarget;
        EditButton.IsEnabled = t != null;
        if (t == null)
        {
            RemoveGameButton.IsEnabled = CopyGameButton.IsEnabled = false;
            TargetText.Text = "게임을 선택하세요.";
            SetSteamInputText(Brushes.Gray, "");
            return;
        }
        RemoveGameButton.IsEnabled = _profile.FindGame(t.AppId) != null;
        CopyGameButton.IsEnabled = _profile.Games.Any(g => g.AppId != t.AppId);
        var game = _profile.FindGame(t.AppId);
        EditButton.Content = game == null ? "이 게임 매핑 만들기" : "게임 매핑 편집";
        TargetText.Text = game == null
            ? $"{t.Name}: 게임 매핑 없음"
            : $"{t.Name}: 게임 매핑 {game.Bindings.Count}개";
        RefreshSteamInputText(t, game);
    }

    /// <summary>선택한 게임의 Steam Input 사용 여부 (게임 속성 > 컨트롤러)와 경고.</summary>
    private void RefreshSteamInputText(SteamGame t, GameMapping? game)
    {
        var path = _env?.LocalConfigPath;
        if (path == null) { SetSteamInputText(Brushes.Gray, ""); return; }
        var mode = SteamInputSetting.Read(path, t.AppId);
        var label = $"Steam Input: {SteamInputSetting.DisplayName(mode)}";
        if (game != null)
        {
            if (mode == SteamInputMode.On) SetSteamInputText(Brushes.ForestGreen, $"✓ {label} → 게임 매핑이 적용됩니다.");
            else SetSteamInputText(Brushes.DarkOrange, $"⚠ {label} → 이대로는 게임 매핑이 동작하지 않을 수 있습니다. [Steam에 적용]하면 '사용'으로 바꿉니다.");
            return;
        }
        SetSteamInputText(Brushes.DimGray, $"{label} → 게임 매핑을 만들어 적용하면 '사용'으로 바꿉니다.");
    }

    private void SetSteamInputText(Brush color, string text)
    {
        SteamInputText.Foreground = color;
        SteamInputText.Text = text;
        SteamInputText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EditMapping_Click(object sender, RoutedEventArgs e)
    {
        var t = SelectedTarget;
        if (t == null) return;
        DeviceInfo? device;
        try { device = PrepareDeviceForEdit(); }
        catch (OperationCanceledException) { return; }
        if (_profile.Device.IsEmpty)
        {
            MessageBox.Show(this, "입력 장치를 연결하고 선택하세요.", "매핑 편집");
            return;
        }

        var existing = _profile.FindGame(t.AppId);
        var game = existing ?? new GameMapping { AppId = t.AppId, GameName = t.Name };
        var editor = new MappingEditorWindow(_profile, game, device) { Owner = this };
        if (editor.ShowDialog() != true) return;
        game.Bindings = editor.Result;
        game.GameName = t.Name;
        if (existing == null) _profile.Games.Add(game);
        AppLog.Info($"게임 매핑 저장: {t.Name} ({game.Bindings.Count}개)");
        _lastEditedAppId = t.AppId;
        SaveProfile();
    }

    private void RemoveGameMapping_Click(object sender, RoutedEventArgs e)
    {
        var t = SelectedTarget;
        var game = t == null ? null : _profile.FindGame(t.AppId);
        if (game == null) return;
        if (MessageBox.Show(this, $"{game.GameName}의 게임 매핑을 삭제할까요?\n\n[Steam에 적용]하면 이 앱이 만든 이 게임의 Steam 설정을 정리합니다.",
                "게임 매핑 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profile.Games.Remove(game);
        AppLog.Info($"게임 매핑 삭제: {game.GameName}");
        SaveProfile();
    }

    private void CopyGameMapping_Click(object sender, RoutedEventArgs e)
    {
        var t = SelectedTarget;
        if (t == null) return;
        var sources = _profile.Games.Where(g => g.AppId != t.AppId)
            .Select(g => new SteamGame { AppId = g.AppId, Name = g.GameName }).ToList();
        var picked = Dialogs.PickGame(this, $"{t.Name}에 복사할 게임 매핑 선택", sources);
        if (picked == null) return;
        var source = _profile.FindGame(picked.AppId)!;
        var target = _profile.FindGame(t.AppId);
        if (target != null && MessageBox.Show(this, $"{t.Name}의 기존 게임 매핑을 덮어쓸까요?", "게임 매핑 복사",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (target == null) _profile.Games.Add(target = new GameMapping { AppId = t.AppId, GameName = t.Name });
        target.Bindings = source.Bindings.Select(b => b.Clone()).ToList();
        AppLog.Info($"게임 매핑 복사: {source.GameName} → {t.Name}");
        SaveProfile();
    }

    // ---------------- Steam 상태 ----------------

    private void RefreshSteamStatus()
    {
        if (_env == null) { SetStatus(Brushes.Gray, "Steam 없음", ""); return; }
        var service = new SteamApplyService(_env, _backups);
        var registered = service.IsDeviceRegistered(_profile.Device);
        DisableButton.IsEnabled = registered;
        var registration = registered ? "장치: Steam 일반 컨트롤러로 등록됨" : "장치: Steam 일반 컨트롤러 아님 (조이스틱)";
        if (_profile.Device.IsEmpty || _profile.Games.Count == 0)
        {
            SetStatus(Brushes.Gray, "게임을 선택해 게임 매핑을 만드세요", _profile.Device.IsEmpty ? "" : registration);
            return;
        }

        var plan = service.Prepare(_profile, CurrentThrottleAppId);
        var detail = new StringBuilder();
        detail.AppendLine(registration);
        var t = SelectedTarget;
        var dir = _env.ControllerConfigDir;
        if (t != null && dir != null)
        {
            var target = SteamInputConfigFinder.Resolve(dir, _profile.Device, t.AppId);
            var selection = SteamInputConfigFinder.ReadSelection(target);
            var managed = selection.Kind != "none" &&
                          SteamApplyService.FindManagedGames(dir, target.ControllerId).Any(m => m.AppId == t.AppId);
            detail.AppendLine(managed
                ? "이 게임: Steam에 이 앱의 게임 매핑이 적용되어 있음"
                : selection.Kind != "none"
                    ? $"이 게임: Steam 자체 설정 사용 중 ({selection}) — 게임 매핑을 만들어 적용하면 대체됩니다"
                    : "이 게임: Steam 게임별 설정 없음");
            if (_env.User != null && SteamInputConfigFinder.HasCloudConfig(_env.SteamPath, _env.User.AccountId, t.AppId))
                detail.AppendLine("⚠ Steam 클라우드 컨트롤러 설정 있음");
        }
        detail.Append($"컨트롤러 ID: {plan.ControllerId ?? "-"}");

        if (!plan.Validation.IsValid)
        {
            SetStatus(Brushes.Firebrick, $"검증 오류 {plan.Validation.Errors.Count}개 — 적용 전에 고쳐야 합니다",
                string.Join("\n", plan.Validation.Errors.Take(3)) + "\n" + detail);
            return;
        }
        if (plan.HasChanges)
        {
            var parts = new List<string>();
            if (plan.LayoutChanged) parts.Add("장치 설정");
            var gameWrites = plan.Writes.Keys.Count(k => k.EndsWith($"{plan.ControllerId}.vdf", StringComparison.OrdinalIgnoreCase) && !k.Contains("configset_"));
            if (gameWrites > 0) parts.Add($"게임 매핑 {gameWrites}개");
            if (plan.Released.Count > 0) parts.Add($"정리 {plan.Released.Count}개");
            if (plan.SteamInputEnabled.Count > 0) parts.Add($"Steam Input 켜기 {plan.SteamInputEnabled.Count}개");
            SetStatus(Brushes.Orange, $"Steam에 적용할 변경 있음 ({string.Join(", ", parts.DefaultIfEmpty("설정 파일"))})", detail.ToString());
        }
        else
        {
            SetStatus(Brushes.LimeGreen, "Steam과 일치 (프로필이 모두 적용되어 있음)", detail.ToString());
        }
    }

    private void SetStatus(Brush color, string text, string detail)
    {
        StatusDot.Fill = color;
        StatusText.Text = text;
        StatusDetailText.Text = AppLog.Sanitize(detail.TrimEnd());
    }

    private void Analyze_Click(object sender, RoutedEventArgs e)
    {
        var dir = _env?.ControllerConfigDir;
        if (_env == null || dir == null || _profile.Device.IsEmpty) return;
        var device = _profile.Device;
        var text = new StringBuilder();
        Steam.Sdl.SdlMapping? layout = null;
        try { layout = SteamConfigGenerator.ReadDeviceLayout(VdfParser.ParseFile(_env.GlobalConfigPath), device); }
        catch (Exception ex) when (ex is VdfParseException or IOException) { text.AppendLine($"config.vdf 분석 실패: {ex.Message}"); }
        text.AppendLine("=== 장치 설정 (config.vdf SDL_GamepadBind, 모든 게임 공유) ===");
        text.AppendLine(layout == null ? "(없음)" : string.Join("\n", layout.ElementFields.Select(f => $"  {f.Key,-14} ← {f.Value}")));
        text.AppendLine();

        var t = SelectedTarget;
        if (t != null)
        {
            var target = SteamInputConfigFinder.Resolve(dir, device, t.AppId);
            text.AppendLine($"=== {t.Name}: configset {SteamInputConfigFinder.ReadSelection(target)} ===");
            var files = SteamInputConfigFinder.ExistingGameConfigs(dir, t.AppId);
            if (files.Count == 0) text.AppendLine("이 게임의 Steam Input 설정 파일이 없습니다.");
            foreach (var f in files)
            {
                text.AppendLine($"=== {Path.GetFileName(f)} ===");
                try { text.AppendLine(SteamInputConfigFinder.Analyze(VdfParser.ParseFile(f), Path.GetFileNameWithoutExtension(f), t.AppId)); }
                catch (Exception ex) when (ex is VdfParseException or IOException) { text.AppendLine($"분석 실패: {ex.Message}"); }
            }
        }
        Dialogs.ShowText(this, "Steam 설정 분석", AppLog.Sanitize(text.ToString()));
    }

    // ---------------- 적용 / 복원 ----------------

    private async Task<bool> EnsureSteamClosedAsync(string action)
    {
        if (!SteamProcess.IsRunning()) return true;
        var answer = MessageBox.Show(this,
            "Steam이 실행 중입니다.\n\nSteam이 설정을 다시 저장하면 현재 변경 사항이 덮어써질 수 있습니다.\n\n" +
            $"[예] Steam 종료 후 {action}\n[아니요] 취소",
            "Steam 실행 중", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return false;

        AppLog.Info("Steam 종료 요청 중...");
        var closed = await SteamProcess.ShutdownAsync(_env!.SteamPath, TimeSpan.FromSeconds(60));
        if (!closed)
        {
            AppLog.Error("Steam이 종료되지 않았습니다.");
            MessageBox.Show(this, "Steam이 60초 안에 종료되지 않았습니다. Steam을 직접 종료한 뒤 다시 시도하세요.", "Steam", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        AppLog.Info("Steam 종료됨");
        return true;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _env == null) return;
        _busy = true;
        try
        {
            var service = new SteamApplyService(_env, _backups);
            // 쓰로틀은 기본적으로 무시: 쓰로틀 매핑이 있는 게임이 있으면 어느 게임 설정을 쓸지(또는 안 쓸지) 고른다
            string? throttleAppId = null;
            var throttleGames = SteamApplyService.ThrottleGames(_profile);
            if (throttleGames.Count > 0)
            {
                var edited = throttleGames.FirstOrDefault(g => g.AppId == _lastEditedAppId);
                var (cancelled, chosen) = Dialogs.ChooseThrottle(this, throttleGames, edited?.AppId ?? _settings.LastThrottleAppId);
                if (cancelled) return;
                throttleAppId = chosen;
                _settings.LastThrottleAppId = chosen ?? "";
                _settings.Save();
            }
            var plan = service.Prepare(_profile, throttleAppId);
            if (!plan.Validation.IsValid) { ShowValidationErrors(plan.Validation); return; }
            if (!plan.HasChanges)
            {
                MessageBox.Show(this, "Steam 설정이 이미 프로필과 일치합니다.", "Steam에 적용");
                return;
            }
            if (!ShowPlanAndConfirm(plan)) return;

            if (!await EnsureSteamClosedAsync("적용")) return;
            // Steam은 종료하면서 config.vdf를 다시 저장하므로 종료 후 다시 생성/검증한다.
            plan = service.Prepare(_profile, throttleAppId);
            if (!plan.Validation.IsValid) { ShowValidationErrors(plan.Validation); return; }
            if (!plan.HasChanges) { RefreshSteamStatus(); return; }

            var backup = service.Execute(plan);
            RefreshAll();

            var restart = MessageBox.Show(this,
                "Steam Input 설정을 적용했습니다.\n\n" +
                $"백업: {Path.GetFileName(backup.Folder)}\n\n" +
                "확인 사항:\n" +
                (plan.SteamInputEnabled.Count > 0
                    ? $" • Steam Input을 '사용'으로 바꾼 게임: {string.Join(", ", plan.SteamInputEnabled.Select(g => g.GameName))}\n"
                    : "") +
                " • 조이스틱을 직접 지원하는 게임(MSFS, DCS 등)은 Steam Input을 '사용 안 함'으로 두세요.\n" +
                (plan.Games.Any(g => g.CloudConfig) ? " • Steam 클라우드가 이전 설정을 복원하면 다시 적용하세요.\n" : "") +
                "\nSteam을 다시 시작할까요?",
                "적용 완료", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (restart == MessageBoxResult.Yes) SteamProcess.Start(_env.SteamPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"적용 실패: {ex.Message}");
            MessageBox.Show(this, $"적용에 실패했습니다. 원본 설정을 복원했습니다.\n\n{AppLog.Sanitize(ex.Message)}", "적용 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Steam에서 비활성화: 장치 설정을 지워 Steam이 이 조이스틱을 일반 컨트롤러가 아닌 조이스틱으로 보게 한다.</summary>
    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _env == null) return;
        _busy = true;
        try
        {
            var service = new SteamApplyService(_env, _backups);
            var plan = service.PrepareDisable(_profile);
            if (!plan.Validation.IsValid) { ShowValidationErrors(plan.Validation); return; }
            if (!plan.HasChanges)
            {
                MessageBox.Show(this, "이 조이스틱은 Steam에 일반 컨트롤러로 등록되어 있지 않습니다.", "Steam에서 비활성화");
                return;
            }
            if (!Dialogs.Confirm(this, "Steam에서 비활성화",
                    $"{_profile.Device.Name}의 Steam 일반 컨트롤러 등록(장치 설정)을 지웁니다.",
                    "• Steam에서는 이 장치가 일반 컨트롤러가 아닌 조이스틱으로 보입니다 (게임 매핑이 동작하지 않음).\n" +
                    "• 게임 매핑과 게임별 설정 파일은 그대로 둡니다. 다시 [Steam에 적용]하면 원래대로 동작합니다.\n" +
                    "• 변경 전 config.vdf는 자동 백업됩니다.",
                    "비활성화")) return;

            if (!await EnsureSteamClosedAsync("비활성화")) return;
            plan = service.PrepareDisable(_profile); // Steam 종료 시 config.vdf가 다시 저장되므로
            if (!plan.Validation.IsValid) { ShowValidationErrors(plan.Validation); return; }
            if (!plan.HasChanges) { RefreshSteamStatus(); return; }

            var backup = service.Execute(plan);
            AppLog.Info("Steam 일반 컨트롤러 등록 해제");
            RefreshAll();
            if (MessageBox.Show(this, $"비활성화했습니다.\n\n백업: {Path.GetFileName(backup.Folder)}\n\nSteam을 다시 시작할까요?",
                    "비활성화 완료", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                SteamProcess.Start(_env.SteamPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"비활성화 실패: {ex.Message}");
            MessageBox.Show(this, $"비활성화에 실패했습니다. 원본 설정을 복원했습니다.\n\n{AppLog.Sanitize(ex.Message)}", "비활성화 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowValidationErrors(Steam.ValidationResult v)
    {
        AppLog.Warn($"검증 실패: 오류 {v.Errors.Count}개");
        MessageBox.Show(this, "검증에 실패하여 적용하지 않습니다.\n\n" + string.Join("\n", v.Errors.Select(x => "• " + x)) +
                              (v.Warnings.Count > 0 ? "\n\n경고:\n" + string.Join("\n", v.Warnings.Select(x => "• " + x)) : ""),
            "설정 검증", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool ShowPlanAndConfirm(ApplyPlan plan)
    {
        AppLog.Info("Validation OK");
        var body = new StringBuilder();
        body.AppendLine("변경할 파일 (변경 전 자동 백업됨):");
        foreach (var path in plan.Writes.Keys) body.AppendLine("  • " + path);
        foreach (var (appId, path) in plan.Released) body.AppendLine($"  • (정리, 백업 폴더로 이동) {path}");
        body.AppendLine();
        if (plan.LayoutChanged)
        {
            body.AppendLine("⚠ 장치 설정(config.vdf)을 변경합니다: 게임 매핑에 쓰인 입력으로 이 조이스틱을 Steam 일반 컨트롤러로 등록합니다.");
            body.AppendLine("  게임 매핑이 없는 게임에서는 이 장치 설정대로 동작합니다.");
            body.AppendLine("  장치 설정: " + string.Join(", ", plan.Layout!.ElementFields.Select(f => $"{f.Key}:{f.Value}")));
            body.AppendLine();
        }
        if (plan.Games.Count > 0)
            body.AppendLine("게임 매핑: " + string.Join(", ", plan.Games.Select(g => $"{g.GameName} ({g.AppId})")));
        if (plan.SteamInputEnabled.Count > 0)
            body.AppendLine("게임 속성 Steam Input → '사용' (localconfig.vdf): " +
                            string.Join(", ", plan.SteamInputEnabled.Select(g => $"{g.GameName} (현재 {SteamInputSetting.DisplayName(g.Previous)})")));
        var cloud = plan.Games.Where(g => g.CloudConfig).ToList();
        if (cloud.Count > 0)
        {
            body.AppendLine($"⚠ Steam 클라우드 컨트롤러 설정이 있는 게임: {string.Join(", ", cloud.Select(g => g.GameName))}");
            body.AppendLine("  Steam 재실행 시 클라우드 설정이 복원될 수 있습니다 (앱은 클라우드 파일을 삭제하지 않습니다).");
        }
        body.AppendLine();
        body.AppendLine("정보:");
        foreach (var n in plan.Notes) body.AppendLine("  " + n);
        if (plan.Validation.Warnings.Count > 0)
        {
            body.AppendLine();
            body.AppendLine("경고:");
            foreach (var w in plan.Validation.Warnings) body.AppendLine("  • " + w);
        }
        return Dialogs.Confirm(this, "Steam에 적용", $"'{plan.Profile.ProfileName}' 프로필 전체를 Steam에 적용합니다.",
            AppLog.Sanitize(body.ToString()), "적용");
    }

    private async void RestoreLatest_Click(object sender, RoutedEventArgs e)
    {
        var backup = _backups.List().FirstOrDefault();
        if (backup == null)
        {
            MessageBox.Show(this, "백업이 없습니다.", "원본 설정 복원");
            return;
        }
        await RestoreAsync(backup);
    }

    public async Task RestoreAsync(BackupManifest backup)
    {
        if (_busy) return;
        if (MessageBox.Show(this, $"다음 백업으로 복원할까요? (그 적용 직전 상태로 돌아갑니다)\n\n{backup.Display}\n파일 {backup.Files.Count}개\n\n" +
                                  "프로필은 바뀌지 않으므로, 상태 표시는 '적용할 변경 있음'이 됩니다.", "복원",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        try
        {
            if (!await EnsureSteamClosedAsync("복원")) return;
            _backups.Restore(backup);
            RefreshAll();
            MessageBox.Show(this, "복원했습니다.", "복원", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error($"복원 실패: {ex.Message}");
            MessageBox.Show(this, $"복원 실패: {AppLog.Sanitize(ex.Message)}\n백업 폴더: {backup.Folder}", "복원", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void BackupHistory_Click(object sender, RoutedEventArgs e)
    {
        new BackupHistoryWindow(_backups, this) { Owner = this }.ShowDialog();
    }
}
