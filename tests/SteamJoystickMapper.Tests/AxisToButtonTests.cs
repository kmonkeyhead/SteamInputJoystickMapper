using SteamJoystickMapper.Devices;
using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Tests;

/// <summary>축 한쪽 방향 → 버튼 (트위스트 −/+ → LB/RB): 장치 설정에서 반쪽 축을 바로 버튼 자리에 둔다.</summary>
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
        var bindings = Presets.BasicFlightStick(Fixtures.TA320).Where(b => b.Source.Axis != JoyAxis.Rz).ToList();
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
    public void TwistHalves_GoStraightToBumperSlots()
    {
        // 트위스트 − → LB, + → RB: 장치 설정에서 반쪽 축을 바로 LB/RB 자리에 (Steam 테스트 화면에서도 LB/RB)
        var p = Profile();
        p.Games.Add(TwistBumpers());
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("-a2", plan.Layout!.Get("leftshoulder"));
        Assert.Equal("+a2", plan.Layout.Get("rightshoulder"));
        Assert.Null(plan.Layout.Get("rightx"));
        _service.Execute(plan);

        var sw = GameGroupFor("switch", out var m);
        Assert.Equal("xinput_button shoulder_left", Binding(sw, "left_bumper"));
        Assert.Equal("xinput_button shoulder_right", Binding(sw, "right_bumper"));
        Assert.DoesNotContain(m.GetAll("group"), g => g.GetValue("mode") == "dpad" && Binding(g, "dpad_west") == "xinput_button shoulder_left");
    }

    [Fact]
    public void SameNameLayoutUnderOtherFileName_IsOverwrittenToo()
    {
        // 이전 버전이 다른 이름(44f-405-...)으로 만든 같은 제목의 레이아웃 → "내 레이아웃"에 같은 이름이 2개.
        // 적용하면 둘 다 같은 내용이 된다. 앱이 만들지 않은 파일(제목이 같아도)은 건드리지 않는다.
        var p = Profile();
        p.Games.Add(TwistBumpers());
        _service.Execute(_service.Prepare(p));
        var dir = Path.Combine(_configDir, "1000");
        var main = Path.Combine(dir, "controller_generic.vdf");
        var old = Path.Combine(dir, "44f-405-99860a.vdf");
        File.WriteAllText(old, File.ReadAllText(main).Replace("xinput_button shoulder_left", "xinput_button A"));
        var foreign = Path.Combine(dir, "45e-2e3-99860a.vdf");
        File.WriteAllText(foreign, "\"controller_mappings\"\n{\n\t\"title\"\t\t\"" + p.ProfileName + "\"\n}\n");

        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Contains(plan.Notes, n => n.Contains("44f-405-99860a.vdf"));
        _service.Execute(plan);
        Assert.Equal(File.ReadAllText(main), File.ReadAllText(old));
        Assert.DoesNotContain("xinput_button", File.ReadAllText(foreign));
    }

    [Fact]
    public void TwistAsStickInAnotherGame_IsRejected()
    {
        // 반쪽 축(버튼)과 축 전체(스틱)를 함께 쓰면 Steam이 한쪽을 무시하므로 오류
        var p = Profile();
        p.Games.Add(TwistBumpers());
        p.Games.Add(new GameMapping { AppId = "2000", GameName = "Other", Bindings = Presets.BasicFlightStick(Fixtures.TA320) });
        Assert.Contains(SteamConfigValidator.ValidateMapper(p).Errors, e => e.Contains("스틱으로 함께 쓸 수 없습니다") && e.Contains("Other"));
    }

    [Fact]
    public void TwistToOtherButtons_UsesFreeButtonSlots()
    {
        // 트위스트 − → X 인데 X 자리를 버튼이 이미 쓰면 빈 버튼 자리에 반쪽 축을 두고 게임별 설정에서 X로
        var p = Profile();
        var game = new GameMapping
        {
            AppId = "1000", GameName = "G", Bindings =
            {
                new() { Target = XboxOutput.X, Source = PhysicalInput.FromButton(2) },
                new() { Target = XboxOutput.X, Source = PhysicalInput.FromAxisHalf(JoyAxis.Rz, -1) },
            },
        };
        p.Games.Add(game);
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        var slot = plan.Layout!.ElementFields.Single(f => f.Value == "-a2").Key;
        Assert.DoesNotContain("trigger", slot);
        Assert.NotEqual("x", slot);
        var (source, input) = SteamLayoutPlanner.ButtonSlot(slot)!.Value;
        _service.Execute(plan);
        Assert.Equal("xinput_button X", Binding(GameGroupFor(source, out _), input));
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
    public void ThrottleOnBothTriggers_IsSplitAtCenter()
    {
        // 쓰로틀을 LT(−)·RT(+) 둘 다에: Steam 자체 항목과 같이 가운데에서 반쪽 축 +/− 로 나눔
        // LT − 10~20% (10%에서 최대), RT + 60~100% (100%에서 최대)
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var bindings = Presets.BasicFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), RangeLow = 10, RangeHigh = 20 });
        bindings.Add(new MappingBinding { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 60, RangeHigh = 100 });
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });

        var plan = _service.Prepare(p, "1000");
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("-a3", plan.Layout!.Get("lefttrigger"));
        Assert.Equal("+a3", plan.Layout.Get("righttrigger"));
        _service.Execute(plan);

        var lt = Group("left_trigger").Find("settings")!;
        var rt = Group("right_trigger").Find("settings")!;
        Assert.Equal("1", lt.GetValue("output_trigger"));
        Assert.Equal("2", rt.GetValue("output_trigger"));
        // LT: 반쪽 축 값 = (50 - 쓰로틀%) * 2 → 쓰로틀 20~10% = 60~80%
        Assert.Equal((60 * 32767 / 100).ToString(), lt.GetValue("deadzone_inner_radius"));
        Assert.Equal((80 * 32767 / 100).ToString(), lt.GetValue("deadzone_outer_radius"));
        // RT: 반쪽 축 값 = (쓰로틀% - 50) * 2 → 60~100% = 20~100%, 끝은 Steam 기본값
        Assert.Equal((20 * 32767 / 100).ToString(), rt.GetValue("deadzone_inner_radius"));
        Assert.Equal("32000", rt.GetValue("deadzone_outer_radius"));
        // Steam 기본 트리거 설정과 같이 "끝까지 당기기"도 같은 트리거
        string? Click(string source) => Group(source).Find("inputs", "click", "activators", "Full_Press", "bindings")?.GetValue("binding");
        Assert.Equal("xinput_button TRIGGER_LEFT", Click("left_trigger"));
        Assert.Equal("xinput_button TRIGGER_RIGHT", Click("right_trigger"));
    }

    [Fact]
    public void ThrottleOnOneTriggerPlus_UsesWholeAxis_AnyRange()
    {
        // 쓰로틀을 + 로만 (RT 40~100%): 축 전체 "a3" (확인된 형식), 범위 제한 없음
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var bindings = Presets.BasicFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 40, RangeHigh = 100 });
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });
        var plan = _service.Prepare(p, "1000");
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("a3", plan.Layout!.Get("righttrigger"));
        _service.Execute(plan);
        var rt = Group("right_trigger").Find("settings")!;
        Assert.Equal((40 * 32767 / 100).ToString(), rt.GetValue("deadzone_inner_radius"));
        Assert.Equal("32000", rt.GetValue("deadzone_outer_radius"));
    }

    [Fact]
    public void SplitThrottle_RangesOutsideHalves_AreRejected()
    {
        // 쓰로틀을 − 로도 쓰면 반으로 나뉘므로 − 0~60, + 40~100 은 오류
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        p.Games.Add(new GameMapping
        {
            AppId = "1000", GameName = "G", Bindings =
            {
                new() { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), RangeLow = 0, RangeHigh = 60 },
                new() { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 40, RangeHigh = 100 },
            },
        });
        var v = SteamConfigValidator.ValidateMapper(p);
        Assert.Contains(v.Errors, e => e.Contains("0~50% 안"));
        Assert.Contains(v.Errors, e => e.Contains("50~100% 안"));
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
        var bindings = Presets.BasicFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), DeadZone = 20 });
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromButton(4) }); // 버튼 5도 LT
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });

        var plan = _service.Prepare(p, "1000");
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("a3~", plan.Layout!.Get("lefttrigger")); // 쓰로틀 매핑 1개(−): 축 전체를 뒤집어서
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
    public void ButtonToLT_UsesTriggerSlot_WhenFree()
    {
        // 버튼 14 → LT: 트리거 자리가 비어 있으면 장치 설정에서부터 LT 자리 (누르면 LT 최대, Steam 테스트 화면에서도 LT)
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var bindings = Presets.BasicFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromButton(13) });
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Equal("b13", plan.Layout!.Get("lefttrigger"));
        _service.Execute(plan);
        var lt = Group("left_trigger");
        Assert.Equal("1", lt.Find("settings")!.GetValue("output_trigger"));
        Assert.Equal("xinput_button TRIGGER_LEFT", lt.Find("inputs", "click", "activators", "Full_Press", "bindings")!.GetValue("binding"));
    }

    [Fact]
    public void Throttle_IsIgnoredByDefault()
    {
        // 쓰로틀 게임을 고르지 않으면(쓰로틀 사용 안 함) 쓰로틀 매핑은 장치 설정에도 게임 설정에도 들어가지 않는다
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var bindings = Presets.BasicFlightStick(Fixtures.TA320);
        bindings.Add(new MappingBinding { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 60, RangeHigh = 100 });
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "G", Bindings = bindings });
        var plan = _service.Prepare(p);
        Assert.True(plan.Validation.IsValid, string.Join("\n", plan.Validation.Errors));
        Assert.Null(plan.Layout!.Get("righttrigger"));
        Assert.Contains(plan.Notes, n => n.Contains("적용하지 않습니다") && n.Contains("G"));
    }

    [Fact]
    public void ChosenThrottleGame_OverridesOthers()
    {
        // 게임 A는 쓰로틀 − → LT, 게임 B는 쓰로틀 + → RT: 고른 게임 것만 장치 설정에 들어간다
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        var a = Presets.BasicFlightStick(Fixtures.TA320);
        a.Add(new MappingBinding { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), RangeLow = 0, RangeHigh = 70 });
        var b = Presets.BasicFlightStick(Fixtures.TA320);
        b.Add(new MappingBinding { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 30, RangeHigh = 100 });
        p.Games.Add(new GameMapping { AppId = "1000", GameName = "A", Bindings = a });
        p.Games.Add(new GameMapping { AppId = "2000", GameName = "B", Bindings = b });
        Assert.True(SteamConfigValidator.ValidateMapper(p).IsValid); // 서로 다른 방향이어도 오류 아님 (적용 때 하나를 고름)

        var planA = _service.Prepare(p, "1000");
        Assert.True(planA.Validation.IsValid, string.Join("\n", planA.Validation.Errors));
        Assert.Equal("a3~", planA.Layout!.Get("lefttrigger"));
        Assert.Null(planA.Layout.Get("righttrigger"));
        var planB = _service.Prepare(p, "2000");
        Assert.Equal("a3", planB.Layout!.Get("righttrigger"));
        Assert.Contains(planB.Notes, n => n.Contains("적용하지 않습니다") && n.Contains("A"));
    }

    [Fact]
    public void ThrottleMapping_ShowsSpecialHandlingWarning()
    {
        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        p.Games.Add(new GameMapping
        {
            AppId = "1000", GameName = "G",
            Bindings = { new() { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 60, RangeHigh = 100 } },
        });
        Assert.Contains(SteamConfigValidator.ValidateMapper(p).Warnings, w => w.Contains("쓰로틀은 특수 처리") && w.Contains("쓰로틀 1개"));
    }

    [Fact]
    public void ThrottleSummary_TellsOneOrTwo()
    {
        var none = new GameMapping { AppId = "1", GameName = "N", Bindings = Presets.BasicFlightStick(Fixtures.TA320) };
        var two = new GameMapping
        {
            AppId = "2", GameName = "T", Bindings =
            {
                new() { Target = XboxOutput.LT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), RangeLow = 0, RangeHigh = 40 },
                new() { Target = XboxOutput.RT, Source = PhysicalInput.FromAxisHalf(JoyAxis.Slider0, +1), RangeLow = 60, RangeHigh = 100 },
            },
        };
        Assert.Null(SteamLayoutPlanner.ThrottleSummary(none));
        var s = SteamLayoutPlanner.ThrottleSummary(two)!;
        Assert.Contains("쓰로틀 2개", s);
        Assert.Contains("0~50%", s);
        Assert.Contains("50~100%", s);
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

