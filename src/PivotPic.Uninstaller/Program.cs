using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using PivotPic.Shared;

namespace PivotPic.Uninstaller;

/// <summary>
/// 独立卸载程序。
/// 关键设计：先把自身复制到 %TEMP% 再从那里执行删除，避免"正在运行的文件删不掉"，
/// 这样才能把安装目录完整清空（包括 uninstall.exe 本身）。
/// </summary>
internal static class Program
{
    private const string AppName = "PivotPic";
    private const string AppId = "PivotPic";
    private const string RunValueName = "PivotPic";
    private const string WorkSwitch = "--work";
    private const string CleanupSwitch = "--cleanup";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_YESNO = 0x00000004;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_SETFOREGROUND = 0x00010000;
    private const int IDYES = 6;

    private static int Main(string[] args)
    {
        // 第二阶段：已经在临时目录里，执行真正的删除。
        if (args.Length >= 2 && string.Equals(args[0], CleanupSwitch, StringComparison.OrdinalIgnoreCase))
        {
            Cleanup(args[1]);
            return 0;
        }

        // 第一阶段：确认 → 复制自身到临时目录 → 交给临时副本执行。
        return ConfirmAndRelaunch();
    }

    private static int ConfirmAndRelaunch()
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(installDir))
        {
            Error("无法确定安装目录。");
            return 1;
        }

        var answer = MessageBoxW(
            IntPtr.Zero,
            $"确定要卸载 {AppName} 吗？\n\n将删除程序文件、桌面与开始菜单快捷方式、开机自启项。\n安装目录：{installDir}",
            $"卸载 {AppName}",
            MB_YESNO | MB_ICONWARNING | MB_SETFOREGROUND);

        if (answer != IDYES)
        {
            return 1;
        }

        try
        {
            StopRunningApp();

            // 复制到临时目录再执行，安装目录里的所有文件（含本程序）才能被彻底删除。
            var tempCopy = Path.Combine(
                Path.GetTempPath(),
                $"pivotpic-uninst-{Guid.NewGuid():N}.exe");

            File.Copy(Environment.ProcessPath!, tempCopy, overwrite: true);

            Process.Start(new ProcessStartInfo(tempCopy, $"{CleanupSwitch} \"{installDir}\"")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });

            return 0;
        }
        catch (Exception ex)
        {
            Error($"无法启动卸载：{ex.Message}");
            return 2;
        }
    }

    /// <summary>第二阶段：从临时目录清理安装痕迹，最后连自己一起删掉。</summary>
    private static void Cleanup(string installDir)
    {
        try
        {
            // 关键：进程的工作目录若仍指向安装目录，Windows 不允许删除它。
            try
            {
                Directory.SetCurrentDirectory(Path.GetTempPath());
            }
            catch (Exception)
            {
                // 切换失败也继续尝试清理。
            }

            StopRunningApp();

            // 内置加速组件的善后：结束进程 → 还原系统代理 → 删除工作目录。
            // 必须排在删除安装目录之前——PAC 快照就存在 proxy 目录里，
            // 目录先没了就再也还原不了系统代理，表现为「卸载后 GitHub 完全打不开」。
            ProxyCleanup.CleanupAll(installDir);

            RemoveShortcuts();
            RemoveAutoStart();
            RemoveRegistry();

            // 允许文件系统短暂释放句柄。
            Thread.Sleep(400);
            DeleteDirectoryWithRetry(installDir);
        }
        catch (Exception)
        {
            // 尽力而为，失败也要把自己清理掉。
        }
        finally
        {
            ScheduleSelfDelete();
        }
    }

    private static void StopRunningApp()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(AppName))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // 进程已退出或无权限时忽略。
                }
            }
        }
        catch (Exception)
        {
            // 枚举失败不影响后续清理。
        }
    }

    private static void RemoveShortcuts()
    {
        var uninstallLink = "卸载 " + AppName + ".lnk";

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

        foreach (var path in new[]
                 {
                     Path.Combine(desktop, AppName + ".lnk"),
                     Path.Combine(programs, AppName + ".lnk"),
                     Path.Combine(programs, uninstallLink),
                     Path.Combine(commonDesktop, AppName + ".lnk"),
                     Path.Combine(commonPrograms, AppName + ".lnk"),
                     Path.Combine(commonPrograms, uninstallLink)
                 })
        {
            TryDeleteFile(path);
        }
    }

    private static void RemoveAutoStart()
    {
        DeleteRunValue(Registry.CurrentUser);
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

    /// <summary>删除整个目录：先清文件，失败的重试几次，最后删目录。</summary>
    private static void DeleteDirectoryWithRetry(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(300);
            }
        }

        // 直接删不掉时逐个文件删除，跳过仍被占用的。
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                TryDeleteFile(file);
            }

            foreach (var dir in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    Directory.Delete(dir, recursive: false);
                }
                catch (Exception)
                {
                    // 忽略非空或占用的目录。
                }
            }

            Directory.Delete(directory, recursive: false);
        }
        catch (Exception)
        {
            // 仍有残留时交给系统重启后清理。
            ScheduleDeleteOnReboot(directory);
        }
    }

    private static void ScheduleDeleteOnReboot(string directory)
    {
        try
        {
            MoveFileEx(directory, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    /// <summary>删除临时目录里的自身副本。</summary>
    private static void ScheduleSelfDelete()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /f /q \"{self}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 被占用时忽略。
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

    private static void Error(string message)
    {
        MessageBoxW(IntPtr.Zero, message, $"卸载 {AppName}", 0x00000010 | MB_SETFOREGROUND);
    }

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
