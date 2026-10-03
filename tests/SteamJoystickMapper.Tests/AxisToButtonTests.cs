using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Tests;

/// <summary>축 한쪽 방향 → 버튼 (트위스트 −/+ → LB/RB): 게임별 설정에서 스틱을 방향 패드 모드로 바꾸고 데드존을 쓴다.</summary>
public class AxisToButtonTests : IDisposable
{
    private const uint Account = 123;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sjm-a2b-" + Guid.NewGuid().ToString("N"));
    private readonly SteamApplyService _service;
    private readonly string _configDir;

    public AxisToButtonTests()
    {
        var steam = Path.Combine(_root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        File.WriteAllText(Path.Combine(steam, "config", "config.vdf"), "\"InstallConfigStore\"\n{\n\t\"SDL_GamepadBind\"\t\t\"\"\n}\n");
        _configDir = SteamInputConfigFinder.ControllerConfigDir(steam, Account);
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(Path.Combine(_configDir, "configset_45e-2e3-99860a.vdf"), "\"controller_config\"\n{\n}\n"); // 접미사 출처
        var localConfig = SteamInputSetting.LocalConfigPath(steam, Account);
        Directory.CreateDirectory(Path.GetDirectoryName(localConfig)!);
        File.WriteAllText(localConfig, "\"UserLocalConfigStore\"\n{\n}\n");
        var user = new SteamUser(Account, "76561197960265851", "", true);
        var env = new SteamEnvironment { SteamPath = steam, Users = new[] { user }, User = user };
        _service = new SteamApplyService(env, new BackupManager(Path.Combine(_root, "backup")), () => false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MapperProfile Profile() => MapperProfile.CreateDefault(Fixtures.TA320);

    /// <summary>AC8 프리셋에서 트위스트를 스틱 대신 LB(−)/RB(+)로.</summary>
    private static GameMapping TwistBumpers(string appId = "1000", int deadZone = 40)
    {
        var bindings = Presets.AceCombatFlightStick(Fixtures.TA320).Where(b => b.Source.Axis != JoyAxis.Rz).ToList();
        bindings.Add(new MappingBinding { Target = XboxOutput.LB, Source = PhysicalInput.FromAxisHalf(JoyAxis.Rz, -1), DeadZone = deadZone });
        bindings.Add(new MappingBinding { Target = XboxOutput.RB, Source = PhysicalInput.FromAxisHalf(JoyAxis.Rz, +1), DeadZone = deadZone });
        return new GameMapping { AppId = appId, GameName = "Game " + appId, Bindings = bindings };
    }

    private VdfNode GameGroupFor(string source, out VdfNode mappings)
    {
        mappings = VdfParser.ParseFile(Path.Combine(_configDir, "1000", "controller_generic.vdf")).Get("controller_mappings")!;
        var gsb = mappings.Get("preset")!.Get("group_source_bindings")!;
        var id = gsb.Children!.Single(e => e.Value == $"{source} active").Key;
        return mappings.GetAll("group").Single(g => g.GetValue("id") == id);
    }

    private static string? Binding(VdfNode group, string input) =>
        group.Find("inputs", input, "activators", "Full_Press", "bindings")?.GetValue("binding");

    [Fact]
    public void TwistHalves_BecomeDpadModeWithDeadZone()
    {
        var p = Profile();
        p.Games.Add(TwistBumpers());
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("a2", plan.Layout!.Get("rightx")); // 트위스트는 장치 설정의 스틱 빈 자리에 자동으로
        _service.Execute(plan);

        var group = GameGroupFor("right_joystick", out _);
        Assert.Equal("dpad", group.GetValue("mode"));
        Assert.Equal("xinput_button shoulder_left", Binding(group, "dpad_west"));
        Assert.Equal("xinput_button shoulder_right", Binding(group, "dpad_east"));
        Assert.Equal((40 * 32767 / 100).ToString(), group.Find("settings")!.GetValue("deadzone"));
        Assert.Equal("0", group.Find("settings")!.GetValue("requires_click"));
    }

    [Fact]
    public void InvertedTwistInLayout_SwapsDirections()
    {
        var p = Profile();
        p.Games.Add(TwistBumpers());
        var other = Presets.AceCombatFlightStick(Fixtures.TA320);
        other.First(b => b.Source.Axis == JoyAxis.Rz).Invert = true; // 다른 게임이 트위스트를 반전된 스틱으로 씀
        p.Games.Add(new GameMapping { AppId = "2000", GameName = "Other", Bindings = other });
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("a2~", plan.Layout!.Get("rightx"));
        _service.Execute(plan);
        var group = GameGroupFor("right_joystick", out _);
        // 장치 설정에서 반전된 축이면 Steam이 보는 방향이 반대 → 물리적 왼쪽(−)은 dpad_east
        Assert.Equal("xinput_button shoulder_left", Binding(group, "dpad_east"));
        Assert.Equal("xinput_button shoulder_right", Binding(group, "dpad_west"));
    }

    [Fact]
    public void BackToStick_ReplacesDpadGroupWithoutLeavingIt()
    {
        var p = Profile();
        p.Games.Add(TwistBumpers());
        _service.Execute(_service.Prepare(p));

        p.Games[0].Bindings = Presets.AceCombatFlightStick(Fixtures.TA320);
        _service.Execute(_service.Prepare(p));
        var group = GameGroupFor("right_joystick", out var m);
        Assert.Equal("joystick_move", group.GetValue("mode"));
        Assert.DoesNotContain(m.GetAll("group"), g => g.GetValue("mode") == "dpad" && Binding(g, "dpad_west") == "xinput_button shoulder_left");
    }

    [Fact]
    public void SameStickAsStickAndButton_IsRejected()
    {
        var p = Profile();
        var game = TwistBumpers();
        game.Bindings.Add(new MappingBinding { Target = XboxOutput.RightStickX, Source = PhysicalInput.FromAxis(JoyAxis.Rz) });
        p.Games.Add(game);
        Assert.False(_service.Prepare(p).Validation.IsValid);
    }

    [Fact]
    public void HalfAxis_RoundTripsInProfileJson()
    {
        var p = Profile();
        p.Games.Add(TwistBumpers());
        var file = Path.Combine(_root, "profile.json");
        new ProfileStore(file).Save(p);
        var loaded = ProfileStore.Read(file);
        var lb = loaded.Games[0].Bindings.Single(b => b.Target == XboxOutput.LB);
        Assert.Equal(PhysicalInput.FromAxisHalf(JoyAxis.Rz, -1), lb.Source);
        Assert.Equal(40, lb.DeadZone);
        Assert.Equal("Twist (Rz) −", lb.Source.DisplayName);
    }
}

/// <summary>쓰로틀 하나 → LT(아래쪽 절반)/RT(위쪽 절반), 가운데 데드존 = Steam 트리거 범위 시작.</summary>
public class ThrottleToTriggersTests : IDisposable
{
    private const uint Account = 123;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sjm-thr-" + Guid.NewGuid().ToString("N"));
    private readonly SteamApplyService _service;
    private readonly string _configDir;

    public ThrottleToTriggersTests()
    {
        var steam = Path.Combine(_root, "steam");
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        File.WriteAllText(Path.Combine(steam, "config", "config.vdf"), "\"InstallConfigStore\"\n{\n\t\"SDL_GamepadBind\"\t\t\"\"\n}\n");
        _configDir = SteamInputConfigFinder.ControllerConfigDir(steam, Account);
        Directory.CreateDirectory(_configDir);
        var localConfig = SteamInputSetting.LocalConfigPath(steam, Account);
        Directory.CreateDirectory(Path.GetDirectoryName(localConfig)!);
        File.WriteAllText(localConfig, "\"UserLocalConfigStore\"\n{\n}\n");
        var user = new SteamUser(Account, "76561197960265851", "", true);
        var env = new SteamEnvironment { SteamPath = steam, Users = new[] { user }, User = user };
        _service = new SteamApplyService(env, new BackupManager(Path.Combine(_root, "backup")), () => false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ThrottleRanges_BecomeTriggerRangeStartEnd()
    {
        // 사용자 예: LT − 10~20% (10%에서 최대), RT + 40~100% (100%에서 최대)
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var bindings = Presets.AceCombatFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), RangeLow = 10, RangeHigh = 20 });
        bindings.Add(new MappingBinding { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 40, RangeHigh = 100 });
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });

        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        // 쓰로틀(4번째 축 = a3) 전체를 트리거 자리에: RT는 그대로, LT는 뒤집어서(0%쪽이 최대)
        Assert.Equal("a3~", plan.Layout!.Get("lefttrigger"));
        Assert.Equal("a3", plan.Layout.Get("righttrigger"));
        _service.Execute(plan);

        var lt = Group("left_trigger").Find("settings")!;
        var rt = Group("right_trigger").Find("settings")!;
        Assert.Equal("1", lt.GetValue("output_trigger"));
        Assert.Equal("2", rt.GetValue("output_trigger"));
        // LT: 뒤집힌 값 80~90% 구간 = 쓰로틀 20~10%
        Assert.Equal((80 * 32767 / 100).ToString(), lt.GetValue("deadzone_inner_radius"));
        Assert.Equal((90 * 32767 / 100).ToString(), lt.GetValue("deadzone_outer_radius"));
        // RT: 40~100%
        Assert.Equal((40 * 32767 / 100).ToString(), rt.GetValue("deadzone_inner_radius"));
        Assert.Equal("32000", rt.GetValue("deadzone_outer_radius")); // 끝까지 닿으면 Steam 기본값
        // Steam 기본 트리거 설정과 같이 "끝까지 당기기"도 같은 트리거
        string? Click(string source) => Group(source).Find("inputs", "click", "activators", "Full_Press", "bindings")?.GetValue("binding");
        Assert.Equal("xinput_button TRIGGER_LEFT", Click("left_trigger"));
        Assert.Equal("xinput_button TRIGGER_RIGHT", Click("right_trigger"));
    }

