using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace MCLneo.App.Services;

public static class ElevationService
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Relaunches MCLneo elevated. Returns true when a new process was started.</summary>
    public static bool TryRestartElevated(string page, out string? error)
    {
        error = null;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            error = "无法定位启动器可执行文件";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (!string.IsNullOrWhiteSpace(page))
            {
                psi.ArgumentList.Add("--page");
                psi.ArgumentList.Add(page);
            }

            Process.Start(psi);
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
