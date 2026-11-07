using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

namespace PivotPic.Services;

/// <summary>
/// 自卸载：由主程序以 --uninstall 参数执行。
/// 好处是安装目录里不需要再放一份上百兆的安装包当"卸载程序"。
/// </summary>
internal static class SelfUninstaller
{
    private const string AppName = "PivotPic";
    private const string AppId = "PivotPic";
    private const string RunValueName = "PivotPic";

    /// <summary>确认并执行卸载，返回进程退出码。</summary>
    public static int Run()
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        var confirm = System.Windows.MessageBox.Show(
            $"确定要卸载 {AppName} 吗？\n\n将删除程序文件、桌面与开始菜单快捷方式、开机自启项。\n安装目录：{installDir}",
            $"卸载 {AppName}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
        {
            return 1;
        }

        try
        {
            RemoveShortcuts();
            RemoveAutoStart();
            RemoveRegistry();
            ScheduleFolderCleanup(installDir);
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"卸载过程中出现问题：{ex.Message}", $"卸载 {AppName}",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
    }

    private static void RemoveShortcuts()
    {
        var uninstallLink = "卸载 " + AppName + ".lnk";

        var targets = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), uninstallLink),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), uninstallLink)
        };

        foreach (var path in targets)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // 个别位置无权限时跳过，不影响其余清理。
            }
        }
    }

    private static void RemoveAutoStart()
    {
        DeleteRunValue(Registry.CurrentUser);

        // 所有用户安装时自启写在 HKLM，需要管理员权限才删得掉。
        if (IsElevated())
        {
            DeleteRunValue(Registry.LocalMachine);
        }
    }

    private static void DeleteRunValue(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // 无权限时忽略。
        }
    }

    private static void RemoveRegistry()
    {
        DeleteUninstallKey(Registry.CurrentUser);

        if (IsElevated())
        {
            DeleteUninstallKey(Registry.LocalMachine);
        }
    }

    private static void DeleteUninstallKey(RegistryKey root)
    {
        try
        {
            root.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId,
                throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 无权限时忽略。
        }
    }

    /// <summary>
    /// 先删掉除自身以外的所有文件，再交给 cmd 在进程退出后删除目录。
    /// 正在运行的可执行文件无法自删，这是 Windows 的硬限制。
    /// </summary>
    private static void ScheduleFolderCleanup(string installDir)
    {
        var self = Environment.ProcessPath;

        foreach (var file in Directory.EnumerateFiles(installDir))
        {
            if (self is not null
                && string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception)
            {
                // 被占用的文件留给后面的 rmdir 处理。
            }
        }

        var script = $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{installDir}\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", script)
            {
                CreateNoWindow = true,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });
        }
        catch (Exception)
        {
            // 目录残留不影响功能，用户可手动删除。
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
