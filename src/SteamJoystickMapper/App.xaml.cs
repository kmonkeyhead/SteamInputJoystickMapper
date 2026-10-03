using System.Windows;
using System.Windows.Threading;
using SteamJoystickMapper.Config;
using SteamJoystickMapper.Logging;

namespace SteamJoystickMapper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Initialize(AppPaths.Logs);
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error($"처리되지 않은 오류: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
