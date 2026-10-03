using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Config;
using SteamJoystickMapper.Devices;
using SteamJoystickMapper.Localization;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Vdf;
using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.UI;

public partial class MainWindow : Window
{
    public const string KofiUrl = "https://ko-fi.com/killkimno";

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
        // 언어: 저장된 선택이 없으면 시스템 언어 (한국어 외는 영어)
        Loc.Init(_settings.Language);
        InitializeComponent();
        _profile = _store.LoadOrCreate();
        AppLog.LineAdded += line => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(line);
            LogList.ScrollIntoView(line);
        });
        Loc.Changed += () =>
        {
            UpdateLanguageButtons();
            RefreshSteamInfo();
            RefreshAll();
        };
        UpdateLanguageButtons();
        Loaded += (_, _) => Initialize();
        Closing += (_, _) => _settings.Save();
    }

    private SteamGame? SelectedTarget => GameCombo.SelectedItem as SteamGame;
    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    /// <summary>상태 표시용: 마지막으로 고른 쓰로틀 게임 (아직 쓰로틀 매핑이 있을 때만).</summary>
    private string? CurrentThrottleAppId =>
        SteamApplyService.ThrottleGames(_profile).Any(g => g.AppId == _settings.LastThrottleAppId) ? _settings.LastThrottleAppId : null;

    private void Initialize()
    {
        // 장치 선택 등으로 RefreshAll이 먼저 돌면 LastAppId가 덮어써지므로 먼저 읽어 둔다.
        var lastAppId = _settings.LastAppId ?? "";
        _env = SteamEnvironment.Detect();
        RefreshSteamInfo();

        LoadGames();
        RefreshDevices();
        // 목록이 채워진 뒤에 선택해야 반영된다.
        GameCombo.ItemsSource = _allGames;
        GameCombo.SelectedItem = _allGames.FirstOrDefault(g => g.AppId == lastAppId) ?? _allGames.FirstOrDefault();
        RefreshAll();
    }

    private void RefreshSteamInfo() =>
        SteamInfoText.Text = _env == null
            ? T("Steam을 찾을 수 없습니다. Steam 설치 후 다시 실행하세요.", "Steam was not found. Install Steam and start again.")
            : $"Steam: {_env.SteamPath}    {T("사용자", "User")}: {(_env.User?.ToString() ?? T("없음", "none"))}";

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

    // ---------------- 언어 / 후원 ----------------

    private void UpdateLanguageButtons()
    {
        LangKoButton.IsChecked = IsKorean;
        LangEnButton.IsChecked = !IsKorean;
    }

    private void LangKo_Checked(object sender, RoutedEventArgs e) => ChangeLanguage(true);
    private void LangEn_Checked(object sender, RoutedEventArgs e) => ChangeLanguage(false);
    private void Lang_Unchecked(object sender, RoutedEventArgs e) => UpdateLanguageButtons(); // 선택된 언어를 다시 눌러도 선택 유지

    private void ChangeLanguage(bool korean)
    {
        if (korean == IsKorean) return;
        Loc.SetLanguage(korean);
        UpdateLanguageButtons();
        _settings.Language = Loc.Code;
        _settings.Save();
    }

    /// <summary>후원하기: 한국어는 카카오페이 QR + Ko-fi 창, 영어는 바로 Ko-fi (MWOLab과 같은 방식).</summary>
    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        if (IsKorean) Dialogs.ShowDonate(this, KofiUrl);
        else OpenUrl(KofiUrl);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Warn($"{T("링크를 열 수 없습니다", "Could not open link")}: {ex.Message}"); }
    }

    // ---------------- 프로필 ----------------

    private void RefreshProfileSummary()
    {
        ProfileNameText.Text = _profile.ProfileName;
        var device = _profile.Device.IsEmpty ? T("장치 미지정", "no device") : $"{_profile.Device.Name} [{_profile.Device.Vid}:{_profile.Device.Pid}]";
        var games = _profile.Games.Count == 0 ? T("없음", "none") : string.Join(", ", _profile.Games.Select(g => g.GameName));
        ProfileSummaryText.Text = $"{T("장치", "Device")}: {device}    {T("게임 매핑", "Game mappings")}: {games}";
    }

    private void BackupProfileFile()
    {
        if (!File.Exists(_store.Path)) return;
        var copy = Path.Combine(AppPaths.Root, $"profile-backup-{DateTime.Now:yyyyMMdd_HHmmss}.json");
        File.Copy(_store.Path, copy, overwrite: true);
        AppLog.Info($"{T("이전 프로필 보관", "Previous profile kept")}: {Path.GetFileName(copy)}");
    }

    private void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm(this, T("프로필 모두 초기화", "Reset profile"),
                T($"모든 게임 매핑을 지우고 '{MapperProfile.DefaultName}' 빈 프로필로 되돌립니다.",
                  $"Deletes all game mappings and returns to an empty '{MapperProfile.DefaultName}' profile."),
                T("• 이전 프로필 파일은 profile-backup-*.json 으로 보관됩니다.\n" +
                  "• Steam 설정은 지금 바뀌지 않습니다. 다음에 [Steam에 적용]하면 이 앱이 만든 게임별 설정이 정리됩니다.",
                  "• The previous profile is kept as profile-backup-*.json.\n" +
                  "• Steam settings are not changed now. The next [Apply to Steam] cleans up the per-game configs this app made."),
                T("초기화", "Reset"), T("취소", "Cancel"))) return;
        BackupProfileFile();
        _profile = MapperProfile.CreateDefault(SelectedDevice?.ToIdentity());
        _store.Save(_profile);
        AppLog.Info(T("프로필 초기화", "Profile reset"));
        RefreshAll();
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = T("프로필 JSON (*.json)|*.json", "Profile JSON (*.json)|*.json") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var imported = ProfileStore.Read(dlg.FileName);
            if (MessageBox.Show(this, T("현재 프로필을 가져온 프로필로 바꿉니다. 현재 프로필은 보관됩니다.\n계속할까요?",
                        "Replace the current profile with the imported one? The current profile is kept as a backup."),
                    T("가져오기", "Import"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            BackupProfileFile();
            _profile = imported;
            SaveProfile();
            AppLog.Info(T("프로필 가져오기 완료", "Profile imported"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"{T("프로필을 읽을 수 없습니다", "Could not read the profile")}: {ex.Message}", T("가져오기", "Import"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = T("프로필 JSON (*.json)|*.json", "Profile JSON (*.json)|*.json"), FileName = _profile.ProfileName + ".json" };
        if (dlg.ShowDialog(this) != true) return;
        ProfileStore.Export(_profile, dlg.FileName);
        AppLog.Info(T("프로필 내보내기 완료", "Profile exported"));
    }

    // ---------------- 게임 목록 ----------------

    private void LoadGames()
    {
        // 설치된 게임만. 단, 게임 매핑이 있는 미설치 게임은 매핑을 지울 수 있도록 남긴다.
        var games = _env == null ? new List<SteamGame>() : SteamGameScanner.Scan(_env.SteamPath).ToList();
        foreach (var pg in _profile.Games.Where(pg => games.All(g => g.AppId != pg.AppId)))
            games.Add(new SteamGame { AppId = pg.AppId, Name = pg.GameName, Installed = false });
        AppLog.Info(T($"Steam 게임 {games.Count}개 검색됨", $"Found {games.Count} Steam games"));
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
            DeviceDetailsText.Text = T("연결된 조이스틱이 없습니다.", "No joystick is connected.");
            AppLog.Warn(T("연결된 조이스틱/HID 게임 장치가 없습니다.", "No joystick / HID game device is connected."));
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
            DeviceDetailsText.Text += T("\n※ 같은 제품이 여러 개 연결됨: Steam 장치 설정은 VID/PID 단위로 공유됩니다.",
                                        "\n※ Several of the same product are connected: Steam shares the device layout per VID/PID.");

        if (_profile.Device.IsEmpty)
        {
            _profile.Device = d.ToIdentity();
            SaveProfile();
        }
        else if (!d.ToIdentity().SameProduct(_profile.Device))
        {
            DeviceDetailsText.Text += T($"\n※ 프로필 장치({_profile.Device.Name})와 다른 장치입니다. 매핑 편집 시 장치를 바꿀지 묻습니다.",
                                        $"\n※ This is not the profile device ({_profile.Device.Name}). You will be asked to switch when editing a mapping.");
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
            T($"프로필 장치는 {_profile.Device.Name} [{_profile.Device.Vid}:{_profile.Device.Pid}]입니다.\n" +
              $"선택한 {d.Name}(으)로 바꿀까요?\n\n매핑은 유지되지만, 새 장치에 없는 입력은 검증 오류가 납니다.\n[아니요]를 누르면 장치 감지 없이 편집합니다.",
              $"The profile device is {_profile.Device.Name} [{_profile.Device.Vid}:{_profile.Device.Pid}].\n" +
              $"Switch to the selected {d.Name}?\n\nMappings are kept, but inputs the new device lacks will fail validation.\n[No] edits without live detection."),
            T("장치 변경", "Change device"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
            RemoveGameButton.IsEnabled = false;
            EditButton.Content = T("게임 매핑 편집", "Edit game mapping");
            TargetText.Text = T("게임을 선택하세요.", "Select a game.");
            SetSteamInputText(Brushes.Gray, "");
            return;
        }
        var game = _profile.FindGame(t.AppId);
        RemoveGameButton.IsEnabled = game != null;
        EditButton.Content = game == null ? T("이 게임 매핑 만들기", "Create mapping for this game") : T("게임 매핑 편집", "Edit game mapping");
        TargetText.Text = game == null
            ? $"{t.Name}: {T("게임 매핑 없음", "no game mapping")}"
            : $"{t.Name}: {T($"게임 매핑 {game.Bindings.Count}개", $"{game.Bindings.Count} mappings")}";
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
            if (mode == SteamInputMode.On) SetSteamInputText(Brushes.ForestGreen, $"✓ {label} → {T("게임 매핑이 적용됩니다.", "the game mapping is used.")}");
            else SetSteamInputText(Brushes.DarkOrange, $"⚠ {label} → " + T("이대로는 게임 매핑이 동작하지 않을 수 있습니다. [Steam에 적용]하면 '사용'으로 바꿉니다.",
                "the game mapping may not work as is. [Apply to Steam] turns it on."));
            return;
        }
        SetSteamInputText(Brushes.DimGray, $"{label} → {T("게임 매핑을 만들어 적용하면 '사용'으로 바꿉니다.", "it is turned on when you create and apply a game mapping.")}");
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
            MessageBox.Show(this, T("입력 장치를 연결하고 선택하세요.", "Connect and select an input device."), T("매핑 편집", "Edit mapping"));
            return;
        }

        var existing = _profile.FindGame(t.AppId);
        var game = existing ?? new GameMapping { AppId = t.AppId, GameName = t.Name };
        var editor = new MappingEditorWindow(_profile, game, device) { Owner = this };
        if (editor.ShowDialog() != true) return;
        game.Bindings = editor.Result;
        game.GameName = t.Name;
        if (existing == null) _profile.Games.Add(game);
        AppLog.Info(T($"게임 매핑 저장: {t.Name} ({game.Bindings.Count}개)", $"Game mapping saved: {t.Name} ({game.Bindings.Count})"));
        _lastEditedAppId = t.AppId;
        SaveProfile();
    }

    private void RemoveGameMapping_Click(object sender, RoutedEventArgs e)
    {
        var t = SelectedTarget;
        var game = t == null ? null : _profile.FindGame(t.AppId);
        if (game == null) return;
        if (MessageBox.Show(this, T($"{game.GameName}의 게임 매핑을 삭제할까요?\n\n[Steam에 적용]하면 이 앱이 만든 이 게임의 Steam 설정을 정리합니다.",
                    $"Delete the game mapping for {game.GameName}?\n\n[Apply to Steam] then cleans up the Steam config this app made for it."),
                T("게임 매핑 삭제", "Delete game mapping"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profile.Games.Remove(game);
        AppLog.Info(T($"게임 매핑 삭제: {game.GameName}", $"Game mapping deleted: {game.GameName}"));
        SaveProfile();
    }

    // ---------------- Steam 상태 ----------------

    private void RefreshSteamStatus()
    {
        if (_env == null) { SetStatus(Brushes.Gray, T("Steam 없음", "Steam not found"), ""); return; }
        var service = new SteamApplyService(_env, _backups);
        var registered = service.IsDeviceRegistered(_profile.Device);
        DisableButton.IsEnabled = registered;
        var registration = registered
            ? T("장치: Steam 일반 컨트롤러로 등록됨", "Device: registered as a Steam generic controller")
            : T("장치: Steam 일반 컨트롤러 아님 (조이스틱)", "Device: not a Steam generic controller (plain joystick)");
        if (_profile.Device.IsEmpty || _profile.Games.Count == 0)
        {
            SetStatus(Brushes.Gray, T("게임을 선택해 게임 매핑을 만드세요", "Select a game and create a game mapping"), _profile.Device.IsEmpty ? "" : registration);
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
                ? T("이 게임: Steam에 이 앱의 게임 매핑이 적용되어 있음", "This game: this app's game mapping is applied in Steam")
                : selection.Kind != "none"
                    ? T($"이 게임: Steam 자체 설정 사용 중 ({selection}) — 게임 매핑을 만들어 적용하면 대체됩니다",
                        $"This game: uses Steam's own config ({selection}) — replaced when you create and apply a game mapping")
                    : T("이 게임: Steam 게임별 설정 없음", "This game: no Steam per-game config"));
            if (_env.User != null && SteamInputConfigFinder.HasCloudConfig(_env.SteamPath, _env.User.AccountId, t.AppId))
                detail.AppendLine(T("⚠ Steam 클라우드 컨트롤러 설정 있음", "⚠ Steam Cloud controller config exists"));
        }
        detail.Append($"{T("컨트롤러 ID", "Controller ID")}: {plan.ControllerId ?? "-"}");

        if (!plan.Validation.IsValid)
        {
            SetStatus(Brushes.Firebrick, T($"검증 오류 {plan.Validation.Errors.Count}개 — 적용 전에 고쳐야 합니다",
                    $"{plan.Validation.Errors.Count} validation error(s) — fix them before applying"),
                string.Join("\n", plan.Validation.Errors.Take(3)) + "\n" + detail);
            return;
        }
        if (plan.HasChanges)
        {
            var parts = new List<string>();
            if (plan.LayoutChanged) parts.Add(T("장치 설정", "device layout"));
            var gameWrites = plan.Writes.Keys.Count(k => k.EndsWith($"{plan.ControllerId}.vdf", StringComparison.OrdinalIgnoreCase) && !k.Contains("configset_"));
            if (gameWrites > 0) parts.Add(T($"게임 매핑 {gameWrites}개", $"{gameWrites} game mapping(s)"));
            if (plan.Released.Count > 0) parts.Add(T($"정리 {plan.Released.Count}개", $"{plan.Released.Count} cleanup(s)"));
            if (plan.SteamInputEnabled.Count > 0) parts.Add(T($"Steam Input 켜기 {plan.SteamInputEnabled.Count}개", $"turn on Steam Input for {plan.SteamInputEnabled.Count}"));
            SetStatus(Brushes.Orange, T("Steam에 적용할 변경 있음", "Changes to apply to Steam") +
                                      $" ({string.Join(", ", parts.DefaultIfEmpty(T("설정 파일", "config files")))})", detail.ToString());
        }
        else
        {
            SetStatus(Brushes.LimeGreen, T("Steam과 일치 (프로필이 모두 적용되어 있음)", "In sync with Steam (the whole profile is applied)"), detail.ToString());
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
        catch (Exception ex) when (ex is VdfParseException or IOException) { text.AppendLine($"{T("config.vdf 분석 실패", "Could not analyze config.vdf")}: {ex.Message}"); }
        text.AppendLine(T("=== 장치 설정 (config.vdf SDL_GamepadBind, 모든 게임 공유) ===", "=== Device layout (config.vdf SDL_GamepadBind, shared by all games) ==="));
        text.AppendLine(layout == null ? T("(없음)", "(none)") : string.Join("\n", layout.ElementFields.Select(f => $"  {f.Key,-14} ← {f.Value}")));
        text.AppendLine();

        var t = SelectedTarget;
        if (t != null)
        {
            var target = SteamInputConfigFinder.Resolve(dir, device, t.AppId);
            text.AppendLine($"=== {t.Name}: configset {SteamInputConfigFinder.ReadSelection(target)} ===");
            var files = SteamInputConfigFinder.ExistingGameConfigs(dir, t.AppId);
            if (files.Count == 0) text.AppendLine(T("이 게임의 Steam Input 설정 파일이 없습니다.", "This game has no Steam Input config file."));
            foreach (var f in files)
            {
                text.AppendLine($"=== {Path.GetFileName(f)} ===");
                try { text.AppendLine(SteamInputConfigFinder.Analyze(VdfParser.ParseFile(f), Path.GetFileNameWithoutExtension(f), t.AppId)); }
                catch (Exception ex) when (ex is VdfParseException or IOException) { text.AppendLine($"{T("분석 실패", "Analysis failed")}: {ex.Message}"); }
            }
        }
        Dialogs.ShowText(this, T("Steam 설정 분석", "Steam settings analysis"), AppLog.Sanitize(text.ToString()));
    }

    // ---------------- 적용 / 비활성화 ----------------

    private async Task<bool> EnsureSteamClosedAsync(string action)
    {
        if (!SteamProcess.IsRunning()) return true;
        var answer = MessageBox.Show(this,
            T("Steam이 실행 중입니다.\n\nSteam이 설정을 다시 저장하면 현재 변경 사항이 덮어써질 수 있습니다.\n\n" +
              $"[예] Steam 종료 후 {action}\n[아니요] 취소",
              "Steam is running.\n\nSteam may overwrite the changes when it saves its settings again.\n\n" +
              $"[Yes] Close Steam and {action}\n[No] Cancel"),
            T("Steam 실행 중", "Steam is running"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return false;

        AppLog.Info(T("Steam 종료 요청 중...", "Asking Steam to close..."));
        var closed = await SteamProcess.ShutdownAsync(_env!.SteamPath, TimeSpan.FromSeconds(60));
        if (!closed)
        {
            AppLog.Error(T("Steam이 종료되지 않았습니다.", "Steam did not close."));
            MessageBox.Show(this, T("Steam이 60초 안에 종료되지 않았습니다. Steam을 직접 종료한 뒤 다시 시도하세요.",
                    "Steam did not close within 60 seconds. Close Steam yourself and try again."), "Steam",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        AppLog.Info(T("Steam 종료됨", "Steam closed"));
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
                MessageBox.Show(this, T("Steam 설정이 이미 프로필과 일치합니다.", "Steam already matches the profile."), T("Steam에 적용", "Apply to Steam"));
                return;
            }
            if (!ShowPlanAndConfirm(plan)) return;

            if (!await EnsureSteamClosedAsync(T("적용", "apply"))) return;
            // Steam은 종료하면서 config.vdf를 다시 저장하므로 종료 후 다시 생성/검증한다.
            plan = service.Prepare(_profile, throttleAppId);
            if (!plan.Validation.IsValid) { ShowValidationErrors(plan.Validation); return; }
            if (!plan.HasChanges) { RefreshSteamStatus(); return; }

            var backup = service.Execute(plan);
            RefreshAll();

            var restart = MessageBox.Show(this,
                T("Steam Input 설정을 적용했습니다.", "Steam Input settings applied.") + "\n\n" +
                $"{T("백업", "Backup")}: {Path.GetFileName(backup.Folder)}\n\n" +
                T("확인 사항:", "Notes:") + "\n" +
                (plan.SteamInputEnabled.Count > 0
                    ? T(" • Steam Input을 '사용'으로 바꾼 게임: ", " • Steam Input turned on for: ") + string.Join(", ", plan.SteamInputEnabled.Select(g => g.GameName)) + "\n"
                    : "") +
                T(" • 조이스틱을 직접 지원하는 게임(MSFS, DCS 등)은 Steam Input을 '사용 안 함'으로 두세요.\n",
                  " • For games with native joystick support (MSFS, DCS, ...), keep Steam Input off.\n") +
                (plan.Games.Any(g => g.CloudConfig)
                    ? T(" • Steam 클라우드가 이전 설정을 복원하면 다시 적용하세요.\n", " • If Steam Cloud restores an older config, apply again.\n")
                    : "") +
                T("\nSteam을 다시 시작할까요?", "\nStart Steam again?"),
                T("적용 완료", "Applied"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (restart == MessageBoxResult.Yes) SteamProcess.Start(_env.SteamPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{T("적용 실패", "Apply failed")}: {ex.Message}");
            MessageBox.Show(this, T("적용에 실패했습니다. 원본 설정을 복원했습니다.", "Apply failed. The original settings were restored.") + $"\n\n{AppLog.Sanitize(ex.Message)}",
                T("적용 실패", "Apply failed"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(this, T("이 조이스틱은 Steam에 일반 컨트롤러로 등록되어 있지 않습니다.", "This joystick is not registered as a Steam generic controller."),
                    T("Steam에서 비활성화", "Disable in Steam"));
                return;
            }
            if (!Dialogs.Confirm(this, T("Steam에서 비활성화", "Disable in Steam"),
                    T($"{_profile.Device.Name}의 Steam 일반 컨트롤러 등록(장치 설정)을 지웁니다.",
                      $"Removes the Steam generic controller registration (device layout) of {_profile.Device.Name}."),
                    T("• Steam에서는 이 장치가 일반 컨트롤러가 아닌 조이스틱으로 보입니다 (게임 매핑이 동작하지 않음).\n" +
                      "• 게임 매핑과 게임별 설정 파일은 그대로 둡니다. 다시 [Steam에 적용]하면 원래대로 동작합니다.\n" +
                      "• 변경 전 config.vdf는 자동 백업됩니다.",
                      "• Steam will see this device as a plain joystick, not a generic controller (game mappings stop working).\n" +
                      "• Game mappings and per-game config files are kept. [Apply to Steam] again restores it.\n" +
                      "• config.vdf is backed up automatically before the change."),
                    T("비활성화", "Disable"), T("취소", "Cancel"))) return;

            if (!await EnsureSteamClosedAsync(T("비활성화", "disable"))) return;
            plan = service.PrepareDisable(_profile); // Steam 종료 시 config.vdf가 다시 저장되므로
            if (!plan.Validation.IsValid) { ShowValidationErrors(plan.Validation); return; }
            if (!plan.HasChanges) { RefreshSteamStatus(); return; }

            var backup = service.Execute(plan);
            AppLog.Info(T("Steam 일반 컨트롤러 등록 해제", "Steam generic controller registration removed"));
            RefreshAll();
            if (MessageBox.Show(this, T("비활성화했습니다.", "Disabled.") + $"\n\n{T("백업", "Backup")}: {Path.GetFileName(backup.Folder)}\n\n" +
                                      T("Steam을 다시 시작할까요?", "Start Steam again?"),
                    T("비활성화 완료", "Disabled"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                SteamProcess.Start(_env.SteamPath);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{T("비활성화 실패", "Disable failed")}: {ex.Message}");
            MessageBox.Show(this, T("비활성화에 실패했습니다. 원본 설정을 복원했습니다.", "Disable failed. The original settings were restored.") +
                                  $"\n\n{AppLog.Sanitize(ex.Message)}", T("비활성화 실패", "Disable failed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowValidationErrors(Steam.ValidationResult v)
    {
        AppLog.Warn(T($"검증 실패: 오류 {v.Errors.Count}개", $"Validation failed: {v.Errors.Count} error(s)"));
        MessageBox.Show(this, T("검증에 실패하여 적용하지 않습니다.", "Validation failed, nothing was applied.") + "\n\n" +
                              string.Join("\n", v.Errors.Select(x => "• " + x)) +
                              (v.Warnings.Count > 0 ? "\n\n" + T("경고", "Warnings") + ":\n" + string.Join("\n", v.Warnings.Select(x => "• " + x)) : ""),
            T("설정 검증", "Validation"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool ShowPlanAndConfirm(ApplyPlan plan)
    {
        AppLog.Info("Validation OK");
        var body = new StringBuilder();
        body.AppendLine(T("변경할 파일 (변경 전 자동 백업됨):", "Files to change (backed up automatically first):"));
        foreach (var path in plan.Writes.Keys) body.AppendLine("  • " + path);
        foreach (var (_, path) in plan.Released) body.AppendLine($"  • {T("(정리, 백업 폴더로 이동)", "(cleanup, moved to the backup folder)")} {path}");
        body.AppendLine();
        if (plan.LayoutChanged)
        {
            body.AppendLine(T("⚠ 장치 설정(config.vdf)을 변경합니다: 게임 매핑에 쓰인 입력으로 이 조이스틱을 Steam 일반 컨트롤러로 등록합니다.",
                "⚠ Changes the device layout (config.vdf): registers this joystick as a Steam generic controller with the inputs your game mappings use."));
            body.AppendLine(T("  게임 매핑이 없는 게임에서는 이 장치 설정대로 동작합니다.", "  Games without a game mapping use this device layout as is."));
            body.AppendLine($"  {T("장치 설정", "Device layout")}: " + string.Join(", ", plan.Layout!.ElementFields.Select(f => $"{f.Key}:{f.Value}")));
            body.AppendLine();
        }
        if (plan.Games.Count > 0)
            body.AppendLine($"{T("게임 매핑", "Game mappings")}: " + string.Join(", ", plan.Games.Select(g => $"{g.GameName} ({g.AppId})")));
        if (plan.SteamInputEnabled.Count > 0)
            body.AppendLine(T("게임 속성 Steam Input → '사용' (localconfig.vdf): ", "Game properties Steam Input → on (localconfig.vdf): ") +
                            string.Join(", ", plan.SteamInputEnabled.Select(g => $"{g.GameName} ({T("현재", "now")} {SteamInputSetting.DisplayName(g.Previous)})")));
        var cloud = plan.Games.Where(g => g.CloudConfig).ToList();
        if (cloud.Count > 0)
        {
            body.AppendLine($"{T("⚠ Steam 클라우드 컨트롤러 설정이 있는 게임", "⚠ Games with a Steam Cloud controller config")}: {string.Join(", ", cloud.Select(g => g.GameName))}");
            body.AppendLine(T("  Steam 재실행 시 클라우드 설정이 복원될 수 있습니다 (앱은 클라우드 파일을 삭제하지 않습니다).",
                "  Steam may restore the cloud config when it restarts (this app does not delete cloud files)."));
        }
        body.AppendLine();
        body.AppendLine(T("정보:", "Details:"));
        foreach (var n in plan.Notes) body.AppendLine("  " + n);
        if (plan.Validation.Warnings.Count > 0)
        {
            body.AppendLine();
            body.AppendLine(T("경고:", "Warnings:"));
            foreach (var w in plan.Validation.Warnings) body.AppendLine("  • " + w);
        }
        return Dialogs.Confirm(this, T("Steam에 적용", "Apply to Steam"),
            T($"'{plan.Profile.ProfileName}' 프로필 전체를 Steam에 적용합니다.", $"Applies the whole '{plan.Profile.ProfileName}' profile to Steam."),
            AppLog.Sanitize(body.ToString()), T("적용", "Apply"), T("취소", "Cancel"));
    }
}
