using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MCLneo.App.Views;

public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    public static void Show(Window? owner, string title, string message)
    {
        var dialog = Create(owner, title, message, "知道了", false, showCancel: false);
        dialog.ShowDialog();
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText,
        bool danger = false)
    {
        var dialog = Create(owner, title, message, confirmText, danger, showCancel: true);
        return dialog.ShowDialog() == true;
    }

    private static MessageDialog Create(Window? owner, string title, string message, string confirmText,
        bool danger, bool showCancel)
    {
        var dialog = new MessageDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        if (danger)
        {
            dialog.IconBadge.Background = (Brush)dialog.FindResource("DangerSoftBrush");
            dialog.IconGlyph.Foreground = (Brush)dialog.FindResource("DangerBrush");
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
        }

        return dialog;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && !HasButtonAncestor(e.OriginalSource as DependencyObject))
        {
            DragMove();
        }
    }

    private static bool HasButtonAncestor(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button) return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }
}
