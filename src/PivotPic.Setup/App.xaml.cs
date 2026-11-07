using System;
using System.Linq;
using System.Windows;

namespace PivotPic.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 在线更新拉起安装程序时，旧版本自己还没退干净。先把界面压住等它退出，
        // 否则用户会看到窗口先出现、进度条卡在 0% 然后报「文件被占用」。
        WaitForHostExit(e.Args);

        // 提权后的静默安装：不显示界面，装完用退出码反馈结果。
        var silent = e.Args.Any(arg => string.Equals(arg, "--silent-install", StringComparison.OrdinalIgnoreCase));
        if (silent)
        {
            Shutdown(RunSilentInstall(e.Args));
            return;
        }

        // 卸载模式：不显示安装向导，直接走确认界面。
        var uninstall = e.Args.Any(arg =>
            string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow(uninstall);
        MainWindow = window;
        window.Show();
    }

    /// <summary>解析 <c>--wait-pid {pid}</c> 并等待该进程退出；未提供时立即返回。</summary>
    private static void WaitForHostExit(string[] args)
    {
        var raw = ReadArg(args, "--wait-pid");
        if (!int.TryParse(raw, out var processId))
        {
            return;
        }

        Installer.WaitForProcessExit(processId);
    }

    private static int RunSilentInstall(string[] args)
    {
        try
        {
            var options = new InstallOptions(
                ReadArg(args, "--dir") ?? Installer.DefaultAllUsersDir,
                ReadFlag(args, "--desktop"),
                ReadFlag(args, "--autostart"),
                ReadFlag(args, "--allusers"));

            Installer.Install(options, _ => { });
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pivotpic-setup.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] SilentInstall: {ex}\r\n");
            }
            catch (Exception)
            {
                // 记录失败不影响返回码。
            }

            return 1;
        }
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1].Trim('"');
            }
        }

        return null;
    }

    private static bool ReadFlag(string[] args, string name)
    {
        var value = ReadArg(args, name);
        return value is "1" or "true" or "True";
    }
}