    [Fact]
    public void OldHalfAxisDeadZone_ConvertsToRange()
    {
        // 이전 방식(가운데 기준 데드존 20%)으로 저장된 매핑은 LT 0~40%, RT 60~100%로 읽힌다
        var lt = new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), DeadZone = 20 };
        var rt = new MappingBinding { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), DeadZone = 20 };
        Assert.Equal((0, 40), lt.TriggerRange());
        Assert.Equal((60, 100), rt.TriggerRange());
    }

    [Fact]
    public void InvalidRange_IsRejected()
    {
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        p.Games.Add(new GameMapping
        {
            AppId = "1000", GameName = "G",
            Bindings = { new() { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 70, RangeHigh = 60 } },
        });
        Assert.Contains(SteamConfigValidator.ValidateMapper(p).Errors, e => e.Contains("감지 범위"));
    }

    private VdfNode Group(string source)
    {
        var m = VdfParser.ParseFile(Path.Combine(_configDir, "1000", "controller_generic.vdf")).Get("controller_mappings")!;
        var gsb = m.Get("preset")!.Get("group_source_bindings")!;
        return m.GetAll("group").Single(g => g.GetValue("id") == gsb.Children!.Single(e => e.Value == $"{source} active").Key);
    }

    [Fact]
    public void ThrottleAndButton_BothDriveLT()
    {
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var bindings = Presets.AceCombatFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), DeadZone = 20 });
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromButton(4) }); // 버튼 5도 LT
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });

        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("a3~", plan.Layout!.Get("lefttrigger")); // 트리거 자리는 쓰로틀
        var buttonSlot = plan.Layout.ElementFields.Single(f => f.Value == "b4").Key; // 버튼 5는 다른 버튼 자리
        Assert.DoesNotContain("trigger", buttonSlot);
        _service.Execute(plan);

        var text = File.ReadAllText(Path.Combine(_configDir, "1000", "controller_generic.vdf"));
        Assert.Contains("xinput_button TRIGGER_LEFT", text); // 버튼 → LT 끝까지 누름
        var m = VdfParser.Parse(text).Get("controller_mappings")!;
        var gsb = m.Get("preset")!.Get("group_source_bindings")!;
        var lt = m.GetAll("group").Single(g => g.GetValue("id") == gsb.Children!.Single(e => e.Value == "left_trigger active").Key);
        Assert.Equal("1", lt.Find("settings")!.GetValue("output_trigger")); // 쓰로틀 → LT 아날로그
    }

    [Fact]
    public void TwoAxesOnOneTrigger_IsRejected()
    {
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        p.Games.Add(new GameMapping
        {
            AppId = "1000", GameName = "G", Bindings =
            {
                new() { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1) },
                new() { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Rz, -1) },
            },
        });
        Assert.Contains(SteamConfigValidator.ValidateMapper(p).Errors, e => e.Contains("아날로그 축이 여러 개"));
    }

    [Fact]
    public void ThrottleHalfToStickOutput_IsRejected()
    {
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        p.Games.Add(new GameMapping
        {
            AppId = "1000", GameName = "G",
            Bindings = { new() { Target = XboxOutput.LeftStickX, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1) } },
        });
        Assert.Contains(SteamConfigValidator.ValidateMapper(p).Errors, e => e.Contains("한쪽 방향"));
    }
}
