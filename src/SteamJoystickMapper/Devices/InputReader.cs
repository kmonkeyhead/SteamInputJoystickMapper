using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using Vortice.DirectInput;

namespace SteamJoystickMapper.Devices;

/// <summary>선택한 장치를 비독점/백그라운드 모드로 폴링한다 (게임이나 Steam의 입력을 방해하지 않음).</summary>
public sealed class InputReader : IDisposable
{
    private readonly IDirectInput8 _di;
    private readonly IDirectInputDevice8 _device;
    private readonly DeviceInfo _info;

    public InputReader(DeviceInfo info, IntPtr windowHandle)
    {
        _info = info;
        _di = DInput.DirectInput8Create();
        _device = _di.CreateDevice(info.InstanceGuid);
        _device.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
        _device.SetDataFormat<RawJoystickState>();
        foreach (var obj in _device.GetObjects(DeviceObjectTypeFlags.Axis))
        {
            try
            {
                _device.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(-32768, 32767);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"축 범위 설정 실패 ({obj.Name}): {ex.Message}");
            }
        }
        _device.Acquire();
    }

    public InputSnapshot? Poll()
    {
        try
        {
            if (_device.Poll().Failure)
            {
                _device.Acquire();
                _device.Poll();
            }
            var state = _device.GetCurrentJoystickState();
            var snap = new InputSnapshot
            {
                Buttons = state.Buttons.Take(_info.ButtonCount).ToArray(),
                Povs = state.PointOfViewControllers.Take(_info.PovCount).ToArray(),
            };
            foreach (var axis in _info.Axes)
            {
                var raw = axis switch
                {
                    JoyAxis.X => state.X,
                    JoyAxis.Y => state.Y,
                    JoyAxis.Z => state.Z,
                    JoyAxis.Rx => state.RotationX,
                    JoyAxis.Ry => state.RotationY,
                    JoyAxis.Rz => state.RotationZ,
                    JoyAxis.Slider0 => state.Sliders[0],
                    _ => state.Sliders[1],
                };
                snap.Axes[axis] = Math.Clamp((raw + 0.5) / 32767.5, -1, 1);
            }
            return snap;
        }
        catch (Exception)
        {
            // 장치 분리 등. 호출 측에서 null 처리.
            return null;
        }
    }

    public void Dispose()
    {
        try { _device.Unacquire(); } catch { /* 이미 해제됨 */ }
        _device.Dispose();
        _di.Dispose();
    }
}

/// <summary>
/// 사양서 6장: 기준 상태 대비 변화를 감지해 "어떤 입력을 움직였는지" 판단한다.
/// </summary>
public sealed class InputDetector
{
    private const double AxisThreshold = 0.5;
    /// <summary>LT/RT 감지: 작은 범위(예: 쓰로틀 10~20%)도 잡도록 3%만 움직여도 축으로 본다.</summary>
    private const double PreferAxisThreshold = 0.06;
    private InputSnapshot? _baseline;

    public void Reset(InputSnapshot? baseline) => _baseline = baseline;

    /// <summary>감지 시작 때보다 축이 움직인 방향 (-1 / +1).</summary>
    public int AxisDirection(InputSnapshot current, JoyAxis axis) =>
        current.Axes.GetValueOrDefault(axis) - (_baseline?.Axes.GetValueOrDefault(axis) ?? 0) < 0 ? -1 : 1;

    /// <param name="preferAxis">
    /// 축을 버튼보다 먼저 본다 (LT/RT 감지용). 쓰로틀을 끝까지 내리면 눌리는 끝단 버튼(T.A320의 버튼 17 등)보다 축을 잡기 위해.
    /// </param>
    public PhysicalInput? Detect(InputSnapshot current, bool preferAxis = false)
    {
        if (_baseline == null)
        {
            _baseline = current;
            return null;
        }

        if (preferAxis)
        {
            var moved = current.Axes
                .Select(kv => (Axis: kv.Key, Delta: Math.Abs(kv.Value - _baseline.Axes.GetValueOrDefault(kv.Key))))
                .Where(x => x.Delta > PreferAxisThreshold).OrderByDescending(x => x.Delta).FirstOrDefault();
            if (moved.Delta > 0) return PhysicalInput.FromAxis(moved.Axis);
        }

        for (var i = 0; i < current.Buttons.Length; i++)
            if (current.Buttons[i] && !(i < _baseline.Buttons.Length && _baseline.Buttons[i]))
                return PhysicalInput.FromButton(i);

        for (var i = 0; i < current.Povs.Length; i++)
        {
            var dir = InputSnapshot.PovToDirection(current.Povs[i]);
            var baseDir = i < _baseline.Povs.Length ? InputSnapshot.PovToDirection(_baseline.Povs[i]) : null;
            if (dir != null && dir != baseDir) return PhysicalInput.FromPov(i, dir.Value);
        }

        JoyAxis? best = null;
        var bestDelta = AxisThreshold;
        foreach (var (axis, value) in current.Axes)
        {
            var delta = Math.Abs(value - _baseline.Axes.GetValueOrDefault(axis));
            if (delta > bestDelta)
            {
                bestDelta = delta;
                best = axis;
            }
        }
        return best is { } a ? PhysicalInput.FromAxis(a) : null;
    }
}