/// <summary>감지: 쓰로틀을 바닥까지 내리면 축과 끝단 버튼(17)이 함께 바뀌므로 둘 다 후보로 돌려준다.</summary>
public class InputDetectorTests
{
    private static InputSnapshot Snap(double slider, bool button17) =>
        new() { Buttons = Enumerable.Range(0, 17).Select(i => i == 16 && button17).ToArray(), Povs = new[] { -1 }, Axes = { [JoyAxis.Slider0] = slider, [JoyAxis.X] = 0 } };

    [Fact]
    public void ThrottleToBottom_GivesAxisAndEndButton()
    {
        var d = new InputDetector();
        d.Reset(Snap(-0.6, false));
        var found = d.DetectAll(Snap(-1, true), sensitiveAxis: true);
        Assert.Equal(new[] { PhysicalInput.FromAxis(JoyAxis.Slider0), PhysicalInput.FromButton(16) }, found);
        Assert.Equal(-1, d.AxisDirection(Snap(-1, true), JoyAxis.Slider0));
    }

    [Fact]
    public void SmallAxisNoise_IsIgnoredForButtons()
    {
        var d = new InputDetector();
        d.Reset(Snap(-0.9, false));
        Assert.Equal(new[] { PhysicalInput.FromButton(16) }, d.DetectAll(Snap(-1, true)));
    }
}

