using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;
using Vortice.DirectInput;

namespace SteamJoystickMapper.Devices;

/// <summary>DirectInput으로 연결된 조이스틱/HID 게임 장치를 검색한다.</summary>
public static class DeviceScanner
{
    public static IReadOnlyList<DeviceInfo> Scan()
    {
        var result = new List<DeviceInfo>();
        try
        {
            using var di = DInput.DirectInput8Create();
            foreach (var instance in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                try
                {
                    using var device = di.CreateDevice(instance.InstanceGuid);
                    var caps = device.Capabilities;
                    var props = device.Properties;
                    var (vid, pid) = ReadVidPid(instance, props);
                    result.Add(new DeviceInfo
                    {
                        Name = string.IsNullOrWhiteSpace(instance.ProductName) ? instance.InstanceName : instance.ProductName.Trim(),
                        InstanceGuid = instance.InstanceGuid,
                        ProductGuid = instance.ProductGuid,
                        Vid = vid,
                        Pid = pid,
                        InterfacePath = SafeGet(() => props.InterfacePath) ?? "",
                        Axes = ReadAxes(device),
                        ButtonCount = caps.ButtonCount,
                        PovCount = caps.PovCount,
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"장치 정보를 읽을 수 없음: {instance.ProductName} ({ex.Message})");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"DirectInput 초기화 실패: {ex.Message}");
        }

        // 같은 제품이 여러 개면 번호를 붙여 구분
        foreach (var group in result.GroupBy(d => (d.Vid, d.Pid)).Where(g => g.Count() > 1))
        {
            var i = 1;
            foreach (var d in group) d.DuplicateIndex = i++;
        }
        foreach (var d in result) AppLog.Info($"{d.Name} detected");
        return result;
    }

    private static (ushort Vid, ushort Pid) ReadVidPid(DeviceInstance instance, ObjectProperties props)
    {
        var vid = SafeGet(() => props.VendorId);
        var pid = SafeGet(() => props.ProductId);
        if (vid == 0 && pid == 0)
        {
            // HID 장치의 ProductGuid Data1 = PID << 16 | VID
            var data1 = BitConverter.ToUInt32(instance.ProductGuid.ToByteArray(), 0);
            vid = (int)(data1 & 0xFFFF);
            pid = (int)(data1 >> 16);
        }
        return ((ushort)vid, (ushort)pid);
    }

    private static List<JoyAxis> ReadAxes(IDirectInputDevice8 device)
    {
        var axes = new HashSet<JoyAxis>();
        var sliders = 0;
        foreach (var obj in device.GetObjects(DeviceObjectTypeFlags.Axis))
        {
            var t = obj.ObjectType;
            if (t == ObjectGuid.XAxis) axes.Add(JoyAxis.X);
            else if (t == ObjectGuid.YAxis) axes.Add(JoyAxis.Y);
            else if (t == ObjectGuid.ZAxis) axes.Add(JoyAxis.Z);
            else if (t == ObjectGuid.RxAxis) axes.Add(JoyAxis.Rx);
            else if (t == ObjectGuid.RyAxis) axes.Add(JoyAxis.Ry);
            else if (t == ObjectGuid.RzAxis) axes.Add(JoyAxis.Rz);
            else if (t == ObjectGuid.Slider) axes.Add(sliders++ == 0 ? JoyAxis.Slider0 : JoyAxis.Slider1);
        }
        return axes.OrderBy(a => (int)a).ToList();
    }

    private static T? SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default; }
    }
}
