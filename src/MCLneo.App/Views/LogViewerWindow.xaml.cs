using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MCLneo.App.Services;
using MCLneo.Core.Logs;
using MCLneo.Core.Models;

namespace MCLneo.App.Views;

public partial class LogViewerWindow : Window
{
    private readonly AppServices _services;
    private readonly GameInstance _instance;
    private IReadOnlyList<GameLogFile> _files = Array.Empty<GameLogFile>();

    public LogViewerWindow(AppServices services, GameInstance instance)
    {
        InitializeComponent();
        _services = services;
        _instance = instance;
        InstanceName.Text = instance.Name;
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
        _files = _services.GameLogs.List(_instance);
        FileList.ItemsSource = _files.Select(f => new
        {
            f.Name,
            f.Path,
            f.Modified,
            f.Size,
            f.IsCrash,
            DisplaySize = FormatSize(f.Size)
        }).ToList();
        var first = _files.FirstOrDefault(f => f.IsCrash) ?? _files.FirstOrDefault();
        if (first is not null)
        {
            FileList.SelectedIndex = _files.ToList().IndexOf(first);
        }
        else
        {
            ContentView.Text = "这个游戏还没有日志。启动一次游戏后会自动生成。";
        }
    }

    private async void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is null) return;
        var path = FileList.SelectedItem.GetType().GetProperty("Path")?.GetValue(FileList.SelectedItem) as string;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var text = await _services.GameLogs.ReadAsync(path);
            ContentView.Text = text;
            ContentView.ScrollToEnd();
            var findings = CrashAnalyzer.Analyze(text);
            if (findings.Count > 0)
            {
                AnalysisText.Text = string.Join("\n",
                    findings.Select(f => $"• [{f.Severity}] {f.Title}：{f.Detail}"));
                AnalysisPanel.Visibility = Visibility.Visible;
            }
            else
            {
                AnalysisText.Text = "";
                AnalysisPanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            ContentView.Text = "读取失败：" + ex.Message;
            AnalysisPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(_instance.Directory, "logs");
        try
        {
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法打开目录");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
