using System.Windows;
using System.Windows.Threading;
using SteamJoystickMapper.Config;
using SteamJoystickMapper.Logging;

using static SteamJoystickMapper.Localization.Loc;

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
        AppLog.Error(T("처리되지 않은 오류: ", "Unhandled error: ") + e.Exception);
        MessageBox.Show(e.Exception.Message, T("오류", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
