using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Mapping;
using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Tests;

internal static class Fixtures
{
    /// <summary>실제 T.A320 Pilot: 축 X, Y, Rz(트위스트), Slider(스로틀), 버튼 17, POV 1.</summary>
    public static DeviceIdentity TA320 => new()
    {
        Name = "T.A320 Pilot", Vid = "044F", Pid = "0405",
        Axes = new() { JoyAxis.X, JoyAxis.Y, JoyAxis.Rz, JoyAxis.Slider0 }, ButtonCount = 17, PovCount = 1,
    };

    public static MappingProfile Ac8() => new()
    {
        ProfileName = "AC8_TA320", AppId = "1234560", GameName = "Ace Combat 8", Device = TA320,
        Bindings = Presets.AceCombatFlightStick(TA320),
    };

    public static PerGamePlan PlanOnOwnLayout(MappingProfile p) =>
        SteamLayoutPlanner.TryPlan(SteamLayoutPlanner.BuildNaturalLayout(p, null), p, new List<string>())!;
}

public class PlannerTests
{
    [Fact]
    public void NaturalLayout_UsesHidAxisOrderAndPov()
    {
        var layout = SteamLayoutPlanner.BuildNaturalLayout(Fixtures.Ac8(), null);
        Assert.Equal("a0", layout.Get("leftx"));
        Assert.Equal("a1", layout.Get("lefty"));
        Assert.Equal("a2", layout.Get("rightx")); // Rz는 세 번째로 존재하는 축
        Assert.Equal("b0", layout.Get("b"));      // Trigger → B
        Assert.Equal("b1", layout.Get("a"));
        Assert.Equal("h0.1", layout.Get("dpup"));
        Assert.Equal("h0.8", layout.Get("dpleft"));
        Assert.Equal("platform", layout.Fields[^1].Key);
    }

    [Fact]
    public void GlobalOnOwnLayout_IsPassthrough()
    {
        var plan = Fixtures.PlanOnOwnLayout(Fixtures.Ac8());
        Assert.Equal("B", plan.ButtonBindings[("button_diamond", "button_b")]);
        Assert.Equal(StickSide.Left, plan.StickOutputs["joystick"]);
        Assert.Equal(StickSide.Right, plan.StickOutputs["right_joystick"]);
        Assert.Equal((5, 0), plan.StickDeadZones["joystick"]);
    }

    [Fact]
    public void GameButtonSwap_IsExpressedOnLayout()
    {
        var global = SteamLayoutPlanner.BuildNaturalLayout(Fixtures.Ac8(), null);
        var game = Fixtures.Ac8();
        game.Bindings.First(b => b.Target == XboxOutput.B).Target = XboxOutput.RB; // 이 게임에서만 Trigger → RB

        var plan = SteamLayoutPlanner.TryPlan(global, game, new List<string>())!;
        // 전역상 Trigger(b0)는 'b' 자리 → 이 게임에서는 그 자리가 RB를 출력
        Assert.Equal("shoulder_right", plan.ButtonBindings[("button_diamond", "button_b")]);
        Assert.False(plan.ButtonBindings.ContainsValue("B"));
    }

    [Fact]
    public void GameLeftStickToRightStick_ViaOutputJoystick()
    {
        var global = SteamLayoutPlanner.BuildNaturalLayout(Fixtures.Ac8(), null);
        var p = Fixtures.Ac8();
        p.Bindings.RemoveAll(b => b.Target is XboxOutput.RightStickX);
        p.Bindings.First(b => b.Target == XboxOutput.LeftStickX).Target = XboxOutput.RightStickX;
        p.Bindings.First(b => b.Target == XboxOutput.LeftStickY).Target = XboxOutput.RightStickY;

        var plan = SteamLayoutPlanner.TryPlan(global, p, new List<string>())!;
        Assert.Equal(StickSide.Right, plan.StickOutputs["joystick"]);
    }

