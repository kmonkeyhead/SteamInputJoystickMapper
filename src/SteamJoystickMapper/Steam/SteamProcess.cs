using System.Diagnostics;
using System.IO;

namespace SteamJoystickMapper.Steam;

public static class SteamProcess
{
    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("steam");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>"steam.exe -shutdown"으로 정상 종료를 요청하고 종료될 때까지 기다린다.</summary>
    public static async Task<bool> ShutdownAsync(string steamPath, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsRunning()) return true;
        var exe = Path.Combine(steamPath, "steam.exe");
        if (!File.Exists(exe)) return false;
        Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })?.Dispose();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            if (!IsRunning())
            {
                // Steam이 종료 직전에 config.vdf 등을 저장하므로 잠시 더 기다린다.
                await Task.Delay(1500, ct);
                return true;
            }
        }
        return false;
    }

    public static void Start(string steamPath)
    {
        var exe = Path.Combine(steamPath, "steam.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
    }
}