public class AceCombat8PresetTests
{
    [Fact]
    public void Preset_MatchesUserMapping_AndIsValid()
    {
        var bindings = Presets.AceCombat8(Fixtures.TA320);
        Assert.Equal(17, bindings.Count);
        Assert.Equal(PhysicalInput.FromButton(0), bindings.Single(b => b.Target == XboxOutput.A).Source);
        Assert.Equal(PhysicalInput.FromButton(2), bindings.Single(b => b.Target == XboxOutput.B).Source);
        Assert.Equal(PhysicalInput.FromButton(1), bindings.Single(b => b.Target == XboxOutput.X).Source);
        Assert.Equal(PhysicalInput.FromButton(3), bindings.Single(b => b.Target == XboxOutput.Y).Source);
        Assert.Equal(new[] { PhysicalInput.FromButton(16), PhysicalInput.FromButton(13) },
            bindings.Where(b => b.Target == XboxOutput.LT).Select(b => b.Source)); // 버튼 17·14
        var rt = bindings.Single(b => b.Target == XboxOutput.RT);
        Assert.Equal(PhysicalInput.FromAxisHalf(JoyAxis.Slider0, -1), rt.Source);
        Assert.Equal((0, 70), rt.TriggerRange());
        Assert.Equal(PhysicalInput.FromAxisHalf(JoyAxis.Rz, -1), bindings.Single(b => b.Target == XboxOutput.LB).Source);
        Assert.Equal(PhysicalInput.FromAxisHalf(JoyAxis.Rz, +1), bindings.Single(b => b.Target == XboxOutput.RB).Source);
        Assert.Equal(0, bindings.Single(b => b.Target == XboxOutput.LB).DeadZone);
        Assert.Equal(0, bindings.Single(b => b.Target == XboxOutput.RB).DeadZone);
        Assert.Equal(PhysicalInput.FromButton(15), bindings.Single(b => b.Target == XboxOutput.LS).Source);
        Assert.Equal(PhysicalInput.FromButton(14), bindings.Single(b => b.Target == XboxOutput.RS).Source);

        var p = MapperProfile.CreateDefault(Fixtures.TA320);
        p.Games.Add(new GameMapping { AppId = "2288340", GameName = "ACE COMBAT 8", Bindings = bindings });
        var v = SteamConfigValidator.ValidateMapper(p);
        Assert.True(v.IsValid, string.Join("\n", v.Errors));
        // 쓰로틀 게임으로 고르면: 쓰로틀 매핑 1개(−)라 축 전체 뒤집어서 RT, 버튼 17이 LT 자리
        var layout = SteamLayoutPlanner.BuildLayout(SteamJoystickMapper.Steam.SteamApplyService.WithThrottleFrom(p, "2288340"), null);
        Assert.Equal("b0", layout.Get("a"));
        Assert.Equal("b2", layout.Get("b"));
        Assert.Equal("b1", layout.Get("x"));
        Assert.Equal("b3", layout.Get("y"));
        Assert.Equal("a3~", layout.Get("righttrigger"));
        Assert.Equal("b16", layout.Get("lefttrigger"));
        var reasons = new List<string>();
        var plan = SteamLayoutPlanner.TryPlan(layout, p.GameView(p.Games.Single()), reasons);
        Assert.True(plan != null, string.Join("\n", reasons));
        Assert.Equal(2, plan!.ButtonBindings.Values.Count(value => value == "TRIGGER_LEFT"));
    }
}
