using System.Collections.Concurrent;
using SteamJoystickMapper.Logging;

namespace SteamJoystickMapper.Tests;

public class MappingInputLogTests
{
    [Fact]
    public void InputRecordsDoNotEnterMainLogEventsOrHistory()
    {
        var marker = "mapping-only-" + Guid.NewGuid();
        var mainLines = new ConcurrentQueue<string>();
        Action<string> onMainLine = mainLines.Enqueue;
        AppLog.LineAdded += onMainLine;
        try
        {
            using var log = new MappingInputLog();
            log.Info(marker + " BUTTON 1 DOWN");
            log.Warn(marker + " device unavailable");
            Assert.Equal(2, log.GetRecentLines().Length);
            Assert.DoesNotContain(mainLines, line => line.Contains(marker));
            Assert.DoesNotContain(AppLog.GetRecentLines(), line => line.Contains(marker));
        }
        finally { AppLog.LineAdded -= onMainLine; }
    }

    [Fact]
    public void SeparateEditorsHaveSeparateRecordsAndExports()
    {
        using var first = new MappingInputLog();
        using var second = new MappingInputLog();
        first.Info("FIRST BUTTON 1 DOWN");
        second.Info("SECOND AXIS X=+0.500");
        Assert.DoesNotContain("SECOND", first.ExportText());
        Assert.DoesNotContain("FIRST", second.ExportText());
        Assert.Contains("FIRST BUTTON 1 DOWN", first.ExportText());
        Assert.Contains("SECOND AXIS X=+0.500", second.ExportText());
    }

    [Fact]
    public void ClosedEditorDoesNotAcceptMoreInputRecords()
    {
        var log = new MappingInputLog();
        var linesRaised = 0;
        log.LineAdded += _ => linesRaised++;
        log.Info("BUTTON 1 DOWN");
        log.Dispose();
        log.Info("BUTTON 1 UP");
        Assert.Single(log.GetRecentLines());
        Assert.Equal(1, linesRaised);
        Assert.DoesNotContain("BUTTON 1 UP", log.ExportText());
    }

    [Fact]
    public void ExportKeepsFullEditorRecordingWhenDisplayHistoryIsBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mapping-input-test-" + Guid.NewGuid());
        using var log = new MappingInputLog(directory);
        try
        {
            for (var i = 0; i <= MappingInputLog.RecentLineLimit; i++) log.Info($"sample-{i:D5}");
            Assert.Equal(MappingInputLog.RecentLineLimit, log.GetRecentLines().Length);
            Assert.DoesNotContain(log.GetRecentLines(), line => line.Contains("sample-00000"));
            Assert.Contains("sample-00000", log.ExportText());
            Assert.Contains($"sample-{MappingInputLog.RecentLineLimit:D5}", log.ExportText());
            Assert.Contains("mapping-input-", Path.GetFileName(log.CurrentFilePath));
        }
        finally
        {
            // 이 테스트가 만든 파일과 빈 폴더만 정리한다.
            if (File.Exists(log.CurrentFilePath)) File.Delete(log.CurrentFilePath!);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
