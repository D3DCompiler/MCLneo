using System.Windows;
using MCLneo.App.Services;
using MCLneo.Core.Updates;

namespace MCLneo.App.Views;

public partial class UpdateWindow : Window
{
    private readonly AppServices _services;
    private bool _busy;

    public UpdateWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        CheckButton.Focus();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CheckButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        NotesText.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在从 GitHub Releases 检查更新…";
        try
        {
            var update = await _services.Updates.CheckAsync(UpdateService.GitHubReleaseUrl);
            if (update is null)
            {
                StatusText.Text = "当前已是最新版本。";
                return;
            }

            StatusText.Text = $"发现新版本 {update.Version}，请前往 GitHub Releases 下载。";
            NotesText.Text = update.Notes ?? "";
            NotesText.Visibility = string.IsNullOrWhiteSpace(update.Notes)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
        catch (Exception ex)
        {
            StatusText.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
