using SteamJoystickMapper.Steam;
using SteamJoystickMapper.Steam.Sdl;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Tests;

public class VdfTests
{
    private const string Sample =
        "\"controller_mappings\"\n{\n" +
        "\t\"version\"\t\t\"3\"\n" +
        "\t\"path\"\t\t\"F:\\\\game\\\\steam\"\n" +
        "\t\"group\"\n\t{\n\t\t\"id\"\t\t\"0\"\n\t}\n" +
        "\t\"group\"\n\t{\n\t\t\"id\"\t\t\"1\"\n\t}\n" +
        "\t\"multi\"\t\t\"line1\nline2\"\n" +
        "}\n";

    [Fact]
    public void Parse_PreservesDuplicateKeysEscapesAndNewlines()
    {
        var root = VdfParser.Parse(Sample);
        var m = root.Get("controller_mappings")!;
        Assert.Equal(2, m.GetAll("group").Count());
        Assert.Equal(@"F:\game\steam", m.GetValue("path"));
        Assert.Equal("line1\nline2", m.GetValue("multi"));
    }

    [Fact]
    public void Write_RoundTripsSteamStyle()
    {
        Assert.Equal(Sample, VdfWriter.Write(VdfParser.Parse(Sample)));
    }

    [Fact]
    public void Parse_HandlesCommentsUnquotedAndConditionals()
    {
        var root = VdfParser.Parse("// comment\nroot { key value [$WIN32] \"q\" \"v\" }");
        Assert.Equal("value", root.Find("root", "key")!.Value);
        Assert.Equal("v", root.Find("root", "q")!.Value);
    }

    [Fact]
    public void Parse_UnclosedBrace_Throws()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"a\" { \"b\" \"c\""));
    }

    /// <summary>이 PC의 실제 Steam 파일로 읽기 전용 왕복 검사 (Steam이 없으면 건너뜀).</summary>
    [Fact]
    public void RealSteamFiles_RoundTripExactly()
    {
        var steam = SteamLocator.FindSteamPath();
        if (steam == null) return;
        var files = new List<string>
        {
            Path.Combine(steam, "config", "config.vdf"),
            Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
        };
        // Valve 템플릿은 Steam 클라이언트 포맷이 아니므로(읽기 전용) 구문 분석만 확인
        var template = Path.Combine(steam, "controller_base", "templates", SteamConfigGenerator.TemplateFileName);
        if (File.Exists(template)) Assert.NotNull(VdfParser.ParseFile(template).Get("controller_mappings"));
        var configs = Path.Combine(steam, "steamapps", "common", "Steam Controller Configs");
        if (Directory.Exists(configs)) files.AddRange(Directory.EnumerateFiles(configs, "*.vdf", SearchOption.AllDirectories).Take(40));

        foreach (var file in files.Where(File.Exists))
        {
            var text = File.ReadAllText(file);
            var written = VdfWriter.Write(VdfParser.Parse(text));
            Assert.True(Normalize(text) == Normalize(written), $"round-trip mismatch: {Path.GetFileName(file)}");
        }
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimStart('\uFEFF').TrimEnd();
}

public class SdlTests
{
    private const string SteamBind =
        "03000000de280000ff11000001000000,Steam Virtual Gamepad,a:b0,b:b1,lefttrigger:+a2,righttrigger:-a2,platform:Windows,\n" +
        "https://example.com/some/note\n" +
        "030000004f0400000504000000000000,T.A320 Pilot,a:b1,b:b0,leftx:a0,lefty:a1~,platform:Windows,";

    [Fact]
    public void GuidFor_EncodesVidPidLittleEndian()
    {
        Assert.Equal("030000004f0400000504000000000000", SdlMapping.GuidFor(0x044F, 0x0405));
    }

    [Fact]
    public void Database_FindsDeviceAndPreservesUnknownLines()
    {
        var db = SdlMappingDatabase.Parse(SteamBind);
        var ta = db.Find(0x044F, 0x0405)!;
        Assert.Equal("a1~", ta.Get("lefty"));

        var replacement = SdlMapping.TryParse("030000004f0400000504000000000000,T.A320 Pilot,b:b0,platform:Windows,")!;
        db.Upsert(replacement, 0x044F, 0x0405);
        var text = db.ToString();
        Assert.Contains("https://example.com/some/note", text);
        Assert.Contains("Steam Virtual Gamepad", text);
        Assert.Single(SdlMappingDatabase.Parse(text).Mappings, m => m.Matches(0x044F, 0x0405));
        Assert.Null(SdlMappingDatabase.Parse(text).Find(0x044F, 0x0405)!.Get("lefty"));
    }

    [Fact]
    public void Database_UnchangedRoundTrip()
    {
        Assert.Equal(SteamBind, SdlMappingDatabase.Parse(SteamBind).ToString());
    }

    [Theory]
    [InlineData("a2", 'a', 2, null, false)]
    [InlineData("-a3~", 'a', 3, '-', true)]
    [InlineData("b16", 'b', 16, null, false)]
    public void ParseSource(string src, char type, int index, char? half, bool inverted)
    {
        var p = SdlElements.Parse(src)!.Value;
        Assert.Equal(type, p.Type);
        Assert.Equal(index, p.Index);
        Assert.Equal(half, p.HalfSign);
        Assert.Equal(inverted, p.Inverted);
    }

    [Fact]
    public void ParseSource_Hat()
    {
        var p = SdlElements.Parse("h0.4")!.Value;
        Assert.Equal("h0.4", p.BaseKey);
    }
}