    [Fact]
    public void GameInvertDifferentFromGlobal_IsNotExpressible()
    {
        var global = SteamLayoutPlanner.BuildNaturalLayout(Fixtures.Ac8(), null);
        var p = Fixtures.Ac8();
        p.Bindings.First(b => b.Target == XboxOutput.LeftStickY).Invert = true;
        var reasons = new List<string>();
        Assert.Null(SteamLayoutPlanner.TryPlan(global, p, reasons));
        Assert.Contains(reasons, r => r.Contains("Stick Y"));
    }

    [Fact]
    public void ExtraAxisSharingUsedStick_IsRejected()
    {
        var global = SteamLayoutPlanner.BuildNaturalLayout(Fixtures.Ac8(), null);
        var p = Fixtures.Ac8();
        p.Bindings.RemoveAll(b => b.Target == XboxOutput.LeftStickY); // 전역엔 lefty:a1 이 남아 있음
        var reasons = new List<string>();
        Assert.Null(SteamLayoutPlanner.TryPlan(global, p, reasons));
    }

    [Fact]
    public void ThrottleToTrigger_Analog()
    {
        var p = Fixtures.Ac8();
        p.Bindings.Add(new MappingBinding { Source = PhysicalInput.FromAxis(JoyAxis.Slider0), Target = XboxOutput.RT });
        Assert.Equal("a3", SteamLayoutPlanner.BuildNaturalLayout(p, null).Get("righttrigger"));
        Assert.Equal(2, Fixtures.PlanOnOwnLayout(p).TriggerOutputs["right_trigger"]);
    }

    [Fact]
    public void SameLayout_IgnoresOrderAndMeta()
    {
        var a = SdlMapping.TryParse("030000004f0400000504000000000000,X,a:b1,b:b0,platform:Windows,")!;
        var b = SdlMapping.TryParse("030000004f0400000504000000000000,Y,b:b0,a:b1,")!;
        Assert.True(SteamLayoutPlanner.SameLayout(a, b));
        b.Set("x", "b2");
        Assert.False(SteamLayoutPlanner.SameLayout(a, b));
    }
}
public class GeneratorTests
{
    [Fact]
    public void Generate_OnMinimalTemplate_ProducesExpectedBindings()
    {
        var profile = Fixtures.Ac8();
        var log = new List<string>();
        var root = SteamConfigGenerator.Generate(SteamConfigGenerator.MinimalTemplate(), profile, Fixtures.PlanOnOwnLayout(profile), log);
        var text = VdfWriter.Write(root);
        var m = VdfParser.Parse(text).Get("controller_mappings")!;

        Assert.Equal("AC8_TA320", m.GetValue("title"));
        var gsb = m.Get("preset")!.Get("group_source_bindings")!;
        string GroupId(string source) => gsb.Children!.First(c => c.Value!.StartsWith(source + " ")).Key;
        VdfNode Group(string source) => m.GetAll("group").First(g => g.GetValue("id") == GroupId(source));

        Assert.Equal("four_buttons", Group("button_diamond").GetValue("mode"));
        Assert.Equal("xinput_button B",
            Group("button_diamond").Find("inputs", "button_b", "activators", "Full_Press", "bindings", "binding")!.Value);
        Assert.Equal("xinput_button dpad_up",
            Group("dpad").Find("inputs", "dpad_north", "activators", "Full_Press", "bindings", "binding")!.Value);
        Assert.Equal("1638", Group("joystick").Find("settings", "deadzone_inner_radius")!.Value);
        Assert.Null(Group("joystick").Find("settings", "output_joystick"));
        Assert.Equal("0", Group("left_trigger").Find("settings", "output_trigger")!.Value);
        Assert.Contains("inactive", gsb.Children!.First(c => c.Value!.StartsWith("left_trigger ")).Value);
        Assert.Contains("inactive", gsb.Children!.First(c => c.Value!.StartsWith("switch ")).Value);
        Assert.Contains("active", gsb.Children!.First(c => c.Value!.StartsWith("right_joystick ")).Value);
    }

