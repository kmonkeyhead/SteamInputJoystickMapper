using System.IO;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Steam;

public sealed class SteamGame
{
    public required string AppId { get; init; }
    public required string Name { get; init; }
    public bool Installed { get; init; }
    public string? InstallDir { get; init; }
    /// <summary>프로필에 이 게임의 매핑이 있는지 (UI에서 갱신).</summary>
    public bool HasMapping { get; set; }

    public string Display => $"{(HasMapping ? "★ " : "")}{Name}  ({AppId}){(Installed ? "" : "  [미설치]")}";
    public override string ToString() => Display;
}

public static class SteamGameScanner
{
    // 게임이 아닌 Steam 구성 요소
    private static readonly HashSet<string> IgnoredAppIds = new()
    {
        "228980", // Steamworks Common Redistributables
        "241100", // Steam Controller Configs
        "250820", // SteamVR
        "1070560", "1391110", "1628350", "1826330", // Steam Linux Runtime
    };

    public static IReadOnlyList<SteamGame> Scan(string steamPath)
    {
        var games = new Dictionary<string, SteamGame>();
        foreach (var library in SteamLocator.GetLibraryFolders(steamPath))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var state = VdfParser.ParseFile(manifest).Get("AppState");
                    var appId = state?.GetValue("appid");
                    var name = state?.GetValue("name");
                    if (appId == null || name == null || IgnoredAppIds.Contains(appId)) continue;
                    if (name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) continue;
                    var dir = state!.GetValue("installdir");
                    games[appId] = new SteamGame
                    {
                        AppId = appId,
                        Name = name,
                        Installed = true,
                        InstallDir = dir == null ? null : Path.Combine(steamapps, "common", dir),
                    };
                }
                catch (Exception ex) when (ex is VdfParseException or IOException)
                {
                    AppLog.Warn($"{Path.GetFileName(manifest)} 분석 실패: {ex.Message}");
                }
            }
        }
        return games.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static bool IsValidAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && uint.TryParse(appId, out var v) && v > 0;
}
