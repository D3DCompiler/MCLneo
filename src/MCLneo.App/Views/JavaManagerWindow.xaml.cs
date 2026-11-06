using System.Windows;
using System.Windows.Controls;
using MCLneo.App.Services;

namespace MCLneo.App.Views;

public partial class JavaManagerWindow : Window
{
    private readonly AppServices _services;

    public JavaManagerWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Topmost = true;
        Topmost = false;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        StatusText.Text = "正在扫描 Java…";
        Progress.Visibility = Visibility.Collapsed;
        try
        {
            var runtimes = await _services.Java.DiscoverAsync(_services.Settings.Current.JavaPath);
            RuntimeList.ItemsSource = runtimes;
            StatusText.Text = $"已检测到 {runtimes.Count} 个 Java 运行时";
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var major))
        {
            return;
        }

        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        StatusText.Text = $"正在下载 Java {major}…";
        try
        {
            var progress = new Progress<Core.Models.InstallProgress>(p =>
            {
                Progress.Value = p.Fraction;
                StatusText.Text = $"{p.Stage} · {p.Detail}";
            });
            var runtime = await _services.Java.InstallRuntimeAsync(major, progress);
            StatusText.Text = $"已安装 {runtime.Display}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "安装失败：" + ex.Message;
            MessageDialog.Show(this, "Java 安装失败", ex.Message);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Core.Minecraft.JavaRuntime runtime })
        {
            return;
        }

        if (!_services.Java.IsManagedRuntime(runtime))
        {
            MessageDialog.Show(this, "无法删除", "这个 Java 是系统或其他程序安装的，MCLneo 只会删除自己下载的运行时。");
            return;
        }

        if (!MessageDialog.Confirm(this, "删除 Java", $"确定删除 {runtime.Display} 吗？\n{runtime.ExecutablePath}",
                "删除", danger: true))
        {
            return;
        }

        try
        {
            await _services.Java.DeleteRuntimeAsync(runtime);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "删除失败", ex.Message);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
