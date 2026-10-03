namespace SteamJoystickMapper.Mapping;

public static class Presets
{
    /// <summary>
    /// AC8 예제 (사용자가 T.A320 Pilot으로 ACE COMBAT 8에 맞춘 매핑, 2026-10-03):
    /// 스틱 X/Y → 왼쪽 스틱, 쓰로틀 0%쪽 → RT(0~70%, 0%에서 최대), 버튼 14·17 → LT(최대),
    /// 버튼 1/2/3/4 → B/A/X/Y, 트위스트 왼쪽/오른쪽 → LB/RB(데드존 60%), 버튼 8/9 → LS/RS, Hat → D-Pad.
    /// 장치에 없는 입력은 건너뛴다.
    /// </summary>
    public static List<MappingBinding> AceCombat8(DeviceIdentity device)
    {
        var list = new List<MappingBinding>();
        void Axis(JoyAxis axis, XboxOutput target, int deadZone)
        {
            if (device.Axes.Contains(axis))
                list.Add(new MappingBinding { Source = PhysicalInput.FromAxis(axis), Target = target, DeadZone = deadZone });
        }
        void Half(JoyAxis axis, int sign, XboxOutput target, int deadZone = 0, int? low = null, int? high = null)
        {
            if (device.Axes.Contains(axis))
                list.Add(new MappingBinding { Source = PhysicalInput.FromAxisHalf(axis, sign), Target = target, DeadZone = deadZone, RangeLow = low, RangeHigh = high });
        }
        void Button(int index, XboxOutput target)
        {
            if (index < device.ButtonCount)
                list.Add(new MappingBinding { Source = PhysicalInput.FromButton(index), Target = target });
        }

        Axis(JoyAxis.X, XboxOutput.LeftStickX, 5);
        Axis(JoyAxis.Y, XboxOutput.LeftStickY, 5);
        Button(13, XboxOutput.LT);
        Button(16, XboxOutput.LT);
        Half(JoyAxis.Slider0, -1, XboxOutput.RT, low: 0, high: 70);
        Button(1, XboxOutput.A);
        Button(0, XboxOutput.B);
        Button(2, XboxOutput.X);
        Button(3, XboxOutput.Y);
        var twist = device.Axes.Contains(JoyAxis.Rz) ? JoyAxis.Rz : JoyAxis.Z;
        Half(twist, -1, XboxOutput.LB, deadZone: 60);
        Half(twist, +1, XboxOutput.RB, deadZone: 60);
        Button(7, XboxOutput.LS);
        Button(8, XboxOutput.RS);
        if (device.PovCount > 0) list.AddRange(PovToDpad(0));
        return list;
    }

    /// <summary>
    /// 기본 비행 스틱 매핑 (테스트 기준): Stick X/Y → 왼쪽 스틱, Twist → 오른쪽 스틱 X,
    /// Trigger → B, 버튼 2/3/4 → A/X/Y, Hat → D-Pad.
    /// </summary>
    public static List<MappingBinding> BasicFlightStick(DeviceIdentity device)
    {
        var list = new List<MappingBinding>();
        void Axis(JoyAxis axis, XboxOutput target, int deadZone = 5)
        {
            if (device.Axes.Contains(axis))
                list.Add(new MappingBinding { Source = PhysicalInput.FromAxis(axis), Target = target, DeadZone = deadZone });
        }
        void Button(int index, XboxOutput target)
        {
            if (index < device.ButtonCount)
                list.Add(new MappingBinding { Source = PhysicalInput.FromButton(index), Target = target });
        }

        Axis(JoyAxis.X, XboxOutput.LeftStickX);
        Axis(JoyAxis.Y, XboxOutput.LeftStickY);
        Axis(device.Axes.Contains(JoyAxis.Rz) ? JoyAxis.Rz : JoyAxis.Z, XboxOutput.RightStickX, 8);
        Button(0, XboxOutput.B);
        Button(1, XboxOutput.A);
        Button(2, XboxOutput.X);
        Button(3, XboxOutput.Y);
        if (device.PovCount > 0) list.AddRange(PovToDpad(0));
        return list;
    }

    public static IEnumerable<MappingBinding> PovToDpad(int pov)
    {
        yield return new MappingBinding { Source = PhysicalInput.FromPov(pov, PovDirection.Up), Target = XboxOutput.DPadUp };
        yield return new MappingBinding { Source = PhysicalInput.FromPov(pov, PovDirection.Down), Target = XboxOutput.DPadDown };
        yield return new MappingBinding { Source = PhysicalInput.FromPov(pov, PovDirection.Left), Target = XboxOutput.DPadLeft };
        yield return new MappingBinding { Source = PhysicalInput.FromPov(pov, PovDirection.Right), Target = XboxOutput.DPadRight };
    }
}