    [Fact]
    public void Generate_ReplacesGroupWithWrongMode_AndRemovesStaleInputs()
    {
        var baseDoc = VdfParser.Parse(
            "\"controller_mappings\" { \"version\" \"3\" " +
            "\"group\" { \"id\" \"0\" \"mode\" \"four_buttons\" \"inputs\" { \"button_a\" { \"activators\" { \"Full_Press\" { \"bindings\" { \"binding\" \"xinput_button A\" } \"settings\" { \"haptic_intensity\" \"1\" } } } } \"stale_input\" { } } } " +
            "\"group\" { \"id\" \"3\" \"mode\" \"dpad\" } " +
            "\"preset\" { \"id\" \"0\" \"name\" \"Default\" \"group_source_bindings\" { \"0\" \"button_diamond active\" \"3\" \"joystick active\" \"4\" \"joystick active modeshift\" } } }");
        var profile = Fixtures.Ac8();
        profile.Bindings.First(b => b.Target == XboxOutput.A).Target = XboxOutput.A;
        var log = new List<string>();
        var m = SteamConfigGenerator.Generate(baseDoc, profile, Fixtures.PlanOnOwnLayout(profile), log).Get("controller_mappings")!;

        var diamond = m.GetAll("group").First(g => g.GetValue("id") == "0");
        Assert.Null(diamond.Find("inputs", "stale_input"));                                 // 프로필에 없는 입력 제거
        Assert.Equal("1", diamond.Find("inputs", "button_a", "activators", "Full_Press", "settings", "haptic_intensity")!.Value); // 햅틱 유지
        var gsb = m.Get("preset")!.Get("group_source_bindings")!;
        Assert.DoesNotContain(gsb.Children!, c => c.Value!.Contains("modeshift"));
        var joystickGroupId = gsb.Children!.First(c => c.Value!.StartsWith("joystick ")).Key;
        Assert.NotEqual("3", joystickGroupId);
        Assert.Equal("joystick_move", m.GetAll("group").First(g => g.GetValue("id") == joystickGroupId).GetValue("mode"));
        Assert.NotEmpty(log);
    }

    [Fact]
    public void Configset_SelectsAutosave()
    {
        var existing = VdfParser.Parse("\"controller_config\" { \"1234560\" { \"workshop\" \"999\" } \"42\" { \"autosave\" \"1\" } }");
        var updated = SteamConfigGenerator.UpdateConfigset(existing, "1234560");
        Assert.Equal("1", updated.Find("controller_config", "1234560", "autosave")!.Value);
        Assert.Null(updated.Find("controller_config", "1234560", "workshop"));
        Assert.Equal("1", updated.Find("controller_config", "42", "autosave")!.Value);
    }

    [Fact]
    public void GlobalConfig_UpsertsLayout()
    {
        var config = VdfParser.Parse("\"InstallConfigStore\" { \"Software\" { } \"SDL_GamepadBind\" \"03000000de280000ff11000001000000,Steam Virtual Gamepad,a:b0,platform:Windows,\" }");
        var layout = SteamLayoutPlanner.BuildNaturalLayout(Fixtures.Ac8(), null);
        var updated = SteamConfigGenerator.UpdateGlobalConfig(config, layout, Fixtures.TA320);
        var bind = updated.Find("InstallConfigStore", "SDL_GamepadBind")!.Value!;
        Assert.Contains("Steam Virtual Gamepad", bind);
        Assert.NotNull(SteamConfigGenerator.ReadDeviceLayout(updated, Fixtures.TA320));
    }
}

public class ValidatorTests
{
    [Fact]
    public void Preset_IsValid()
    {
        var v = SteamConfigValidator.ValidateProfile(Fixtures.Ac8());
        Assert.True(v.IsValid, string.Join("\n", v.Errors));
    }

