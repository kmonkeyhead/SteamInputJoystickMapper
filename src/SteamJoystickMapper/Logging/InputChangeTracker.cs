using System.Globalization;
using SteamJoystickMapper.Devices;
using SteamJoystickMapper.Mapping;

namespace SteamJoystickMapper.Logging;

/// <summary>축의 떨림/과도한 기록을 줄이고 버튼과 POV의 상태 변화를 기록한다.</summary>
public sealed class InputChangeTracker
{
    // -1..1 축에서 0.02 차이는 전체 이동 범위의 1%다.
    public const double AxisChangeThreshold = 0.02;
    public static readonly TimeSpan AxisLogInterval = TimeSpan.FromMilliseconds(100);
    private readonly Dictionary<JoyAxis, (double Value, TimeSpan At)> _axes = new();
    private bool[]? _buttons;
    private int[] _povs = Array.Empty<int>();

    public void Reset()
    {
        _axes.Clear();
        _buttons = null;
        _povs = Array.Empty<int>();
    }

    /// <summary>초기 상태 또는 변화가 있을 때 한 줄을 반환한다. 시간은 단조 증가하는 경과 시간이다.</summary>
    public string? Update(InputSnapshot snapshot, TimeSpan elapsed)
    {
        var initial = _buttons == null;
        var changes = new List<string>();
        foreach (var (axis, value) in snapshot.Axes.OrderBy(a => (int)a.Key))
        {
            if (!double.IsFinite(value)) continue;
            if (!_axes.TryGetValue(axis, out var previous) ||
                (Math.Abs(value - previous.Value) >= AxisChangeThreshold && elapsed - previous.At >= AxisLogInterval))
            {
                changes.Add(AxisText(axis, value));
                _axes[axis] = (value, elapsed);
            }
        }
        if (initial)
        {
            var down = Enumerable.Range(0, snapshot.Buttons.Length).Where(i => snapshot.Buttons[i]).Select(i => i + 1);
            changes.Add("BUTTONS DOWN: " + (snapshot.Buttons.Any(b => b) ? string.Join(",", down) : "none"));
        }
        else
        {
            for (var i = 0; i < snapshot.Buttons.Length; i++)
                if (snapshot.Buttons[i] != (i < _buttons!.Length && _buttons[i]))
                    changes.Add($"BUTTON {i + 1} {(snapshot.Buttons[i] ? "DOWN" : "UP")}");
        }
        for (var i = 0; i < snapshot.Povs.Length; i++)
            if (initial || i >= _povs.Length || snapshot.Povs[i] != _povs[i])
                changes.Add($"POV {i + 1} {PovText(snapshot.Povs[i])}");

        _buttons = (bool[])snapshot.Buttons.Clone();
        _povs = (int[])snapshot.Povs.Clone();
        return changes.Count == 0 ? null : (initial ? "STATE " : "") + string.Join("; ", changes);
    }

    private static string AxisText(JoyAxis axis, double value) =>
        string.Create(CultureInfo.InvariantCulture, $"AXIS {axis}={value:+0.000;-0.000;0.000} ({(value + 1) * 50:0.0}%)");

    private static string PovText(int value)
    {
        if (value == -1) return "NEUTRAL (-1)";
        if (value < 0 || value > 36000) return $"INVALID ({value})";
        var direction = ((value + 2250) / 4500) % 8;
        var name = direction switch
        {
            0 => "UP", 1 => "UP-RIGHT", 2 => "RIGHT", 3 => "DOWN-RIGHT",
            4 => "DOWN", 5 => "DOWN-LEFT", 6 => "LEFT", _ => "UP-LEFT",
        };
        return $"{name} ({value})";
    }
}
