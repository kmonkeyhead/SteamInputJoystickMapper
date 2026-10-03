using System.IO;
using SteamJoystickMapper.Logging;

namespace SteamJoystickMapper.Steam;

/// <summary>탐색된 Steam 환경 (설치 경로, 사용자, 컨트롤러 설정 폴더).</summary>
public sealed class SteamEnvironment
{
    public required string SteamPath { get; init; }
    public required IReadOnlyList<SteamUser> Users { get; init; }
    public SteamUser? User { get; set; }

    public string GlobalConfigPath => SteamInputConfigFinder.GlobalConfigPath(SteamPath);
    /// <summary>게임별 Steam Input 사용 여부가 저장되는 사용자 설정 파일.</summary>
    public string? LocalConfigPath => User == null ? null : SteamInputSetting.LocalConfigPath(SteamPath, User.AccountId);
    public string? ControllerConfigDir => User == null ? null : SteamInputConfigFinder.ControllerConfigDir(SteamPath, User.AccountId);

    public static SteamEnvironment? Detect()
    {
        var steamPath = SteamLocator.FindSteamPath();
        if (steamPath == null)
        {
            AppLog.Warn("Steam 설치 경로를 찾을 수 없습니다.");
            return null;
        }
        AppLog.Info("Steam detected");

        var users = SteamUserDetector.FindUsers(steamPath);
        var env = new SteamEnvironment { SteamPath = steamPath, Users = users, User = SteamUserDetector.DetectCurrent(steamPath, users) };
        if (env.User != null)
        {
            AppLog.Info($"Userdata detected (사용자 {users.Count}명)");
            if (env.ControllerConfigDir != null && !Directory.Exists(env.ControllerConfigDir))
                AppLog.Warn("이 사용자의 Steam Input 설정 폴더가 아직 없습니다. 적용 시 생성됩니다.");
        }
        else
        {
            AppLog.Warn("Steam 사용자를 찾을 수 없습니다. Steam에 한 번 로그인하세요.");
        }
        return env;
    }
}
