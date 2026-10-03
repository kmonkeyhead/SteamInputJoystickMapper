using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using SteamJoystickMapper.Logging;
using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.UI;

public partial class LogWindow : Window
{
    private readonly MappingInputLog _log;
    private bool _closed;

    public LogWindow(MappingInputLog log)
    {
        _log = log;
        InitializeComponent();
        FilePathText.Text = _log.CurrentFilePath ?? "";
        foreach (var line in _log.GetRecentLines()) AddLine(line);
        _log.LineAdded += OnLineAdded;
        Closed += (_, _) =>
        {
            _closed = true;
            _log.LineAdded -= OnLineAdded;
        };
    }

    private void OnLineAdded(string line)
    {
        if (_closed || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (!_closed) AddLine(line); });
    }

    private void AddLine(string line)
    {
        LogList.Items.Add(line);
        while (LogList.Items.Count > MappingInputLog.RecentLineLimit) LogList.Items.RemoveAt(0);
        if (AutoScrollBox.IsChecked == true) LogList.ScrollIntoView(line);
    }

    private string DisplayedText() => string.Join(Environment.NewLine, LogList.Items.Cast<string>());

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DisplayedText()); }
        catch (Exception ex) { ShowError(T("복사 실패", "Copy failed"), ex); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"mapping-input-{DateTime.Now:yyyyMMdd_HHmmss}.log",
            Filter = T("로그 파일 (*.log)|*.log|텍스트 파일 (*.txt)|*.txt", "Log file (*.log)|*.log|Text file (*.txt)|*.txt"),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // 이 매핑 창의 입력 기록만 저장한다.
            var source = _log.CurrentFilePath;
            if (source != null && string.Equals(Path.GetFullPath(source), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
                return;
            var text = _log.ExportText();
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(false));
        }
        catch (Exception ex) { ShowError(T("저장 실패", "Save failed"), ex); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    private void ShowError(string title, Exception ex)
    {
        _log.Warn($"{title}: {ex.Message}");
        MessageBox.Show(this, AppLog.Sanitize(ex.Message), title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
