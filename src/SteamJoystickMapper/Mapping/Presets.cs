namespace SteamJoystickMapper.Mapping;

public static class Presets
{
    /// <summary>
    /// 사양서 22장 AC8 예제: T.A320 → Xbox.
    /// Stick X/Y → 왼쪽 스틱, Twist → 오른쪽 스틱 X, Trigger → B, 버튼 2/3/4 → A/X/Y, Hat → D-Pad.
    /// </summary>
    public static List<MappingBinding> AceCombatFlightStick(DeviceIdentity device)
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
