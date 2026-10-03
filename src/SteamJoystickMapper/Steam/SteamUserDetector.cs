using System.IO;
using Microsoft.Win32;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Steam.Vdf;

using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.Steam;

public sealed record SteamUser(uint AccountId, string SteamId64, string PersonaName, bool MostRecent)
{
    public override string ToString() => string.IsNullOrEmpty(PersonaName) ? T($"사용자 {AccountId}", $"User {AccountId}") : PersonaName;
}

/// <summary>현재 Steam 사용자를 찾는다. 계정 정보는 로그에 기록되지 않도록 AppLog에 등록한다.</summary>
public static class SteamUserDetector
{
    private const ulong SteamId64Base = 76561197960265728UL;

    public static IReadOnlyList<SteamUser> FindUsers(string steamPath)
    {
        var users = new List<SteamUser>();
        var loginUsers = Path.Combine(steamPath, "config", "loginusers.vdf");
        if (File.Exists(loginUsers))
        {
            try
            {
                var root = VdfParser.ParseFile(loginUsers);
                foreach (var u in root.Get("users")?.Children ?? new List<VdfNode>())
                {
                    if (!ulong.TryParse(u.Key, out var id64) || id64 < SteamId64Base) continue;
                    var accountId = (uint)(id64 - SteamId64Base);
                    var persona = u.GetValue("PersonaName") ?? "";
                    AppLog.RegisterSecret(u.Key);
                    AppLog.RegisterSecret(accountId.ToString());
                    AppLog.RegisterSecret(u.GetValue("AccountName"));
                    AppLog.RegisterSecret(persona);
                    users.Add(new SteamUser(accountId, u.Key, persona, u.GetValue("MostRecent") == "1"));
                }
            }
            catch (Exception ex) when (ex is VdfParseException or IOException)
            {
                AppLog.Warn($"loginusers.vdf 분석 실패: {ex.Message}");
            }
        }

        // loginusers.vdf 에 없더라도 userdata 폴더가 있으면 후보로 추가
        var userdata = Path.Combine(steamPath, "userdata");
        if (Directory.Exists(userdata))
        {
            foreach (var dir in Directory.EnumerateDirectories(userdata))
            {
                if (!uint.TryParse(Path.GetFileName(dir), out var accountId) || accountId == 0) continue;
                if (users.Any(u => u.AccountId == accountId)) continue;
                AppLog.RegisterSecret(accountId.ToString());
                users.Add(new SteamUser(accountId, (accountId + SteamId64Base).ToString(), "", false));
            }
        }
        return users;
    }

    /// <summary>우선순위: 실행 중인 Steam의 ActiveUser → loginusers MostRecent → 컨트롤러 설정이 있는 사용자.</summary>
    public static SteamUser? DetectCurrent(string steamPath, IReadOnlyList<SteamUser> users)
    {
        if (users.Count == 0) return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int active && active != 0)
            {
                var match = users.FirstOrDefault(u => u.AccountId == (uint)active);
                if (match != null) return match;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        return users.FirstOrDefault(u => u.MostRecent)
            ?? users.FirstOrDefault(u => Directory.Exists(SteamInputConfigFinder.ControllerConfigDir(steamPath, u.AccountId)))
            ?? users[0];
    }
}
