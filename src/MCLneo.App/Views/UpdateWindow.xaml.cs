using System.Windows;
using MCLneo.App.Services;
using MCLneo.Core.Models;
using MCLneo.Core.Updates;

namespace MCLneo.App.Views;

public partial class UpdateWindow : Window
{
    private readonly AppServices _services;
    private UpdateInfo? _update;
    private bool _busy;

    public UpdateWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        UrlBox.Text = services.Settings.Current.UpdateManifestUrl;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        UrlBox.Focus();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText.Text = "请先填写更新清单 URL。MCLneo 不内置更新服务器，避免未经签名的远程更新。";
            return;
        }

        _busy = true;
        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Collapsed;
        StatusText.Text = "正在检查更新…";
        NotesText.Visibility = Visibility.Collapsed;
        try
        {
            _update = await _services.Updates.CheckAsync(url);
            if (_update is null)
            {
                StatusText.Text = "当前已是最新版本。";
                return;
            }

            StatusText.Text = $"发现新版本 {_update.Version}";
            NotesText.Text = _update.Notes ?? "";
            NotesText.Visibility = string.IsNullOrWhiteSpace(_update.Notes) ? Visibility.Collapsed : Visibility.Visible;
            InstallButton.IsEnabled = true;
            _services.Settings.Current.UpdateManifestUrl = url;
            await _services.Settings.SaveAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "检查失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _update is null) return;
        if (!MessageDialog.Confirm(this, "安装更新",
                $"将下载并安装 {_update.Version}，然后关闭 MCLneo。\n更新完成后会自动重新启动。",
                "下载并安装"))
        {
            return;
        }

        _busy = true;
        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                StatusText.Text = $"{p.Stage} · {p.Detail}";
                Progress.Value = p.Clamp();
            });
            var staged = await _services.Updates.DownloadAndStageAsync(_update, progress);
            var exe = Environment.ProcessPath
                      ?? throw new InvalidOperationException("无法定位启动器可执行文件");
            _services.Updates.LaunchInstaller(staged, exe);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
            MessageDialog.Show(this, "更新失败", ex.Message);
        }
        finally
        {
            _busy = false;
            InstallButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal static class InstallProgressExtensions
{
    public static double Clamp(this InstallProgress progress) =>
        Math.Clamp(progress.Fraction, 0, 1);
}
