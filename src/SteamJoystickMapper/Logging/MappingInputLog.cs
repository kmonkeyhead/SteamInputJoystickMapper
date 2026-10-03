using System.IO;

namespace SteamJoystickMapper.Logging;

/// <summary>매핑 편집 창 하나의 입력 기록. 메인 화면의 AppLog로 전달하지 않는다.</summary>
public sealed class MappingInputLog(string? directory = null) : IDisposable
{
    public const int RecentLineLimit = 2000;
    private readonly object _sync = new();
    private readonly Queue<string> _lines = new();
    private bool _closed;

    public string? CurrentFilePath { get; } = directory == null ? null : Path.Combine(
        directory, $"mapping-input-{DateTime.Now:yyyyMMdd_HHmmss_fff}-{Guid.NewGuid():N}.log");
    public event Action<string>? LineAdded;

    public string[] GetRecentLines()
    {
        lock (_sync) return _lines.ToArray();
    }

    public string ExportText()
    {
        lock (_sync)
            return CurrentFilePath != null && File.Exists(CurrentFilePath)
                ? File.ReadAllText(CurrentFilePath)
                : string.Join(Environment.NewLine, _lines);
    }

    public void Info(string message) => Write("", message);
    public void Warn(string message) => Write("WARN ", message);

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level}{AppLog.Sanitize(message)}";
        lock (_sync)
        {
            if (_closed) return;
            _lines.Enqueue(line);
            while (_lines.Count > RecentLineLimit) _lines.Dequeue();
            if (CurrentFilePath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CurrentFilePath)!);
                    File.AppendAllText(CurrentFilePath, line + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 파일 기록에 실패해도 매핑 창의 화면 기록은 유지한다.
                }
            }
        }
        LineAdded?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_sync) _closed = true;
    }
}
