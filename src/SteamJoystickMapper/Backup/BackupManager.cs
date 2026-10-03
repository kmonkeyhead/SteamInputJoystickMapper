using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using SteamJoystickMapper.Logging;
using SteamJoystickMapper.Mapping;

namespace SteamJoystickMapper.Backup;

public sealed class BackupFileEntry
{
    public string OriginalPath { get; set; } = "";
    /// <summary>백업 폴더 내 파일명. 원본이 없던 경우 null.</summary>
    public string? StoredName { get; set; }
    public bool Existed { get; set; }
    public string? Sha256 { get; set; }
    public DateTime? LastWriteTime { get; set; }
}

public sealed class BackupManifest
{
    public DateTime CreatedAt { get; set; }
    public string AppId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string ControllerId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public List<BackupFileEntry> Files { get; set; } = new();

    // 복원 UI용
    public string Folder { get; set; } = "";
    public string Display => $"{CreatedAt:yyyy-MM-dd HH:mm}   {GameName} ({AppId})   {DeviceName}";
}

/// <summary>
/// 사양서 18/19장: Steam 설정 변경 전 자동 백업과 복원.
/// 원본 파일은 절대 삭제하지 않는다 (복원 시 새로 생긴 파일은 백업 폴더로 이동).
/// </summary>
public sealed class BackupManager(string backupRoot)
{
    private const string ManifestName = "backup.json";
    public string BackupRoot { get; } = backupRoot;

    public BackupManifest Create(BackupManifest meta, IEnumerable<string> files)
    {
        Directory.CreateDirectory(BackupRoot);
        var stamp = DateTime.Now;
        var folder = Path.Combine(BackupRoot, stamp.ToString("yyyy-MM-dd_HHmmss"));
        for (var i = 2; Directory.Exists(folder); i++)
            folder = Path.Combine(BackupRoot, $"{stamp:yyyy-MM-dd_HHmmss}_{i}");
        Directory.CreateDirectory(folder);

        meta.CreatedAt = stamp;
        meta.Folder = folder;
        meta.Files.Clear();
        var index = 0;
        foreach (var path in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = new BackupFileEntry { OriginalPath = path, Existed = File.Exists(path) };
            if (entry.Existed)
            {
                entry.StoredName = $"{index++:D2}_{Path.GetFileName(path)}";
                var dest = Path.Combine(folder, entry.StoredName);
                File.Copy(path, dest);
                entry.Sha256 = ComputeSha256(dest);
                if (entry.Sha256 != ComputeSha256(path))
                    throw new IOException($"백업 검증 실패 (파일이 복사 중 변경됨): {Path.GetFileName(path)}");
                entry.LastWriteTime = File.GetLastWriteTime(path);
            }
            meta.Files.Add(entry);
        }
        File.WriteAllText(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(meta, ProfileStore.JsonOptions));
        AppLog.Info($"Backup created ({Path.GetFileName(folder)}, 파일 {meta.Files.Count(f => f.Existed)}개)");
        return meta;
    }

    public IReadOnlyList<BackupManifest> List()
    {
        if (!Directory.Exists(BackupRoot)) return Array.Empty<BackupManifest>();
        var list = new List<BackupManifest>();
        foreach (var dir in Directory.EnumerateDirectories(BackupRoot))
        {
            var file = Path.Combine(dir, ManifestName);
            if (!File.Exists(file)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file), ProfileStore.JsonOptions);
                if (m == null) continue;
                m.Folder = dir;
                list.Add(m);
            }
            catch (JsonException ex)
            {
                AppLog.Warn($"백업 정보를 읽을 수 없음: {Path.GetFileName(dir)} ({ex.Message})");
            }
        }
        return list.OrderByDescending(m => m.CreatedAt).ToList();
    }

    /// <summary>
    /// 백업 시점 상태로 되돌린다. 복원 전 현재 파일도 백업 폴더 안에 보관한다(pre_restore_*).
    /// </summary>
    public void Restore(BackupManifest backup)
    {
        var safety = Path.Combine(backup.Folder, $"pre_restore_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(safety);
        var i = 0;
        foreach (var entry in backup.Files)
        {
            var current = entry.OriginalPath;
            if (File.Exists(current))
            {
                var keep = Path.Combine(safety, $"{i++:D2}_{Path.GetFileName(current)}");
                if (entry.Existed) File.Copy(current, keep);
                else File.Move(current, keep); // 백업 당시 없던 파일: 삭제하지 않고 백업 폴더로 이동
            }
            if (!entry.Existed || entry.StoredName == null) continue;

            var stored = Path.Combine(backup.Folder, entry.StoredName);
            if (entry.Sha256 != null && ComputeSha256(stored) != entry.Sha256)
                throw new IOException($"백업 파일이 손상되었습니다: {entry.StoredName}");
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            var tmp = current + ".sjm-restore.tmp";
            File.Copy(stored, tmp, overwrite: true);
            File.Move(tmp, current, overwrite: true);
            if (entry.Sha256 != null && ComputeSha256(current) != entry.Sha256)
                throw new IOException($"복원 검증 실패: {Path.GetFileName(current)}");
        }
        AppLog.Info($"Backup restored ({Path.GetFileName(backup.Folder)})");
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
