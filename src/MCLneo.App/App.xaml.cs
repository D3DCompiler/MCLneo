using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace MCLneo.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\MCLneo.SingleInstance.2026-11-07-r3";
    private Services.AppServices? _services;
    private Services.AppState? _state;
    private static Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _singleInstanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        Services.LiquidGlass.Attach();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { _services?.Log.Error("未处理异常", args.ExceptionObject as Exception); } catch { }
        };

        try
        {
            _services = await Services.AppServices.CreateAsync();
            Services.ThemeService.Apply(_services.Settings.Current.ThemeMode,
                _services.Settings.Current.AccentTheme);
            _state = new Services.AppState(_services);
            await _state.InitializeAsync();
            var pageIndex = Array.IndexOf(e.Args, "--page");
            var initialPage = pageIndex >= 0 && pageIndex + 1 < e.Args.Length ? e.Args[pageIndex + 1] : null;
            var window = new MainWindow(_services, _state, initialPage);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("MCLneo 启动失败：\n" + ex.Message, "MCLneo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var handle = FindWindow(null, "MCLneo");
            if (handle == IntPtr.Zero) return;
            ShowWindowAsync(handle, 9);
            SetForegroundWindow(handle);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Error("界面线程未处理异常", e.Exception);
        MessageBox.Show("发生了一个错误：\n" + e.Exception.Message, "MCLneo", MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _state?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
