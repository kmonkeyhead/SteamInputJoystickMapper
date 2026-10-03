using System.IO;
using Microsoft.Win32;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Steam;

/// <summary>Steam 설치 경로와 라이브러리 폴더를 찾는다. 경로를 코드에 고정하지 않는다.</summary>
public static class SteamLocator
{
    public static string? FindSteamPath()
    {
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = Path.GetFullPath(candidate.Replace('/', '\\'));
            if (File.Exists(Path.Combine(normalized, "steam.exe")) || Directory.Exists(Path.Combine(normalized, "config")))
                return normalized;
        }
        return null;
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        yield return ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        yield return ReadRegistry(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(pf86, "Steam");
    }

    private static string? ReadRegistry(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>steamapps/libraryfolders.vdf 의 라이브러리 경로들. Steam 설치 경로가 항상 첫 번째.</summary>
    public static IReadOnlyList<string> GetLibraryFolders(string steamPath)
    {
        var result = new List<string> { steamPath };
        var file = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) file = Path.Combine(steamPath, "config", "libraryfolders.vdf");
        if (!File.Exists(file)) return result;
        try
        {
            var root = VdfParser.ParseFile(file);
            var folders = root.Get("libraryfolders") ?? root.Get("LibraryFolders");
            foreach (var entry in folders?.Children ?? new List<VdfNode>())
            {
                var path = entry.IsObject ? entry.GetValue("path") : entry.Value; // 구버전: "1" "D:\\Games"
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                var full = Path.GetFullPath(path);
                if (!result.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase))) result.Add(full);
            }
        }
        catch (Exception ex) when (ex is VdfParseException or IOException)
        {
            AppLog.Warn($"libraryfolders.vdf 분석 실패: {ex.Message}");
        }
        return result;
    }
}
