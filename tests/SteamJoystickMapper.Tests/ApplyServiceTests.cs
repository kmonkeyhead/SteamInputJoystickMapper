using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Tests;

/// <summary>가짜 Steam 폴더에서 프로필 전체 적용(자동 장치 설정 + 게임별)을 끝까지 검증한다.</summary>
public class ApplyServiceTests : IDisposable
{
    private const uint Account = 123;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sjm-apply-" + Guid.NewGuid().ToString("N"));
    private readonly SteamEnvironment _env;
    private readonly SteamApplyService _service;
    private readonly string _configDir;
    private readonly string _localConfig;

    // 2000은 사용자가 Steam Input을 끈 상태 (0), 이스케이프된 JSON 값은 그대로 보존되어야 한다
    private const string LocalConfigText =
        @"""UserLocalConfigStore""
{
	""Json""		""{\""a\"":\""x\/y\""}""
	""apps""
	{
		""2000""
		{
			""UseSteamControllerConfig""		""0""
		}
	}
}
";

    public ApplyServiceTests()
    {
        var steam = Path.Combine(_root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        File.WriteAllText(Path.Combine(steam, "config", "config.vdf"),
            "\"InstallConfigStore\"\n{\n\t\"Software\"\n\t{\n\t}\n\t\"SDL_GamepadBind\"\t\t\"03000000de280000ff11000001000000,Steam Virtual Gamepad,a:b0,platform:Windows,\"\n}\n");
        _configDir = SteamInputConfigFinder.ControllerConfigDir(steam, Account);
        Directory.CreateDirectory(_configDir);
        // 접미사 출처: 같은 계정의 다른 장치 configset
        File.WriteAllText(Path.Combine(_configDir, "configset_45e-2e3-99860a.vdf"), "\"controller_config\"\n{\n}\n");

        _localConfig = SteamInputSetting.LocalConfigPath(steam, Account);
        Directory.CreateDirectory(Path.GetDirectoryName(_localConfig)!);
        File.WriteAllText(_localConfig, LocalConfigText);

        var user = new SteamUser(Account, "76561197960265851", "", true);
        _env = new SteamEnvironment { SteamPath = steam, Users = new[] { user }, User = user };
        _service = new SteamApplyService(_env, new BackupManager(Path.Combine(_root, "backup")), () => false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MapperProfile Profile() => MapperProfile.CreateDefault(Fixtures.TA320);

    /// <summary>AC8 프리셋에서 시작해 edit으로 바꾼 게임 매핑.</summary>
    private static GameMapping Game(string appId, Action<List<MappingBinding>> edit)
    {
        var g = new GameMapping { AppId = appId, GameName = "Game " + appId, Bindings = Presets.BasicFlightStick(Fixtures.TA320) };
        edit(g.Bindings);
        return g;
    }

    private string GameFile(string appId) => Path.Combine(_configDir, appId, "controller_generic.vdf");
    private string ConfigsetFile => Path.Combine(_configDir, "configset_controller_generic.vdf");

    private string? Binding(string appId, string groupMode, string input)
    {
        var m = VdfParser.ParseFile(GameFile(appId)).Get("controller_mappings")!;
        var gsb = m.Get("preset")!.Get("group_source_bindings")!;
        var active = gsb.Children!.Where(e => e.Value!.EndsWith(" active")).Select(e => e.Key).ToHashSet();
        return m.GetAll("group").Where(g => active.Contains(g.GetValue("id")!) && g.GetValue("mode") == groupMode)
            .Select(g => g.Find("inputs", input, "activators", "Full_Press", "bindings")?.GetValue("binding"))
            .FirstOrDefault(b => b != null);
    }

    [Fact]
    public void NoGameMappings_IsRejected()
    {
        var plan = _service.Prepare(Profile());
        Assert.False(plan.Validation.IsValid);
        Assert.Contains(plan.Validation.Errors, e => e.Contains("게임 매핑이 없습니다"));
    }

    [Fact]
    public void FirstGame_RegistersDeviceLayout_AndWritesGameFile()
    {
        var p = Profile();
        p.Games.Add(Game("1000", _ => { }));
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.True(plan.LayoutChanged);
        _service.Execute(plan);

        var layout = SteamConfigGenerator.ReadDeviceLayout(VdfParser.ParseFile(_env.GlobalConfigPath), Fixtures.TA320)!;
        Assert.Equal("b0", layout.Get("b")); // 처음 쓰인 출력의 자연스러운 자리
        Assert.Contains("Steam Virtual Gamepad", File.ReadAllText(_env.GlobalConfigPath)); // 다른 장치 보존
        Assert.True(SteamConfigGenerator.IsManaged(VdfParser.ParseFile(GameFile("1000"))));
        Assert.Equal("1", VdfParser.ParseFile(ConfigsetFile).Find("controller_config", "1000", "autosave")!.Value);
        Assert.Equal(SteamInputMode.On, SteamInputSetting.Read(_localConfig, "1000"));
        Assert.Equal(SteamInputMode.Off, SteamInputSetting.Read(_localConfig, "2000")); // 매핑 없는 게임은 그대로
        Assert.False(_service.Prepare(p).HasChanges); // 멱등
    }

    [Fact]
    public void GamesWithDifferentInputs_EachGetOwnMapping()
    {
        // 사용자 예시: 게임 A는 버튼1/2/3 → A/X/Y, 게임 B는 버튼4 → B, 버튼1 → Y
        var p = Profile();
        p.Games.Add(new GameMapping
        {
            AppId = "1000", GameName = "A", Bindings =
            {
                new() { Source = PhysicalInput.FromButton(0), Target = XboxOutput.A },
                new() { Source = PhysicalInput.FromButton(1), Target = XboxOutput.X },
                new() { Source = PhysicalInput.FromButton(2), Target = XboxOutput.Y },
            },
        });
        p.Games.Add(new GameMapping
        {
            AppId = "2000", GameName = "B", Bindings =
            {
                new() { Source = PhysicalInput.FromButton(3), Target = XboxOutput.B },
                new() { Source = PhysicalInput.FromButton(0), Target = XboxOutput.Y },
            },
        });
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("b0", plan.Layout!.Get("a"));
        Assert.Equal("b1", plan.Layout.Get("x"));
        Assert.Equal("b2", plan.Layout.Get("y"));
        Assert.Equal("b3", plan.Layout.Get("b"));
        _service.Execute(plan);

        Assert.Equal("xinput_button A", Binding("1000", "four_buttons", "button_a"));
        Assert.Null(Binding("1000", "four_buttons", "button_b")); // 버튼4: 게임 A에서는 동작 안 함
        Assert.Equal("xinput_button Y", Binding("2000", "four_buttons", "button_a")); // 버튼1: 게임 B에서는 Y
        Assert.Equal("xinput_button B", Binding("2000", "four_buttons", "button_b"));
        Assert.Null(Binding("2000", "four_buttons", "button_x"));
    }

    [Fact]
    public void AddingGame_KeepsExistingSlots()
    {
        var p = Profile();
        p.Games.Add(Game("1000", b => b.First(x => x.Target == XboxOutput.B).Target = XboxOutput.RB));
        _service.Execute(_service.Prepare(p));
        var configBefore = File.ReadAllText(_env.GlobalConfigPath);

        // 같은 입력만 쓰는 두 번째 게임: 장치 설정은 그대로, 게임 파일만 추가
        p.Games.Add(Game("2000", b => b.First(x => x.Target == XboxOutput.A).Target = XboxOutput.LB));
        var second = _service.Prepare(p);
        Assert.False(second.LayoutChanged);
        Assert.DoesNotContain(GameFile("1000"), second.Writes.Keys);
        _service.Execute(second);
        Assert.Equal(configBefore, File.ReadAllText(_env.GlobalConfigPath));
        Assert.Equal(SteamInputMode.On, SteamInputSetting.Read(_localConfig, "2000"));
        Assert.Contains(@"""{\""a\"":\""x\/y\""}""", File.ReadAllText(_localConfig)); // 다른 값은 바이트 그대로
        Assert.False(_service.Prepare(p).HasChanges);
    }

    [Fact]
    public void NewInput_ExtendsLayout_AndRegeneratesGames()
    {
        var p = Profile();
        p.Games.Add(Game("1000", _ => { }));
        _service.Execute(_service.Prepare(p));

        p.Games.Add(Game("2000", b => b.Add(new MappingBinding { Source = PhysicalInput.FromButton(9), Target = XboxOutput.Menu })));
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.True(plan.LayoutChanged);
        Assert.Equal("b9", plan.Layout!.Get("start"));
        _service.Execute(plan);
        Assert.Equal("xinput_button start", Binding("2000", "switches", "button_escape"));
        Assert.Null(Binding("1000", "switches", "button_escape")); // 게임 1000에서는 버튼10 동작 안 함
    }

    [Fact]
    public void RemovingGameMapping_ReleasesItWithoutDeleting()
    {
        var p = Profile();
        p.Games.Add(Game("1000", _ => { }));
        p.Games.Add(Game("2000", _ => { }));
        _service.Execute(_service.Prepare(p));
        Assert.True(File.Exists(GameFile("1000")));

        p.Games.RemoveAll(g => g.AppId == "1000");
        var plan = _service.Prepare(p);
        Assert.Single(plan.Released);
        var backup = _service.Execute(plan);

        Assert.False(File.Exists(GameFile("1000")));
        Assert.Null(VdfParser.ParseFile(ConfigsetFile).Find("controller_config", "1000"));
        Assert.Single(Directory.GetFiles(Path.Combine(backup.Folder, "released")));
        Assert.False(_service.Prepare(p).HasChanges);

        // Steam이 재시작하며 클라우드에서 같은 파일을 되살려도 configset이 가리키지 않으므로 정리 대상이 아니다
        File.Copy(Directory.GetFiles(Path.Combine(backup.Folder, "released"))[0], GameFile("1000"));
        Assert.False(_service.Prepare(p).HasChanges);
    }

    [Fact]
    public void InvertMustMatchAcrossGames()
    {
        var p = Profile();
        p.Games.Add(Game("1000", b => b.First(x => x.Target == XboxOutput.LeftStickY).Invert = true));
        p.Games.Add(Game("2000", _ => { }));
        var plan = _service.Prepare(p);
        Assert.Contains(plan.Validation.Errors, e => e.Contains("축 반전") && e.Contains("Game 1000"));

        p.Games[1].Bindings.First(x => x.Target == XboxOutput.LeftStickY).Invert = true;
        plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("a1~", plan.Layout!.Get("lefty"));
    }

    [Fact]
    public void Disable_RemovesDeviceLayoutOnly()
    {
        var p = Profile();
        p.Games.Add(Game("1000", _ => { }));
        _service.Execute(_service.Prepare(p));
        Assert.True(_service.IsDeviceRegistered(p.Device));

        var plan = _service.PrepareDisable(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        _service.Execute(plan);
        Assert.False(_service.IsDeviceRegistered(p.Device));
        Assert.Contains("Steam Virtual Gamepad", File.ReadAllText(_env.GlobalConfigPath)); // 다른 장치 보존
        Assert.True(File.Exists(GameFile("1000"))); // 게임 설정은 그대로
        Assert.False(_service.PrepareDisable(p).HasChanges); // 이미 비활성

        // 다시 적용하면 장치 설정만 되살아난다
        var again = _service.Prepare(p);
        Assert.True(again.LayoutChanged);
        Assert.Single(again.Writes);
    }
}

public class ProfileStoreTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "sjm-profile-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    [Fact]
    public void LoadOrCreate_DefaultProfile_WithEmptyDevice_RoundTrips()
    {
        var store = new ProfileStore(_file);
        var created = store.LoadOrCreate();
        Assert.Equal(MapperProfile.DefaultName, created.ProfileName);
        Assert.True(created.Device.IsEmpty);

        created.Device = Fixtures.TA320;
        created.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = Presets.BasicFlightStick(Fixtures.TA320) });
        store.Save(created);

        var loaded = store.LoadOrCreate();
        Assert.Single(loaded.Games);
        Assert.Equal(PhysicalInput.FromPov(0, PovDirection.Up), loaded.Games[0].Bindings.First(b => b.Target == XboxOutput.DPadUp).Source);
        Assert.DoesNotContain("vidValue", File.ReadAllText(_file));
    }

    [Fact]
    public void OldFormatWithGlobal_LoadsGamesAndDropsGlobal()
    {
        File.WriteAllText(_file, """
            { "formatVersion": 2, "profileName": "P", "device": { "name": "T.A320 Pilot", "vid": "044F", "pid": "0405" },
              "global": [ { "target": "A", "source": { "kind": "Button", "button": 1 } } ],
              "games": [ { "appId": "1000", "gameName": "G", "bindings": [ { "target": "B", "source": { "kind": "Button", "button": 0 } } ] } ] }
            """);
        var loaded = ProfileStore.Read(_file);
        Assert.Equal(3, loaded.FormatVersion);
        Assert.Single(loaded.Games);
        new ProfileStore(_file).Save(loaded);
        Assert.DoesNotContain("global", File.ReadAllText(_file));
    }
}
