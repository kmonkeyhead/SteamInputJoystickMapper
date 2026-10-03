using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static SteamJoystickMapper.Localization.Loc;

namespace SteamJoystickMapper.UI;

/// <summary>코드로 만드는 간단한 대화상자들.</summary>
public static class Dialogs
{
    public static void ShowText(Window owner, string title, string text)
    {
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
        };
        var window = new Window
        {
            Title = title, Owner = owner, Width = 760, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border { Padding = new Thickness(10), Child = box },
        };
        window.ShowDialog();
    }

    /// <summary>긴 내용과 함께 예/아니요를 묻는다.</summary>
    public static bool Confirm(Window owner, string title, string header, string body, string yesText, string? noText = null)
    {
        var result = false;
        var window = new Window
        {
            Title = title, Owner = owner, Width = 680, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = yesText, IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = noText ?? T("취소", "Cancel"), IsCancel = true, Padding = new Thickness(14, 5, 14, 5) };
        yes.Click += (_, _) => { result = true; window.Close(); };
        no.Click += (_, _) => window.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var dock = new DockPanel { Margin = new Thickness(14) };
        var headerBlock = new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(headerBlock, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(headerBlock);
        dock.Children.Add(buttons);
        dock.Children.Add(new TextBox
        {
            Text = body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        window.Content = dock;
        window.ShowDialog();
        return result;
    }


    /// <summary>
    /// [Steam에 적용] 때 쓰로틀 처리 선택: "쓰로틀 사용 안 함" 또는 "쓰로틀 설정한 게임으로 덮음"(게임 드롭다운).
    /// 취소하면 Cancelled. 사용 안 함이면 AppId = null.
    /// </summary>
    public static (bool Cancelled, string? AppId) ChooseThrottle(Window owner, IReadOnlyList<Mapping.GameMapping> games, string? preselectAppId)
    {
        var result = (Cancelled: true, AppId: (string?)null);
        var window = new Window
        {
            Title = T("쓰로틀 설정", "Throttle"), Owner = owner, Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            Text = T("쓰로틀 매핑이 있는 게임이 있습니다. 쓰로틀은 장치 설정을 모든 게임이 같이 쓰므로 한 게임의 설정만 적용할 수 있습니다.",
                     "Some games have throttle mappings. The throttle uses the device layout shared by all games, so only one game's throttle setup can be applied."),
        });
        var none = new RadioButton { Content = T("쓰로틀 사용 안 함", "Don't use the throttle"), Margin = new Thickness(0, 0, 0, 8) };
        var use = new RadioButton { Content = T("쓰로틀 설정한 게임으로 덮음:", "Use the throttle setup of this game:"), Margin = new Thickness(0, 0, 0, 4) };
        var combo = new ComboBox { ItemsSource = games, DisplayMemberPath = nameof(Mapping.GameMapping.GameName), Margin = new Thickness(20, 0, 0, 0) };
        var pre = games.FirstOrDefault(g => g.AppId == preselectAppId);
        combo.SelectedItem = pre ?? games.FirstOrDefault();
        if (pre != null) use.IsChecked = true; else none.IsChecked = true;
        combo.SelectionChanged += (_, _) => use.IsChecked = true;
        panel.Children.Add(none);
        panel.Children.Add(use);
        panel.Children.Add(combo);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0),
            Text = T("고른 게임 외의 쓰로틀 매핑은 이번 적용에서 빠집니다. 고른 값은 다음에도 기본으로 선택됩니다.",
                     "Throttle mappings of other games are left out of this apply. Your choice is remembered for next time."),
        });
        var ok = new Button { Content = T("적용 계속", "Continue"), IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = T("취소", "Cancel"), IsCancel = true, Padding = new Thickness(14, 5, 14, 5) };
        ok.Click += (_, _) =>
        {
            result = (false, use.IsChecked == true ? (combo.SelectedItem as Mapping.GameMapping)?.AppId : null);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }

    /// <summary>후원하기 (한국어): 카카오페이 QR 코드 + Ko-fi 버튼. MWOLab의 후원 창과 같은 구성.</summary>
    public static void ShowDonate(Window owner, string kofiUrl)
    {
        var window = new Window
        {
            Title = "후원하기", Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "후원하기", FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 12) });
        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/kakaopay-donate-qr.png")),
                Width = 260, Stretch = Stretch.Uniform, ToolTip = "카카오페이 후원 QR 코드",
            },
        });
        var kofi = new Button { Content = "Ko-fi로 후원하기", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        kofi.Click += (_, _) => MainWindow.OpenUrl(kofiUrl);
        panel.Children.Add(kofi);
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.Content = panel;
        window.ShowDialog();
    }
}
