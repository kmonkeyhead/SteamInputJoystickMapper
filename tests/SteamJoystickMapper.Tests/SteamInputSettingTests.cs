using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Tests;

/// <summary>localconfig.vdf의 게임별 Steam Input 값만 텍스트로 고치는지 검증.</summary>
public class SteamInputSettingTests
{
    private const string Base =
        "\"UserLocalConfigStore\"\n{\n\t\"Json\"\t\t\"{\\\"a\\\":\\\"x\\/y\\\\n\\\"}\"\n\t\"apps\"\n\t{\n" +
        "\t\t\"100\"\n\t\t{\n\t\t\t\"UseSteamControllerConfig\"\t\t\"0\"\n\t\t}\n" +
        "\t\t\"200\"\n\t\t{\n\t\t\t\"CDDialog\"\t\t\"1\"\n\t\t}\n\t}\n\t\"After\"\t\t\"1\"\n}\n";

    [Theory]
    [InlineData("0", SteamInputMode.Off)]
    [InlineData("1", SteamInputMode.Default)]
    [InlineData("2", SteamInputMode.On)]
    [InlineData(null, SteamInputMode.Default)]
    public void Parse_MapsSteamValues(string? value, SteamInputMode expected) =>
        Assert.Equal(expected, SteamInputSetting.Parse(value));

    [Fact]
    public void ExistingValue_IsReplacedInPlace()
    {
        var result = SteamInputSetting.EnableFor(Base, new[] { "100" });
        Assert.Equal(Base.Replace("\"UseSteamControllerConfig\"\t\t\"0\"", "\"UseSteamControllerConfig\"\t\t\"2\""), result);
    }

    [Fact]
    public void AppWithoutKey_GetsKeyAppended()
    {
        var result = SteamInputSetting.EnableFor(Base, new[] { "200" });
        Assert.Contains("\t\t\"200\"\n\t\t{\n\t\t\t\"CDDialog\"\t\t\"1\"\n\t\t\t\"UseSteamControllerConfig\"\t\t\"2\"\n\t\t}\n", result);
        Assert.Equal(SteamInputMode.On, SteamInputSetting.Read(VdfParser.Parse(result), "200"));
    }

    [Fact]
    public void MissingApp_GetsNewBlockInApps()
    {
        var result = SteamInputSetting.EnableFor(Base, new[] { "300", "100" });
        Assert.Contains("\t\t\"300\"\n\t\t{\n\t\t\t\"UseSteamControllerConfig\"\t\t\"2\"\n\t\t}\n\t}\n\t\"After\"", result);
        var root = VdfParser.Parse(result);
        Assert.Equal(SteamInputMode.On, SteamInputSetting.Read(root, "300"));
        Assert.Equal(SteamInputMode.On, SteamInputSetting.Read(root, "100"));
        Assert.StartsWith(Base[..Base.IndexOf("\t\"apps\"")], result); // 이스케이프 값 포함 앞부분은 바이트 그대로
    }

    [Fact]
    public void MissingApps_IsCreated()
    {
        var text = "\"UserLocalConfigStore\"\r\n{\r\n\t\"A\"\t\t\"1\"\r\n}\r\n";
        var result = SteamInputSetting.EnableFor(text, new[] { "5" });
        Assert.Equal("\"UserLocalConfigStore\"\r\n{\r\n\t\"A\"\t\t\"1\"\r\n\t\"apps\"\r\n\t{\r\n\t\t\"5\"\r\n\t\t{\r\n" +
                     "\t\t\t\"UseSteamControllerConfig\"\t\t\"2\"\r\n\t\t}\r\n\t}\r\n}\r\n", result);
    }

    [Fact]
    public void NestedAppsSection_IsNotTouched()
    {
        // 다른 깊이의 "apps"(예: Software/Valve/Steam/apps)는 게임 속성이 아니다
        var text = "\"UserLocalConfigStore\"\n{\n\t\"Software\"\n\t{\n\t\t\"apps\"\n\t\t{\n\t\t\t\"100\"\n\t\t\t{\n\t\t\t}\n\t\t}\n\t}\n}\n";
        var result = SteamInputSetting.EnableFor(text, new[] { "100" });
        Assert.StartsWith(text[..^2], result);
        Assert.Equal(SteamInputMode.On, SteamInputSetting.Read(VdfParser.Parse(result), "100"));
    }
}
