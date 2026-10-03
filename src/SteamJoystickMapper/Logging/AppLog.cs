using System.Collections.Concurrent;
using System.IO;

namespace SteamJoystickMapper.Logging;

/// <summary>
/// 앱 로그. 민감 정보(Steam 계정 ID, 이름 등)는 등록 후 자동 마스킹된다.
/// </summary>
public static class AppLog
{
    public const int RecentLineLimit = 2000;
    private static readonly object FileLock = new();
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    private static readonly Queue<string> RecentLines = new();
    private static string? _logFile;

    public static event Action<string>? LineAdded;

    public static string? CurrentFilePath => _logFile;

    public static string[] GetRecentLines()
    {
        lock (FileLock) return RecentLines.ToArray();
    }

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
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level}{Sanitize(message)}";
        lock (FileLock)
        {
            RecentLines.Enqueue(line);
            while (RecentLines.Count > RecentLineLimit) RecentLines.Dequeue();
            if (_logFile != null)
            {
                try { File.AppendAllText(_logFile, line + Environment.NewLine); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 파일 기록에 실패해도 화면 로그와 앱 동작은 유지한다.
                }
            }
        }
        LineAdded?.Invoke(line);
    }
}
