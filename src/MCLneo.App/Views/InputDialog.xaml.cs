using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MCLneo.App.Views;

public partial class InputDialog : Window
{
    private Func<string, string?>? _validate;

    private InputDialog()
    {
        InitializeComponent();
    }

    public string? Result { get; private set; }

    public static string? Show(Window? owner, string title, string prompt, string initialValue = "",
        Func<string, string?>? validate = null)
    {
        var dialog = new InputDialog();
        dialog.TitleText.Text = title;
        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initialValue;
        dialog._validate = validate;
        if (owner is not null)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var value = InputBox.Text.Trim();
        var error = _validate?.Invoke(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            error ??= "内容不能为空";
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = value;
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
