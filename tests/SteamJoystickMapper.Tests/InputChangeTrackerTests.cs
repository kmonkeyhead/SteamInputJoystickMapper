using System.Globalization;
using SteamJoystickMapper.Devices;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;

namespace SteamJoystickMapper.Tests;

public class InputChangeTrackerTests
{
    private static InputSnapshot Snapshot(double x = 0, double throttle = -1, bool down = false, int pov = -1) => new()
    {
        Axes = { [JoyAxis.X] = x, [JoyAxis.Slider0] = throttle },
        Buttons = new[] { down, false },
        Povs = new[] { pov },
    };

    [Fact]
    public void InitialStateIncludesIdleAxesHeldButtonsAndPov()
    {
        var tracker = new InputChangeTracker();
        var line = tracker.Update(Snapshot(down: true, pov: 4500), TimeSpan.Zero);
        Assert.StartsWith("STATE ", line);
        Assert.Contains("AXIS X=0.000 (50.0%)", line);
        Assert.Contains("AXIS Slider0=-1.000 (0.0%)", line);
        Assert.Contains("BUTTONS DOWN: 1", line);
        Assert.Contains("POV 1 UP-RIGHT (4500)", line);
    }

    [Fact]
    public void IdleAndSmallAxisNoiseDoNotFloodLog()
    {
        var tracker = new InputChangeTracker();
        tracker.Update(Snapshot(), TimeSpan.Zero);
        for (var ms = 33; ms <= 990; ms += 33)
            Assert.Null(tracker.Update(Snapshot(x: ms % 2 == 0 ? 0.009 : -0.009), TimeSpan.FromMilliseconds(ms)));
    }

    [Fact]
    public void SlowMovementAccumulatesRelativeToLastLoggedValue()
    {
        var tracker = new InputChangeTracker();
        tracker.Update(Snapshot(), TimeSpan.Zero);
        Assert.Null(tracker.Update(Snapshot(x: 0.009), TimeSpan.FromMilliseconds(100)));
        Assert.Null(tracker.Update(Snapshot(x: 0.018), TimeSpan.FromMilliseconds(200)));
        Assert.Contains("AXIS X=+0.027", tracker.Update(Snapshot(x: 0.027), TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void AxisBurstIsCoalescedAndFinalPositionIsLoggedAfterInterval()
    {
        var tracker = new InputChangeTracker();
        tracker.Update(Snapshot(), TimeSpan.Zero);
        Assert.Null(tracker.Update(Snapshot(x: 0.2), TimeSpan.FromMilliseconds(33)));
        Assert.Null(tracker.Update(Snapshot(x: 0.7), TimeSpan.FromMilliseconds(66)));
        var line = tracker.Update(Snapshot(x: 0.7), TimeSpan.FromMilliseconds(100));
        Assert.Contains("AXIS X=+0.700 (85.0%)", line);
        Assert.Null(tracker.Update(Snapshot(x: 0.7), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void ButtonPressAndReleaseAreNotDelayedByAxisRateLimit()
    {
        var tracker = new InputChangeTracker();
        tracker.Update(Snapshot(), TimeSpan.Zero);
        var pressed = tracker.Update(Snapshot(x: 0.5, down: true), TimeSpan.FromMilliseconds(33));
        Assert.Contains("BUTTON 1 DOWN", pressed);
        Assert.DoesNotContain("AXIS", pressed);
        var released = tracker.Update(Snapshot(x: 0.5), TimeSpan.FromMilliseconds(66));
        Assert.Contains("BUTTON 1 UP", released);
        Assert.DoesNotContain("AXIS", released);
        Assert.Contains("AXIS X=+0.500", tracker.Update(Snapshot(x: 0.5), TimeSpan.FromMilliseconds(100)));
    }

    [Theory]
    [InlineData(0, "UP (0)")]
    [InlineData(4500, "UP-RIGHT (4500)")]
    [InlineData(9000, "RIGHT (9000)")]
    [InlineData(13500, "DOWN-RIGHT (13500)")]
    [InlineData(18000, "DOWN (18000)")]
    [InlineData(22500, "DOWN-LEFT (22500)")]
    [InlineData(27000, "LEFT (27000)")]
    [InlineData(31500, "UP-LEFT (31500)")]
    public void PovChangesIncludeDiagonalsAndRawAngle(int pov, string expected)
    {
        var tracker = new InputChangeTracker();
        tracker.Update(Snapshot(), TimeSpan.Zero);
        Assert.Contains("POV 1 " + expected, tracker.Update(Snapshot(pov: pov), TimeSpan.FromMilliseconds(33)));
        Assert.Contains("POV 1 NEUTRAL (-1)", tracker.Update(Snapshot(), TimeSpan.FromMilliseconds(66)));
    }

    [Fact]
    public void SnapshotsAreCopiedSoReusedButtonArrayStillProducesRelease()
    {
        var tracker = new InputChangeTracker();
        var snapshot = Snapshot(down: true);
        tracker.Update(snapshot, TimeSpan.Zero);
        snapshot.Buttons[0] = false;
        Assert.Contains("BUTTON 1 UP", tracker.Update(snapshot, TimeSpan.FromMilliseconds(33)));
    }

    [Fact]
    public void ReconnectResetsBaselineAndRecordsNewInitialState()
    {
        var tracker = new InputChangeTracker();
        tracker.Update(Snapshot(), TimeSpan.Zero);
        tracker.Reset();
        var line = tracker.Update(Snapshot(x: 0.5, down: true), TimeSpan.FromMilliseconds(33));
        Assert.StartsWith("STATE ", line);
        Assert.Contains("AXIS X=+0.500", line);
        Assert.Contains("BUTTONS DOWN: 1", line);
    }

    [Fact]
    public void AxisNumbersRemainReadableAcrossSystemCultures()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var line = new InputChangeTracker().Update(Snapshot(x: 0.25), TimeSpan.Zero);
            Assert.Contains("AXIS X=+0.250 (62.5%)", line);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
