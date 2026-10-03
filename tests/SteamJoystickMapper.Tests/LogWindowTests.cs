using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.UI;

namespace SteamJoystickMapper.Tests;

public class LogWindowTests
{
    [Fact]
    public void HistoryAndLiveEventsRemainCorrectAfterClosingAndReopening()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            LogWindow? first = null;
            LogWindow? reopened = null;
            using var inputLog = new MappingInputLog();
            try
            {
                var marker = "log-window-test-" + Guid.NewGuid();
                inputLog.Info(marker + " history");
                first = new LogWindow(inputLog);
                var firstList = (ListBox)first.FindName("LogList");
                Assert.Single(Lines(firstList, marker + " history"));

                AppLog.Info(marker + " main-only");
                DrainDispatcher();
                Assert.Empty(Lines(firstList, marker + " main-only"));

                inputLog.Info(marker + " live");
                DrainDispatcher();
                Assert.Single(Lines(firstList, marker + " live"));

                first.Close();
                inputLog.Info(marker + " closed");
                DrainDispatcher();
                Assert.Empty(Lines(firstList, marker + " closed"));

                reopened = new LogWindow(inputLog);
                var reopenedList = (ListBox)reopened.FindName("LogList");
                Assert.Single(Lines(reopenedList, marker + " history"));
                Assert.Single(Lines(reopenedList, marker + " live"));
                Assert.Single(Lines(reopenedList, marker + " closed"));

                inputLog.Info(marker + " reopened");
                DrainDispatcher();
                Assert.Single(Lines(reopenedList, marker + " reopened"));
                Assert.Empty(Lines(firstList, marker + " reopened"));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                first?.Close();
                reopened?.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Log window verification did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<string> Lines(ListBox list, string marker) =>
        list.Items.Cast<string>().Where(line => line.Contains(marker, StringComparison.Ordinal));

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
