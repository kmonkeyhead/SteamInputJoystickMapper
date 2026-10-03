using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SteamJoystickMapper.Steam;

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
    public static bool Confirm(Window owner, string title, string header, string body, string yesText, string noText = "취소")
    {
        var result = false;
        var window = new Window
        {
            Title = title, Owner = owner, Width = 680, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = yesText, IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = noText, IsCancel = true, Padding = new Thickness(14, 5, 14, 5) };
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

    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        string? result = null;
        var window = new Window
        {
            Title = title, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10), Padding = new Thickness(3) };
        var ok = new Button { Content = "확인", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => { result = box.Text.Trim(); window.Close(); };
        cancel.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        window.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static SteamGame? PickGame(Window owner, string title, IReadOnlyList<SteamGame> games)
    {
        SteamGame? result = null;
        var window = new Window
        {
            Title = title, Owner = owner, Width = 520, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(3) };
        var list = new ListBox { ItemsSource = games };
        void Filter()
        {
            var q = search.Text.Trim();
            list.ItemsSource = q.Length == 0 ? games
                : games.Where(g => g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || g.AppId.Contains(q)).ToList();
        }
        search.TextChanged += (_, _) => Filter();
        var ok = new Button { Content = "선택", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => { result = list.SelectedItem as SteamGame; if (result != null) window.Close(); };
        list.MouseDoubleClick += (_, _) => { result = list.SelectedItem as SteamGame; if (result != null) window.Close(); };
        cancel.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var dock = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(search);
        dock.Children.Add(buttons);
        dock.Children.Add(list);
        window.Content = dock;
        window.Loaded += (_, _) => search.Focus();
        window.ShowDialog();
        return result;
    }
}