    [Fact]
    public void DetectsConflictsAndUnsupported()
    {
        var p = Fixtures.Ac8();
        p.AppId = "abc";
        p.Bindings.Add(new MappingBinding { Source = PhysicalInput.FromAxis(JoyAxis.Slider0), Target = XboxOutput.LeftStickX }); // 출력 충돌 (스틱 축에 축 2개)
        p.Bindings.Add(new MappingBinding { Source = PhysicalInput.FromButton(5), Target = XboxOutput.B });       // 버튼은 같은 출력에 여러 개 허용
        p.Bindings.Add(new MappingBinding { Source = PhysicalInput.FromAxis(JoyAxis.Slider0), Target = XboxOutput.LB }); // 축 전체→버튼
        p.Bindings.Add(new MappingBinding { Source = PhysicalInput.FromButton(6), Target = XboxOutput.Guide });
        p.Bindings.Add(new MappingBinding { Source = PhysicalInput.FromButton(40), Target = XboxOutput.View });
        var v = SteamConfigValidator.ValidateProfile(p);
        Assert.Contains(v.Errors, e => e.Contains("AppID"));
        Assert.Contains(v.Errors, e => e.Contains("출력 충돌"));
        Assert.Contains(v.Errors, e => e.Contains("한쪽 방향")); // 축 전체 → 버튼은 방향이 필요
        Assert.Contains(v.Errors, e => e.Contains("Guide"));
        Assert.Contains(v.Errors, e => e.Contains("버튼이 없습니다"));
    }

    [Fact]
    public void GeneratedVdf_SyntaxChecked()
    {
        var v = SteamConfigValidator.ValidateGenerated("44f-405-99860a", "\"controller_mappings\" {", "\"controller_config\" { }", null, null);
        Assert.Contains(v.Errors, e => e.Contains("구문"));
        Assert.Contains(SteamConfigValidator.ValidateGenerated("bad id!", "", "", null, null).Errors, e => e.Contains("Controller ID"));
    }
}

public class BackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sjm-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void CreateAndRestore_RoundTrip_WithoutDeletingNewFiles()
    {
        var steamDir = Path.Combine(_dir, "steam");
        Directory.CreateDirectory(steamDir);
        var existing = Path.Combine(steamDir, "configset_x.vdf");
        var created = Path.Combine(steamDir, "new.vdf");
        File.WriteAllText(existing, "original");

        var manager = new BackupManager(Path.Combine(_dir, "backup"));
        var backup = manager.Create(new BackupManifest { AppId = "1", GameName = "G", DeviceName = "D", ControllerId = "44f-405-a" }, new[] { existing, created });
        Assert.Equal(BackupManager.ComputeSha256(existing), backup.Files[0].Sha256);
        Assert.False(backup.Files[1].Existed);

        File.WriteAllText(existing, "modified");
        File.WriteAllText(created, "generated");
        manager.Restore(manager.List().Single());

        Assert.Equal("original", File.ReadAllText(existing));
        Assert.False(File.Exists(created));
        // 새로 생긴 파일은 삭제되지 않고 백업 폴더로 이동
        Assert.Single(Directory.GetFiles(backup.Folder, "*new.vdf", SearchOption.AllDirectories));
    }

    [Fact]
    public void ControllerId_IsGeneric_EvenWithDeviceSpecificConfigset()
    {
        // 일반 컨트롤러로 등록된 장치는 Steam이 controller_generic 파일을 쓴다 (장치 전용 configset이 있어도)
        var dir = Path.Combine(_dir, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "configset_44f-405-99860a.vdf"), "\"controller_config\" { }");
        var target = SteamInputConfigFinder.Resolve(dir, Fixtures.TA320, "123");
        Assert.Equal("controller_generic", target.ControllerId);
        Assert.EndsWith(Path.Combine("123", "controller_generic.vdf"), target.PerGamePath);
        Assert.EndsWith("configset_controller_generic.vdf", target.ConfigsetPath);
    }
}
