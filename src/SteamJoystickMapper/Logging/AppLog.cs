using System.Collections.Concurrent;
using System.IO;

namespace SteamJoystickMapper.Logging;

/// <summary>
/// 앱 로그. 민감 정보(Steam 계정 ID, 이름 등)는 등록 후 자동 마스킹된다.
/// </summary>
public static class AppLog
{
    private static readonly object FileLock = new();
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    private static string? _logFile;

    public static event Action<string>? LineAdded;

    public static void Initialize(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFile = Path.Combine(logDirectory, $"{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>로그에 절대 노출되면 안 되는 문자열을 등록한다 (계정 ID, SteamID64, 계정명 등).</summary>
    public static void RegisterSecret(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3)
            Secrets.TryAdd(value, 0);
    }

    public static string Sanitize(string message)
    {
        foreach (var secret in Secrets.Keys.OrderByDescending(s => s.Length))
            message = message.Replace(secret, "<user>", StringComparison.OrdinalIgnoreCase);
        return message;
    }

    public static void Info(string message) => Write("", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR ", message);

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {level}{Sanitize(message)}";
        if (_logFile != null)
        {
            try
            {
                lock (FileLock) File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // 로그 파일 기록 실패는 앱 동작에 영향을 주지 않는다.
            }
        }
        LineAdded?.Invoke(line);
    }
}
