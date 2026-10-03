using System.Diagnostics;
using System.IO;
using System.Windows;
using SteamJoystickMapper.Backup;
using SteamJoystickMapper.Logging;

namespace SteamJoystickMapper.UI;

public partial class BackupHistoryWindow : Window
{
    private readonly BackupManager _backups;
    private readonly MainWindow _main;

    public BackupHistoryWindow(BackupManager backups, MainWindow main)
    {
        InitializeComponent();
        _backups = backups;
        _main = main;
        Reload();
    }

    private void Reload()
    {
        BackupList.ItemsSource = _backups.List();
        if (BackupList.Items.Count > 0) BackupList.SelectedIndex = 0;
        else DetailText.Text = "백업이 없습니다.";
    }

    private BackupManifest? Selected => BackupList.SelectedItem as BackupManifest;

    private void BackupList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var b = Selected;
        if (b == null) return;
        DetailText.Text = AppLog.Sanitize(
            $"프로필: {b.ProfileName}   Controller ID: {b.ControllerId}\n" +
            string.Join("\n", b.Files.Select(f =>
                $"{(f.Existed ? "●" : "○ (원래 없음)")} {f.OriginalPath}" + (f.Sha256 != null ? $"\n    SHA256 {f.Sha256}  수정 {f.LastWriteTime:yyyy-MM-dd HH:mm:ss}" : ""))));
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } b) return;
        await _main.RestoreAsync(b);
        Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Selected?.Folder ?? _backups.BackupRoot;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
