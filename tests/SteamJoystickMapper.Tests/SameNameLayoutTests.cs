using SteamJoystickMapper.Steam;

namespace SteamJoystickMapper.Tests;

/// <summary>같은 이름 레이아웃 덮어쓰기: 이 앱이 만든 것 중 제목과 설명(게임·장치)이 모두 같은 파일만.</summary>
public class SameNameLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sjm-same-" + Guid.NewGuid().ToString("N"));

    public SameNameLayoutTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Layout(string file, string title, string description)
    {
        var path = Path.Combine(_dir, file);
        File.WriteAllText(path, $"\"controller_mappings\"\n{{\n\t\"title\"\t\t\"{title}\"\n\t\"description\"\t\t\"{description}\"\n}}\n");
        return path;
    }

    [Fact]
    public void OnlySameTitleAndDescription_FromThisApp()
    {
        const string title = "Monkeyhead Mapper - default";
        const string desc = "Monkeyhead Mapper - ACE COMBAT 8 - T.A320 Copilot";
        var target = Layout("controller_generic.vdf", title, desc);
        var old = Layout("44f-406-99860a.vdf", title, desc);                                            // 예전 버전이 만든 같은 레이아웃
        Layout("44f-405-99860a.vdf", title, "Monkeyhead Mapper - ACE COMBAT 8 - T.A320 Pilot");          // 다른 장치용
        Layout("45e-2e3-99860a.vdf", title, "my own layout");                                           // 앱이 만들지 않음

        Assert.Equal(new[] { old }, SteamApplyService.SameNameLayouts(target, title, desc));
    }
}
