using System.Windows;
using MCLneo.App.Services;

namespace MCLneo.App.Views;

public partial class AuthlibLoginWindow : Window
{
    private readonly AppState _state;
    private bool _busy;

    public AuthlibLoginWindow(AppState state)
    {
        InitializeComponent();
        _state = state;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        UsernameBox.Focus();
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var server = ServerBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(username) || password.Length == 0)
        {
            StatusText.Text = "请填写认证服务器、用户名和密码";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }

        _busy = true;
        LoginButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        StatusText.Text = "正在登录…";
        try
        {
            await _state.LoginAuthlibAsync(server, username, password);
            StatusText.Text = "登录成功";
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            StatusText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            LoginButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
