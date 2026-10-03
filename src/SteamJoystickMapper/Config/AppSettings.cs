using System.IO;
using System.Text.Json;
using SteamJoystickMapper.Mapping;

namespace SteamJoystickMapper.Config;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamJoystickMapper");

    public static string ProfileFile => Path.Combine(Root, "profile.json");
    public static string Backup => Path.Combine(Root, "Backup");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
}

/// <summary>앱 자체 설정 (마지막 선택 등).</summary>
public sealed class AppSettings
{
    public string? LastAppId { get; set; }
    public string? LastDeviceInstanceId { get; set; }
    /// <summary>[Steam에 적용] 때 마지막으로 고른 쓰로틀 게임 AppID ("" 또는 null = 쓰로틀 사용 안 함).</summary>
    public string? LastThrottleAppId { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), ProfileStore.JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Logging.AppLog.Warn($"설정 파일을 읽을 수 없어 기본값 사용: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, ProfileStore.JsonOptions));
        }
        catch (IOException ex)
        {
            Logging.AppLog.Warn($"설정 저장 실패: {ex.Message}");
        }
    }
}
